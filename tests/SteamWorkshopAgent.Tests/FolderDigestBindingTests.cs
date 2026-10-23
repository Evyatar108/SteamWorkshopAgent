using System.Text.Json;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

[CollectionDefinition("Folder digest binding", DisableParallelization = true)]
public sealed class FolderDigestBindingCollection;

[Collection("Folder digest binding")]
public sealed class FolderDigestBindingTests : IDisposable
{
    private readonly TestModRepo mod = TestModRepo.CreateDeployed(includePublishedFileId: true);
    private readonly string dataRoot;
    private readonly string? previousDataRoot;
    private int detailsCalls;
    private int publishCalls;
    private WorkshopPublishRequest? publishedRequest;

    public FolderDigestBindingTests()
    {
        dataRoot = mod.Root + "-agent-data";
        previousDataRoot = Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DATA_ROOT");
        Environment.SetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DATA_ROOT", dataRoot);
    }

    [Fact]
    public async Task Changed_Content_After_DryRun_Is_Refused_Before_Details_Or_Native_Access()
    {
        var publisher = Publisher();
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));
        await File.WriteAllBytesAsync(Path.Combine(mod.Root, "1.6", "Assemblies", "DeployedTestMod.dll"), [9, 8, 7]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishFolderAsync(mod.Root, 123456789, note,
                confirm: true, expectedDigest: plan.ContentDigest));

        Assert.Contains("approved digest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, detailsCalls);
        Assert.Equal(0, publishCalls);
        AssertNoRequest();
    }

    [Fact]
    public async Task Content_Changed_During_Details_Is_Refused_At_Request_Creation()
    {
        var publisher = Publisher(() => File.WriteAllBytes(
            Path.Combine(mod.Root, "1.6", "Assemblies", "DeployedTestMod.dll"), [9, 8, 7]));
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishFolderAsync(mod.Root, 123456789, note,
                confirm: true, expectedDigest: plan.ContentDigest));

        Assert.Contains("approved digest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, detailsCalls);
        Assert.Equal(0, publishCalls);
        AssertNoRequest();
    }

    [Fact]
    public async Task Confirm_Publishes_Prepared_Request_With_Approved_Digest()
    {
        var publisher = Publisher();
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));

        await publisher.PublishFolderAsync(mod.Root, 123456789, note,
            confirm: true, expectedDigest: plan.ContentDigest);

        Assert.Equal(1, detailsCalls);
        Assert.Equal(1, publishCalls);
        Assert.NotNull(publishedRequest);
        Assert.Equal(plan.ContentDigest, publishedRequest.ContentDigest);
    }

    [Fact]
    public async Task Submission_Uses_Verified_ReadOnly_Snapshot_When_Original_Changes()
    {
        var originalAssembly = Path.Combine(mod.Root, "1.6", "Assemblies", "DeployedTestMod.dll");
        var publisher = Publisher(beforeSubmit: _ => File.WriteAllBytes(originalAssembly, [9, 8, 7]));
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));

        await publisher.PublishFolderAsync(mod.Root, 123456789, note,
            confirm: true, expectedDigest: plan.ContentDigest);

        Assert.Equal(1, publishCalls);
        Assert.NotEqual(mod.Root, publishedRequest!.ContentFolder);
        Assert.StartsWith(dataRoot, publishedRequest.ContentFolder);
        Assert.StartsWith(publishedRequest.ContentFolder, publishedRequest.PreviewFile);
        Assert.Equal(plan.ContentDigest,
            await WorkshopPublishRequestStore.ComputeContentDigestAsync(publishedRequest.ContentFolder));
        Assert.All(Directory.EnumerateFiles(publishedRequest.ContentFolder, "*", SearchOption.AllDirectories),
            file => Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(originalAssembly));
    }

    [Fact]
    public async Task Tampered_Snapshot_Is_Refused_By_Helper_Validation()
    {
        var publisher = Publisher(beforeSubmit: request =>
        {
            Assert.NotEqual(mod.Root, request.ContentFolder);
            var file = Path.Combine(request.ContentFolder, "1.6", "Assemblies", "DeployedTestMod.dll");
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllBytes(file, [9, 8, 7]);
        });
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishFolderAsync(mod.Root, 123456789, note,
                confirm: true, expectedDigest: plan.ContentDigest));
        Assert.Contains("content changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("a17d5d4c3b19ba2c20e8808a97990b6453de78c9ade4147bb51d8ff838c2e36")]
    [InlineData("g17d5d4c3b19ba2c20e8808a97990b6453de78c9ade4147bb51d8ff838c2e367")]
    public async Task Confirm_Requires_Well_Formed_Approved_Digest(string? digest)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Publisher().PublishFolderAsync(mod.Root, 123456789, Note(),
                confirm: true, expectedDigest: digest));
        Assert.Equal(0, detailsCalls);
        Assert.Equal(0, publishCalls);
        AssertNoRequest();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    public async Task Cli_Confirm_Rejects_Missing_Or_Malformed_Digest(string? digest)
    {
        var args = new List<string>
        {
            typeof(Program).Assembly.Location, "publish-folder", mod.Root,
            "--expect-id", "123456789", "--changenote-file", Note(), "--confirm"
        };
        if (digest != null)
            args.AddRange(["--expect-digest", digest]);
        var result = await new ProcessRunner().RunAsync("dotnet", args,
            environment: new Dictionary<string, string>
            {
                ["STEAM_WORKSHOP_AGENT_DATA_ROOT"] = dataRoot
            });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--expect-digest", result.Stderr);
        AssertNoRequest();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    public async Task Mcp_Confirm_Rejects_Missing_Or_Malformed_Digest(string? digest)
    {
        ServiceLocator.SetProvider(new PublisherServices(Publisher()));
        try
        {
            using var result = JsonDocument.Parse(await WorkshopTools.WorkshopPublishFolder(
                mod.Root, 123456789, Note(), confirm: true, expectDigest: digest));
            Assert.Equal("error", result.RootElement.GetProperty("status").GetString());
            Assert.Contains("--expect-digest", result.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, detailsCalls);
            Assert.Equal(0, publishCalls);
            AssertNoRequest();
        }
        finally
        {
            ServiceLocator.SetProvider(new PublisherServices(null));
        }
    }

    [Fact]
    public async Task Mcp_Confirm_Forwards_Approved_Digest_To_Prepared_Request()
    {
        var publisher = Publisher();
        var note = Note();
        var plan = Assert.IsType<WorkshopFolderPlan>(
            await publisher.PublishFolderAsync(mod.Root, 123456789, note));
        ServiceLocator.SetProvider(new PublisherServices(publisher));
        try
        {
            using var result = JsonDocument.Parse(await WorkshopTools.WorkshopPublishFolder(
                mod.Root, 123456789, note, confirm: true, expectDigest: plan.ContentDigest));
            Assert.Equal("ok", result.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, detailsCalls);
            Assert.Equal(1, publishCalls);
            Assert.Equal(plan.ContentDigest, publishedRequest!.ContentDigest);
        }
        finally
        {
            ServiceLocator.SetProvider(new PublisherServices(null));
        }
    }

    private sealed class PublisherServices(WorkshopPublisher? publisher) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(WorkshopPublisher) ? publisher : null;
    }

    private WorkshopPublisher Publisher(
        Action? duringDetails = null, Action<WorkshopPublishRequest>? beforeSubmit = null)
    {
        var runner = new ProcessRunner();
        var environment = new SteamEnvironment(runner);
        var inspector = new ModInspector(runner);
        var store = new WorkshopPublishRequestStore(runner);
        var nativePublisher = new SteamworksPublisher(environment, runner, store);
        var details = new WorkshopDescriptionReader(new WorkshopTargetResolver(inspector));
        return new WorkshopPublisher(runner, environment,
            new WorkshopPlanner(inspector, new GitHubReleaseReader(runner)),
            new WorkshopTagUpdater(environment, inspector, runner), new GitReleaseWorktree(runner),
            details, store, nativePublisher)
        {
            ReadDescription = id =>
            {
                detailsCalls++;
                duringDetails?.Invoke();
                const string json = """{"response":{"publishedfiledetails":[{"result":1,"creator":"42","consumer_app_id":294100}]}}""";
                return Task.FromResult(WorkshopDescriptionReader.ParseSnapshot(json,
                    new WorkshopDescriptionTarget(ulong.Parse(id), null, null)));
            },
            SubmitPrepared = async path =>
            {
                publishCalls++;
                beforeSubmit?.Invoke(await store.ReadForVerificationAsync(path));
                publishedRequest = await store.ReadAndValidateAsync(path);
                return new WorkshopPublishBackendResult(
                    "fake-publisher", "completed", true, true, true, false, false, false,
                    null, null, publishedRequest.PublishedFileId, "OK", false, null, 0, 0, 0,
                    path, publishedRequest.ResultPath, "", "Faked without native access.");
            }
        };
    }

    private string Note()
    {
        var path = Path.Combine(mod.Root, "note.txt");
        File.WriteAllText(path, "Exact update note");
        return path;
    }

    private void AssertNoRequest() => Assert.Empty(Directory.Exists(dataRoot)
        ? Directory.EnumerateFiles(dataRoot, "steamworks-request.json", SearchOption.AllDirectories)
        : []);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DATA_ROOT", previousDataRoot);
        mod.Dispose();
        if (Directory.Exists(dataRoot))
        {
            foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
