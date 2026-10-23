using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class WorkshopPublishRequestStore(ProcessRunner processRunner)
{
    private readonly string? runsDirectoryOverride;

    internal WorkshopPublishRequestStore(ProcessRunner processRunner, string runsDirectory)
        : this(processRunner)
    {
        runsDirectoryOverride = runsDirectory;
    }

    public async Task<WorkshopPublishPreparation> CreateAsync(
        WorkshopReleasePlan plan,
        ulong expectedCreatorSteamId,
        string? expectedDigest = null)
    {
        if (plan.Mod.PublishedFileId is not { } publishedFileId || publishedFileId == 0)
            throw new InvalidOperationException("A nonzero Workshop published file id is required.");
        if (expectedCreatorSteamId == 0)
            throw new InvalidOperationException("The current Workshop item creator could not be verified.");

        var approvedDigest = await ComputeContentDigestAsync(plan.ContentFolder, expectedDigest);
        var snapshot = await CreateSnapshotAsync(
            plan.RunDirectory, plan.ContentFolder, plan.PreviewFile, approvedDigest);
        Directory.CreateDirectory(plan.RunDirectory);
        var requestId = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(plan.RunDirectory, "steamworks-request.json");
        var resultPath = Path.Combine(plan.RunDirectory, "steamworks-result.json");
        var request = new WorkshopPublishRequest(
            SchemaVersion: 1,
            requestId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            AgentPaths.RimWorldAppId,
            publishedFileId,
            expectedCreatorSteamId,
            snapshot.ContentFolder,
            snapshot.PreviewFile,
            plan.Mod.ModName,
            plan.UpdateDescription ? plan.Mod.Description ?? "" : null,
            plan.UpdateDescription,
            PreserveTags: true,
            Visibility: null,
            plan.ChangeNote,
            plan.Release?.TagName ?? "not-applicable",
            plan.Release is { } release
                ? await ResolveCommitAsync(plan.Mod.RepoPath, release.TagName)
                : "not-applicable",
            approvedDigest,
            resultPath)
        {
            PreviewDigest = PublishSnapshotPaths.FileDigest(snapshot.PreviewFile),
            ContentDigestFormat = ContentFingerprint.Format
        };

        await WriteOwnerOnlyJsonAsync(requestPath, request);
        return new WorkshopPublishPreparation(
            "steamworks-standalone",
            requestPath,
            resultPath,
            request.ContentDigest,
            request,
            plan);
    }

    public async Task<WorkshopPublishPreparation> CreatePrivateValidationAsync(
        ulong publishedFileId,
        ulong creatorSteamId,
        string contentFolder,
        string previewFile)
    {
        if (publishedFileId == 0 || creatorSteamId == 0)
            throw new InvalidOperationException("The private validation item and creator ids must be nonzero.");

        var runDirectory = AgentPaths.NewRunDirectory("SteamWorkshopAgent", "private-validation");
        Directory.CreateDirectory(runDirectory);
        var digest = await ComputeContentDigestAsync(contentFolder);
        var snapshot = await CreateSnapshotAsync(runDirectory, contentFolder, previewFile, digest);
        var requestPath = Path.Combine(runDirectory, "steamworks-request.json");
        var resultPath = Path.Combine(runDirectory, "steamworks-result.json");
        var request = new WorkshopPublishRequest(
            SchemaVersion: 1,
            RequestId: Guid.NewGuid().ToString("N"),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            AppId: AgentPaths.RimWorldAppId,
            publishedFileId,
            ExpectedCreatorSteamId: creatorSteamId,
            ContentFolder: snapshot.ContentFolder,
            PreviewFile: snapshot.PreviewFile,
            Title: "SteamWorkshopAgent Private Validation",
            Description: "Private validation item for the local SteamWorkshopAgent release pipeline.",
            UpdateDescription: true,
            PreserveTags: true,
            Visibility: 2,
            ChangeNote: "Private SteamWorkshopAgent validation",
            SourceTag: "private-validation",
            SourceCommit: "not-applicable",
            ContentDigest: digest,
            ResultPath: resultPath)
        {
            PreviewDigest = PublishSnapshotPaths.FileDigest(snapshot.PreviewFile),
            ContentDigestFormat = ContentFingerprint.Format
        };
        await WriteOwnerOnlyJsonAsync(requestPath, request);
        return new WorkshopPublishPreparation(
            "steamworks-standalone",
            requestPath,
            resultPath,
            request.ContentDigest,
            request,
            Plan: null);
    }

    public async Task<WorkshopPublishRequest> ReadAndValidateAsync(string requestPath)
    {
        var request = await ReadForVerificationAsync(requestPath);
        ContentFingerprint.RequireFormat(request.ContentDigestFormat);

        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The Workshop publish request has expired. Create a fresh release plan.");
        if (!Directory.Exists(request.ContentFolder))
            throw new DirectoryNotFoundException($"Workshop content folder does not exist: {request.ContentFolder}");
        if (!File.Exists(request.PreviewFile))
            throw new FileNotFoundException("Workshop preview file does not exist.", request.PreviewFile);
        PublishSnapshotPaths.Validate(requestPath, request.ContentFolder, request.PreviewFile);

        var digest = await ComputeContentDigestAsync(request.ContentFolder);
        if (!string.Equals(digest, request.ContentDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Workshop content changed after the publish request was prepared.");
        if (!string.Equals(PublishSnapshotPaths.FileDigest(request.PreviewFile),
                request.PreviewDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Workshop preview changed after the publish request was prepared.");

        return request;
    }

    private async Task<(string ContentFolder, string PreviewFile)> CreateSnapshotAsync(
        string runDirectory, string contentFolder, string previewFile, string approvedDigest)
    {
        var source = Path.GetFullPath(contentFolder);
        var run = Path.GetFullPath(runDirectory);
        var runsRoot = Path.GetFullPath(runsDirectoryOverride ?? AgentPaths.RunsDirectory)
            + Path.DirectorySeparatorChar;
        if (!run.StartsWith(runsRoot, AgentDataPath.PathComparison)
            || run.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar,
                AgentDataPath.PathComparison))
            throw new InvalidOperationException("Upload snapshots require a run directory under the agent data root, outside the content folder.");
        PublishSnapshotPaths.EnsureNoLinks(source);
        var snapshotRoot = Path.Combine(run, "snapshot");
        if (Directory.Exists(snapshotRoot))
            throw new InvalidOperationException("This run already has an upload snapshot. Prepare a new request.");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(snapshotRoot);
        else
            Directory.CreateDirectory(snapshotRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var snapshotContent = Path.Combine(snapshotRoot, "content");
        Directory.CreateDirectory(snapshotContent);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(snapshotContent, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
        var relativePreview = Path.GetRelativePath(source, Path.GetFullPath(previewFile));
        string snapshotPreview;
        if (relativePreview != ".." && !relativePreview.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePreview))
            snapshotPreview = Path.Combine(snapshotContent, relativePreview);
        else
        {
            PublishSnapshotPaths.EnsureNoLinks(previewFile);
            snapshotPreview = Path.Combine(snapshotRoot, "preview.png");
            File.Copy(previewFile, snapshotPreview, overwrite: false);
        }
        await ComputeContentDigestAsync(snapshotContent, approvedDigest);
        foreach (var file in Directory.EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
        return (snapshotContent, snapshotPreview);
    }

    public async Task<WorkshopPublishRequest> ReadForVerificationAsync(string requestPath)
    {
        var fullPath = Path.GetFullPath(requestPath);
        var runsRoot = Path.GetFullPath(runsDirectoryOverride ?? AgentPaths.RunsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(runsRoot, AgentDataPath.PathComparison))
            throw new InvalidOperationException("Workshop publish requests must be stored under the SteamWorkshopAgent runs directory.");

        var request = JsonSerializer.Deserialize<WorkshopPublishRequest>(
            await File.ReadAllTextAsync(fullPath),
            ToolJson.Options)
            ?? throw new InvalidOperationException("The Workshop publish request is invalid JSON.");
        if (request.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported Workshop publish request schema: {request.SchemaVersion}.");
        if (request.AppId != AgentPaths.RimWorldAppId)
            throw new InvalidOperationException($"Refusing to use a request for unexpected Steam app id {request.AppId}.");
        if (request.PublishedFileId == 0 || request.ExpectedCreatorSteamId == 0)
            throw new InvalidOperationException("The Workshop item and expected creator ids must be nonzero.");
        if (!Path.GetFullPath(request.ResultPath).StartsWith(runsRoot, AgentDataPath.PathComparison))
            throw new InvalidOperationException("The Workshop result path must be stored under the SteamWorkshopAgent runs directory.");
        return request;
    }

    public async Task WriteResultAsync(string path, WorkshopPublishBackendResult result)
    {
        await WriteOwnerOnlyJsonAsync(path, result);
    }

    internal static string ParseContentDigest(string value)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("--expect-digest must be a 64-character SHA-256 hexadecimal digest from the approved dry run.");
        return value.ToLowerInvariant();
    }

    internal static async Task<string> ComputeContentDigestAsync(
        string contentFolder, string? expectedDigest = null)
    {
        var approvedDigest = expectedDigest == null ? null : ParseContentDigest(expectedDigest);
        var root = Path.GetFullPath(contentFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Workshop content folder does not exist: {root}");

        var digest = await ContentFingerprint.ComputeAsync(root);
        if (approvedDigest != null && !string.Equals(digest, approvedDigest, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Workshop content does not match the approved digest. Run publish-folder without --confirm and review the new plan.");
        return approvedDigest ?? digest;
    }

    private async Task<string> ResolveCommitAsync(string repoPath, string tag)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["-C", repoPath, "rev-parse", $"{tag}^{{commit}}"],
            timeout: TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"Could not resolve release tag {tag} to a commit.");
        return result.Stdout.Trim();
    }

    private static async Task WriteOwnerOnlyJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, ToolJson.Options));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
