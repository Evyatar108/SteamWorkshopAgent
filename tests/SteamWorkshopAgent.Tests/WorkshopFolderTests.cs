using System.Text.Json;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class WorkshopFolderTests
{
    private static WorkshopPlanner Planner() =>
        new(new ModInspector(new ProcessRunner()), new GitHubReleaseReader(new ProcessRunner()));

    [Fact]
    public async Task FolderPlan_Allows_XmlOnly_Mods_Without_Relaxing_Build_Validation()
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        Directory.Delete(Path.Combine(mod.Root, "1.6", "Assemblies"), recursive: true);
        Directory.CreateDirectory(Path.Combine(mod.Root, "Defs"));
        File.WriteAllText(Path.Combine(mod.Root, "Defs", "Things.xml"), "<Defs />");
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, "XML-only update");

        var plan = await Planner().CreateFolderPlanAsync(mod.Root, 123456789, note);

        Assert.DoesNotContain(plan.ValidationIssues, issue => issue.Code == "staged_missing_assembly");
        Assert.Contains(Validation.ValidateForWorkshop(plan.Mod, plan.PreviewFile, plan.ContentFolder),
            issue => issue.Code == "staged_missing_assembly" && issue.Severity == "error");
    }

    [Fact]
    public async Task FolderPlan_Uses_About_Title_And_Exact_Note_Without_Source_Metadata()
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        // Folder publishing must not parse source props, resolve Git or read GitHub.
        File.WriteAllText(Path.Combine(mod.Root, "Directory.Build.props"), "not XML");
        var notePath = Path.Combine(mod.Root, "note.txt");
        const string note = "  [b]2.1.0[/b]\r\nExact text.\n\n";
        await File.WriteAllTextAsync(notePath, note);
        var before = await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root);

        var plan = await Planner().CreateFolderPlanAsync(mod.Root, 123456789, notePath);

        Assert.Equal("Deployed Test Mod", plan.Mod.ModName);
        Assert.Equal(note, plan.ChangeNote);
        Assert.Equal(mod.Root, plan.ContentFolder);
        Assert.Null(plan.Release);
        Assert.False(plan.UpdateDescription);
        Assert.True(plan.TagsPreserved);
        Assert.DoesNotContain("\"description\"", plan.VdfContent);
        Assert.DoesNotContain("\"visibility\"", plan.VdfContent);
        Assert.DoesNotContain("\"tags\"", plan.VdfContent);
        Assert.Equal(before, await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root));
        Assert.False(Directory.Exists(plan.RunDirectory));

        // An explicit temporary run directory keeps all test writes isolated.
        using var runs = TestModRepo.CreateDeployed();
        var preparation = await new WorkshopPublishRequestStore(new ProcessRunner(), runs.Root)
            .CreateAsync(plan with { RunDirectory = Path.Combine(runs.Root, "run") }, 42);
        Assert.Equal(note, preparation.Request.ChangeNote);
        Assert.Equal("not-applicable", preparation.Request.SourceCommit);
        Assert.Null(preparation.Request.Description);
        Assert.Null(preparation.Request.Visibility);
        Assert.True(preparation.Request.PreserveTags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("legacy-unframed")]
    [InlineData("sha256-manifest-v2")]
    public async Task Validation_Refuses_Unsupported_Digest_Format(string? format)
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        using var runs = TestModRepo.CreateDeployed();
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, "Update");
        var plan = await Planner().CreateFolderPlanAsync(mod.Root, 123456789, note);
        var store = new WorkshopPublishRequestStore(new ProcessRunner(), runs.Root);
        var preparation = await store.CreateAsync(
            plan with { RunDirectory = Path.Combine(runs.Root, "run") }, 42);
        await File.WriteAllTextAsync(preparation.RequestPath, JsonSerializer.Serialize(
            preparation.Request with { ContentDigestFormat = format }, ToolJson.Options));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAndValidateAsync(preparation.RequestPath));

        Assert.Contains("digest format", error.Message);
        Assert.Equal(preparation.Request.PublishedFileId,
            (await store.ReadForVerificationAsync(preparation.RequestPath)).PublishedFileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("987654321")]
    public async Task FolderPlan_Refuses_Missing_Zero_Or_Wrong_Id(string? id)
    {
        using var mod = TestModRepo.CreateDeployed();
        if (id != null)
            File.WriteAllText(Path.Combine(mod.Root, "About", "PublishedFileId.txt"), id);
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, "Update");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Planner().CreateFolderPlanAsync(mod.Root, 123456789, note));
    }

    [Fact]
    public async Task FolderPlan_Refuses_Missing_About_And_Empty_Note()
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, " \r\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Planner().CreateFolderPlanAsync(mod.Root, 123456789, note));
        File.Delete(Path.Combine(mod.Root, "About", "About.xml"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Planner().CreateFolderPlanAsync(mod.Root, 123456789, note));
    }

    [Fact]
    public async Task Cli_Refuses_Data_Root_Inside_Content_Before_Confirmed_Publishing()
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, "Update");
        var before = await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root);
        var result = await new ProcessRunner().RunAsync("dotnet",
            [typeof(Program).Assembly.Location, "publish-folder", mod.Root,
                "--expect-id", "123456789", "--changenote-file", note, "--confirm", "--expect-digest", before],
            environment: new Dictionary<string, string>
            {
                ["STEAM_WORKSHOP_AGENT_DATA_ROOT"] = mod.Root
            });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("data root must be outside", result.Stderr);
        Assert.Equal(before, await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root));
    }

    [Fact]
    public async Task Cli_DryRun_Prints_Only_Plan_And_Writes_Nothing()
    {
        using var mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var note = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(note, "[b]2.1.0[/b]\r\nExact text.\n");
        var dataRoot = mod.Root + "-app-data";
        var before = await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root);
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("dotnet",
            [typeof(Program).Assembly.Location, "publish-folder", mod.Root,
                "--expect-id", "123456789", "--changenote-file", note],
            environment: new Dictionary<string, string>
            {
                ["STEAM_WORKSHOP_AGENT_DATA_ROOT"] = dataRoot,
                ["STEAMWORKS_NATIVE_LIB"] = Path.Combine(mod.Root, "must-not-load.dll")
            });

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        var data = json.RootElement.GetProperty("data");
        // Two-way key snapshot: each field is requested plan content for approval.
        Assert.Equal(
            ["changeNote", "contentDigest", "fileCount", "previewBytes", "publishedFileId", "title"],
            data.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(File.ReadAllText(note), data.GetProperty("changeNote").GetString());
        Assert.Equal(before, data.GetProperty("contentDigest").GetString());
        Assert.Equal("Deployed Test Mod", data.GetProperty("title").GetString());
        Assert.Equal(123456789UL, data.GetProperty("publishedFileId").GetUInt64());
        Assert.Equal(6, data.GetProperty("fileCount").GetInt32());
        Assert.Equal(128, data.GetProperty("previewBytes").GetInt64());
        Assert.True(result.Stdout.Length < 512); // Bound incidental JSON overhead.
        Assert.False(Directory.Exists(dataRoot));
        Assert.Equal(before, await WorkshopPublishRequestStore.ComputeContentDigestAsync(mod.Root));

        var refusal = await runner.RunAsync("dotnet",
            [typeof(Program).Assembly.Location, "publish-folder", mod.Root,
                "--expect-id", "1", "--changenote-file", note]);
        Assert.Equal(1, refusal.ExitCode);
        using var error = JsonDocument.Parse(refusal.Stderr);
        // Refusal readers: status -> branch; message -> recovery; details -> error type.
        Assert.Equal(["details", "message", "status"],
            error.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Contains("expected item", error.RootElement.GetProperty("message").GetString());
        Assert.True(refusal.Stderr.Length < 512);
    }
}
