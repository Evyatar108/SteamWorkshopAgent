using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class WorkshopPublisher(
    ProcessRunner processRunner,
    SteamEnvironment steamEnvironment,
    WorkshopPlanner workshopPlanner,
    WorkshopTagUpdater tagUpdater,
    GitReleaseWorktree releaseWorktree,
    WorkshopDescriptionReader descriptionReader,
    WorkshopPublishRequestStore requestStore,
    SteamworksPublisher steamworksPublisher)
{
    internal Func<string, Task<WorkshopDescriptionSnapshot>> ReadDescription { get; init; }
        = descriptionReader.GetDescriptionAsync;

    internal Func<string, Task<WorkshopPublishBackendResult>> SubmitPrepared { get; init; }
        = steamworksPublisher.PublishPreparedAsync;

    public async Task<object> PublishFolderAsync(
        string folder, ulong expectedId, string changeNoteFile, bool confirm = false, string backend = "auto",
        string? expectedDigest = null)
    {
        if (backend.Trim().ToLowerInvariant() is not ("auto" or "standalone"))
            throw new ArgumentException("Folder publishing supports only auto or standalone.", nameof(backend));
        if (confirm && expectedDigest == null)
            throw new ArgumentException("--expect-digest is required when confirming a folder publish. Use the contentDigest from the approved dry run.");
        var approvedDigest = expectedDigest == null
            ? null
            : WorkshopPublishRequestStore.ParseContentDigest(expectedDigest);
        var plan = await workshopPlanner.CreateFolderPlanAsync(folder, expectedId, changeNoteFile);
        if (!confirm)
            return new WorkshopFolderPlan(
                plan.Mod.ModName, expectedId,
                Directory.EnumerateFiles(plan.ContentFolder, "*", SearchOption.AllDirectories).Count(),
                await WorkshopPublishRequestStore.ComputeContentDigestAsync(plan.ContentFolder),
                new FileInfo(plan.PreviewFile).Length, plan.ChangeNote);

        await WorkshopPublishRequestStore.ComputeContentDigestAsync(plan.ContentFolder, approvedDigest);
        Directory.CreateDirectory(plan.RunDirectory);
        await File.WriteAllTextAsync(plan.VdfPath, plan.VdfContent);
        await File.WriteAllTextAsync(Path.Combine(plan.RunDirectory, "plan.json"),
            JsonSerializer.Serialize(plan, ToolJson.Options));
        return await PublishFinalPlanAsync(plan, backend, steamUser: null, expectedDigest: approvedDigest);
    }

    public async Task<object> PublishReleaseAsync(
        string repoPath,
        string tag,
        bool confirm,
        bool updateDescription = false,
        string? steamUser = null,
        string? changeNote = null,
        string backend = "auto")
    {
        if (!confirm)
            return await workshopPlanner.CreateReleasePlanAsync(repoPath, tag, updateDescription, changeNote: changeNote);

        var initialPlan = await workshopPlanner.CreateReleasePlanAsync(repoPath, tag, updateDescription, changeNote: changeNote);
        Directory.CreateDirectory(initialPlan.RunDirectory);
        Directory.CreateDirectory(initialPlan.StagingRoot);

        await ThrowIfDirtyAsync(initialPlan.Mod.RepoPath);

        await using var buildSource = await releaseWorktree.CreateAsync(
            initialPlan.Mod.RepoPath,
            tag,
            initialPlan.RunDirectory);

        var buildPlan = await workshopPlanner.CreateReleasePlanAsync(
            buildSource.RepoPath,
            tag,
            updateDescription,
            initialPlan.RunDirectory,
            changeNote: changeNote);

        await BuildReleaseAsync(buildPlan.Mod, buildPlan.StagingRoot);

        var finalPlan = await workshopPlanner.CreateReleasePlanAsync(
            buildSource.RepoPath,
            tag,
            updateDescription,
            initialPlan.RunDirectory,
            buildPlan.ContentFolder,
            changeNote);

        Validation.ThrowIfErrors(finalPlan.ValidationIssues);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPlan.VdfPath)!);
        await File.WriteAllTextAsync(finalPlan.VdfPath, finalPlan.VdfContent);
        await File.WriteAllTextAsync(
            Path.Combine(finalPlan.RunDirectory, "plan.json"),
            JsonSerializer.Serialize(finalPlan, ToolJson.Options));

        return await PublishFinalPlanAsync(finalPlan, backend, steamUser);
    }

    public async Task<object> PublishDeployedReleaseAsync(
        string repoPath,
        string tag,
        string contentFolder,
        bool confirm,
        bool updateDescription = false,
        string? steamUser = null,
        string? changeNote = null,
        string backend = "auto")
    {
        var resolvedContentFolder = Path.GetFullPath(contentFolder);
        if (!confirm)
            return await workshopPlanner.CreateReleasePlanAsync(
                repoPath,
                tag,
                updateDescription,
                contentFolderOverride: resolvedContentFolder,
                changeNote: changeNote);

        await ThrowIfDirtyAsync(repoPath);

        var finalPlan = await workshopPlanner.CreateReleasePlanAsync(
            repoPath,
            tag,
            updateDescription,
            contentFolderOverride: resolvedContentFolder,
            changeNote: changeNote);

        Directory.CreateDirectory(finalPlan.RunDirectory);
        Validation.ThrowIfErrors(finalPlan.ValidationIssues);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPlan.VdfPath)!);
        await File.WriteAllTextAsync(finalPlan.VdfPath, finalPlan.VdfContent);
        await File.WriteAllTextAsync(
            Path.Combine(finalPlan.RunDirectory, "plan.json"),
            JsonSerializer.Serialize(finalPlan, ToolJson.Options));

        return await PublishFinalPlanAsync(finalPlan, backend, steamUser);
    }

    public async Task<object> CreateNewModAsync(
        string repoPath,
        bool confirm,
        string? steamUser = null,
        string visibility = "private",
        string changeNote = "Initial upload")
    {
        if (!confirm)
            return await workshopPlanner.CreateNewModPlanAsync(repoPath, visibility, changeNote);

        var initialPlan = await workshopPlanner.CreateNewModPlanAsync(repoPath, visibility, changeNote);
        Validation.ThrowIfErrors(initialPlan.ValidationIssues);
        Directory.CreateDirectory(initialPlan.RunDirectory);
        Directory.CreateDirectory(initialPlan.StagingRoot);

        var finalPlan = initialPlan;
        if (initialPlan.Mod.HasBuildProject)
        {
            await BuildReleaseAsync(initialPlan.Mod, initialPlan.StagingRoot);

            finalPlan = await workshopPlanner.CreateNewModPlanAsync(
                repoPath,
                visibility,
                changeNote,
                initialPlan.RunDirectory,
                initialPlan.ContentFolder);
        }

        Validation.ThrowIfErrors(finalPlan.ValidationIssues);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPlan.VdfPath)!);
        await File.WriteAllTextAsync(finalPlan.VdfPath, finalPlan.VdfContent);
        await File.WriteAllTextAsync(
            Path.Combine(finalPlan.RunDirectory, "plan.json"),
            JsonSerializer.Serialize(finalPlan, ToolJson.Options));

        var steamCmdPath = steamEnvironment.RequireSteamCmd();
        var resolvedSteamUser = await steamEnvironment.RequireSteamUserAsync(steamUser);
        var steamResult = await processRunner.RunAsync(
            steamCmdPath,
            ["+login", resolvedSteamUser, "+workshop_build_item", finalPlan.VdfPath, "+quit"],
            workingDirectory: finalPlan.RunDirectory,
            timeout: TimeSpan.FromMinutes(15));

        var publishedFileId = TryReadPublishedFileId(finalPlan.VdfPath);
        var publishedFileIdPath = GetPublishedFileIdPath(finalPlan.Mod);
        var success = steamResult.ExitCode == 0 && publishedFileId.GetValueOrDefault() != 0;
        if (success)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(publishedFileIdPath)!);
            await File.WriteAllTextAsync(publishedFileIdPath, publishedFileId!.Value.ToString());
        }

        var logTails = steamEnvironment.GetWorkshopLogPaths()
            .Where(File.Exists)
            .Select(path => TailFile(path, 80))
            .ToList();
        var tagUpdate = success && publishedFileId is { } createdFileId
            ? await tagUpdater.SetTagsAsync(createdFileId, finalPlan.Tags, confirm: true, changeNote: "Set Workshop tags")
            : null;

        return new CreateNewModResult(
            success,
            steamResult.ExitCode,
            publishedFileId,
            publishedFileIdPath,
            publishedFileId.GetValueOrDefault() != 0
                ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId!.Value}"
                : "",
            finalPlan.RunDirectory,
            finalPlan.VdfPath,
            finalPlan.ContentFolder,
            Truncate(steamResult.Stdout, 12000),
            Truncate(steamResult.Stderr, 12000),
            logTails,
            tagUpdate,
            finalPlan);
    }

    private async Task ThrowIfDirtyAsync(string repoPath)
    {
        var status = await processRunner.RunAsync(
            "git",
            ["-C", repoPath, "status", "--short", "--untracked-files=all"],
            timeout: TimeSpan.FromSeconds(10));

        if (status.ExitCode != 0)
            throw new InvalidOperationException($"Failed to inspect git status for {repoPath}.\nSTDOUT:\n{status.Stdout}\nSTDERR:\n{status.Stderr}");

        if (!string.IsNullOrWhiteSpace(status.Stdout))
            throw new InvalidOperationException("Refusing to publish from a dirty worktree. Commit or stash local changes first.");
    }

    private async Task<object> PublishFinalPlanAsync(
        WorkshopReleasePlan finalPlan,
        string backend,
        string? steamUser,
        string? expectedDigest = null)
    {
        var normalizedBackend = backend.Trim().ToLowerInvariant();
        if (normalizedBackend is not ("auto" or "standalone" or "steamcmd"))
            throw new ArgumentException("Publish backend must be auto, standalone, or steamcmd.", nameof(backend));

        if (normalizedBackend == "steamcmd")
            return await PublishWithSteamCmdAsync(finalPlan, steamUser);

        var publishedFileId = finalPlan.Mod.PublishedFileId
            ?? throw new InvalidOperationException("A Workshop published file id is required.");
        var current = await ReadDescription(publishedFileId.ToString());
        if (current.Result != 1 || !ulong.TryParse(current.Creator, out var creatorSteamId) || creatorSteamId == 0)
            throw new InvalidOperationException("Steam did not return a verifiable creator account for the Workshop item.");

        var preparation = await requestStore.CreateAsync(finalPlan, creatorSteamId, expectedDigest);
        var result = await SubmitPrepared(preparation.RequestPath);
        return result with { Plan = finalPlan };
    }

    private async Task<PublishResult> PublishWithSteamCmdAsync(
        WorkshopReleasePlan finalPlan,
        string? steamUser)
    {
        var steamCmdPath = steamEnvironment.RequireSteamCmd();
        var resolvedSteamUser = await steamEnvironment.RequireSteamUserAsync(steamUser);
        var steamResult = await processRunner.RunAsync(
            steamCmdPath,
            ["+login", resolvedSteamUser, "+workshop_build_item", finalPlan.VdfPath, "+quit"],
            workingDirectory: finalPlan.RunDirectory,
            timeout: TimeSpan.FromMinutes(15));

        var logTails = steamEnvironment.GetWorkshopLogPaths()
            .Where(File.Exists)
            .Select(path => TailFile(path, 80))
            .ToList();

        return new PublishResult(
            steamResult.ExitCode == 0,
            steamResult.ExitCode,
            finalPlan.Mod.WorkshopUrl,
            finalPlan.RunDirectory,
            finalPlan.VdfPath,
            finalPlan.ContentFolder,
            Truncate(steamResult.Stdout, 12000),
            Truncate(steamResult.Stderr, 12000),
            logTails,
            TagUpdate: null,
            finalPlan);
    }

    private async Task BuildReleaseAsync(ModInspection mod, string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(mod.ProjectPath))
            throw new InvalidOperationException("This mod path does not include a build project. Use the existing folder as Workshop content instead of building it.");

        var arguments = CreateBuildReleaseArguments(mod.ProjectPath, stagingRoot);
        var buildResult = await processRunner.RunAsync(
            "dotnet",
            arguments,
            workingDirectory: mod.RepoPath,
            timeout: TimeSpan.FromMinutes(5));

        if (buildResult.ExitCode != 0)
            throw new InvalidOperationException($"Release build failed with exit code {buildResult.ExitCode}.\nSTDOUT:\n{buildResult.Stdout}\nSTDERR:\n{buildResult.Stderr}");
    }

    internal static IReadOnlyList<string> CreateBuildReleaseArguments(
        string projectPath,
        string stagingRoot)
    {
        return [
            "build",
            projectPath,
            "-c",
            "Release",
            $"-p:RIMWORLD_MOD_DIR={stagingRoot}",
            "-p:BuildBridgeTools=false"
        ];
    }

    private static ulong? TryReadPublishedFileId(string vdfPath)
    {
        if (!File.Exists(vdfPath))
            return null;

        var raw = VdfWriter.ReadWorkshopItemField(File.ReadAllText(vdfPath), "publishedfileid");
        return ulong.TryParse(raw, out var parsed) && parsed != 0 ? parsed : null;
    }

    private static string GetPublishedFileIdPath(ModInspection mod)
    {
        return mod.PublishedFileIdPath ?? Path.Combine(mod.RepoPath, "About", "PublishedFileId.txt");
    }

    private static string TailFile(string path, int maxLines)
    {
        var lines = File.ReadLines(path).TakeLast(maxLines);
        return $"==> {path}\n" + string.Join('\n', lines);
    }

    private static string Truncate(string value, int maxChars)
    {
        return value.Length <= maxChars
            ? value
            : value[^maxChars..];
    }
}
