using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class WorkshopPublishRequestStore(ProcessRunner processRunner)
{
    public async Task<WorkshopPublishPreparation> CreateAsync(
        WorkshopReleasePlan plan,
        ulong expectedCreatorSteamId)
    {
        if (plan.Mod.PublishedFileId is not { } publishedFileId || publishedFileId == 0)
            throw new InvalidOperationException("A nonzero Workshop published file id is required.");
        if (expectedCreatorSteamId == 0)
            throw new InvalidOperationException("The current Workshop item creator could not be verified.");

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
            Path.GetFullPath(plan.ContentFolder),
            Path.GetFullPath(plan.PreviewFile),
            plan.Mod.ModName,
            plan.UpdateDescription ? plan.Mod.Description ?? "" : null,
            plan.UpdateDescription,
            PreserveTags: true,
            Visibility: null,
            plan.ChangeNote,
            plan.Release.TagName,
            await ResolveCommitAsync(plan.Mod.RepoPath, plan.Release.TagName),
            await ComputeContentDigestAsync(plan.ContentFolder),
            resultPath);

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
            ContentFolder: Path.GetFullPath(contentFolder),
            PreviewFile: Path.GetFullPath(previewFile),
            Title: "SteamWorkshopAgent Private Validation",
            Description: "Private validation item for the local SteamWorkshopAgent release pipeline.",
            UpdateDescription: true,
            PreserveTags: true,
            Visibility: 2,
            ChangeNote: "Private SteamWorkshopAgent validation",
            SourceTag: "private-validation",
            SourceCommit: "not-applicable",
            ContentDigest: await ComputeContentDigestAsync(contentFolder),
            ResultPath: resultPath);
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

        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The Workshop publish request has expired. Create a fresh release plan.");
        if (!Directory.Exists(request.ContentFolder))
            throw new DirectoryNotFoundException($"Workshop content folder does not exist: {request.ContentFolder}");
        if (!File.Exists(request.PreviewFile))
            throw new FileNotFoundException("Workshop preview file does not exist.", request.PreviewFile);

        var digest = await ComputeContentDigestAsync(request.ContentFolder);
        if (!string.Equals(digest, request.ContentDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Workshop content changed after the publish request was prepared.");

        return request;
    }

    public async Task<WorkshopPublishRequest> ReadForVerificationAsync(string requestPath)
    {
        var fullPath = Path.GetFullPath(requestPath);
        var runsRoot = Path.GetFullPath(AgentPaths.RunsDirectory) + Path.DirectorySeparatorChar;
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

    internal static async Task<string> ComputeContentDigestAsync(string contentFolder)
    {
        var root = Path.GetFullPath(contentFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Workshop content folder does not exist: {root}");

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);

            await using var stream = File.OpenRead(path);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
                aggregate.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
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
