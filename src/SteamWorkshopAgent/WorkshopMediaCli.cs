using System.Reflection;
using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed record WorkshopMediaOutput(string Digest, IReadOnlyList<MediaAction> Actions, string Status);

internal sealed class WorkshopMediaCli
{
    private readonly ProcessRunner runner = new();
    private readonly WorkshopPublishRequestStore store;
    private readonly SteamEnvironment environment;
    private readonly Func<ulong, Task<MediaState>>? readState;
    private readonly Func<string, Task<WorkshopMediaOutput>>? submit;

    internal WorkshopMediaCli()
    {
        store = new WorkshopPublishRequestStore(runner);
        environment = new SteamEnvironment(runner);
    }

    // Tests substitute boundaries only, not a product "fake Steam" flag.
    internal WorkshopMediaCli(WorkshopPublishRequestStore store, Func<ulong, Task<MediaState>> readState,
        Func<string, Task<WorkshopMediaOutput>> submit) : this()
    {
        this.store = store;
        this.readState = readState;
        this.submit = submit;
    }

    internal async Task<WorkshopMediaOutput> RunAsync(ulong item, string? folder, int? visibility,
        bool confirm, string? expectedDigest)
    {
        if (item == 0) throw new ArgumentException("A nonzero Workshop item id is required.");
        if (confirm && (expectedDigest is null
            || !System.Text.RegularExpressions.Regex.IsMatch(expectedDigest, "^[a-f0-9]{64}$")))
            throw new ArgumentException("Confirm requires a full lowercase --expect-digest from the approved plan.");
        // No unthrottled fallback. Parent details and child UGC lookups each take a machine-wide turn.
        var files = folder is null ? [] : WorkshopMediaPlan.ReadFolder(folder);
        var before = await (readState?.Invoke(item) ?? ReadStateAsync(item));
        var plan = WorkshopMediaPlan.Create(before, files, visibility);
        if (!confirm)
            return new(plan.Digest, plan.Actions, "Plan only. Approve these actions, then repeat with --confirm --expect-digest.");
        if (string.IsNullOrWhiteSpace(expectedDigest) || expectedDigest != plan.Digest)
            throw new InvalidOperationException("Media plan changed or --expect-digest missing. Review a fresh plan.");
        if (plan.Actions.Count == 0)
            return new(plan.Digest, plan.Actions, "Already matches; no submission.");
        var requestPath = await PrepareAsync(plan, files);
        return await (submit?.Invoke(requestPath) ?? HelperAsync<WorkshopMediaOutput>(["media-submit-internal", requestPath]));
    }

    private async Task<MediaState> ReadStateAsync(ulong item)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DETAILS_COMMAND")))
            throw new InvalidOperationException("Set STEAM_WORKSHOP_AGENT_DETAILS_COMMAND to steam_common.py details.");
        var reader = new WorkshopDescriptionReader(new WorkshopTargetResolver(new ModInspector(runner)));
        var details = await reader.GetDescriptionAsync(item.ToString());
        if (details.Result != 1 || details.ConsumerAppId != AgentPaths.RimWorldAppId
            || !ulong.TryParse(details.Creator, out var creator) || creator == 0 || details.Visibility is null)
            throw new InvalidOperationException("Cannot verify item creator, app and visibility.");
        var previews = await HelperAsync<IReadOnlyList<AdditionalPreview>>(["media-query-internal", item.ToString()]);
        return new(item, creator, details.Visibility.Value, previews);
    }

    private async Task<string> PrepareAsync(MediaPlan plan, IReadOnlyList<MediaFile> files)
    {
        var run = Path.Combine(store.RunsDirectory, "media-" + plan.Digest);
        var requestPath = Path.Combine(run, "steamworks-request.json");
        PublishRequestClaim.ThrowIfExists(requestPath);
        // Complete an isolated payload, then atomically name it. An interrupted
        // preparation never leaves a half-filled canonical request.
        if (!File.Exists(requestPath))
        {
            Directory.CreateDirectory(store.RunsDirectory);
            var staging = Path.Combine(store.RunsDirectory, ".media-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var snapshot = Path.Combine(staging, "snapshot");
                Directory.CreateDirectory(snapshot);
                var owned = new List<MediaFile>();
                foreach (var file in files)
                {
                    var bytes = await File.ReadAllBytesAsync(file.Path);
                    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != file.Sha256)
                        throw new InvalidOperationException("Gallery changed during snapshot.");
                    var snapshotFile = Path.Combine(snapshot, file.Name);
                    using (var stream = new FileStream(snapshotFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        await stream.WriteAsync(bytes);
                        stream.Flush(true);
                    }
                    File.SetAttributes(snapshotFile, File.GetAttributes(snapshotFile) | FileAttributes.ReadOnly);
                    owned.Add(file with { Path = Path.Combine(run, "snapshot", file.Name) });
                }
                await WorkshopPublishRequestStore.WriteOwnerOnlyJsonAsync(
                    Path.Combine(staging, "steamworks-request.json"),
                    new MediaRequest(plan with { Files = owned }, DateTimeOffset.UtcNow.AddMinutes(30)));
                try { Directory.Move(staging, run); }
                catch (IOException) when (File.Exists(requestPath)) { /* Another preparer won; claim arbitrates. */ }
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        return requestPath;
    }

    internal async Task<object> InternalAsync(string command, string value, string library)
    {
        if (!ProcessIsolation.TryDetachFromControllingTerminal(out var why))
            throw new InvalidOperationException("Cannot detach media helper: " + why);
        Environment.SetEnvironmentVariable("SteamAppId", AgentPaths.RimWorldAppId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", AgentPaths.RimWorldAppId.ToString());
        using var steam = new SteamworksNativeClient(library);
        if (!steam.Init()) throw new InvalidOperationException("SteamAPI_Init failed.");
        try
        {
            if (!steam.UserLoggedOn() || steam.GetAppId() != AgentPaths.RimWorldAppId)
                throw new InvalidOperationException("Steam must be online and authenticated as RimWorld.");
            if (command == "media-query-internal")
                return await steam.QueryPreviewsAsync(ulong.Parse(value));
            await new WorkshopMediaService(store).ExecuteAsync(value, new NativeMediaClient(steam), steam.GetSteamId());
            var request = JsonSerializer.Deserialize<MediaRequest>(await File.ReadAllTextAsync(value), ToolJson.Options)!;
            return new WorkshopMediaOutput(request.Plan.Digest, request.Plan.Actions,
                "Submitted. Read back the private page before approving public visibility.");
        }
        finally { steam.Shutdown(); }
    }

    private async Task<T> HelperAsync<T>(string[] args)
    {
        var prefix = WorkshopDescriptionReader.ParseDetailsCommand(
            Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_MEDIA_COMMAND")
            ?? throw new InvalidOperationException("Set STEAM_WORKSHOP_AGENT_MEDIA_COMMAND to [python, steam_common.py, run]."));
        var library = environment.FindSteamworksNativeLibrary()
            ?? throw new InvalidOperationException("Steamworks native library not found.");
        var process = Environment.ProcessPath!;
        var child = process.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
            || process.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { process, Assembly.GetExecutingAssembly().Location } : new[] { process };
        var result = await runner.RunAsync(prefix[0],
            prefix.Skip(1).Concat(child).Concat(args).Append(library).ToArray(), timeout: TimeSpan.FromMinutes(20));
        if (result.ExitCode != 0 || result.TimedOut || result.StdoutTruncated)
            throw new InvalidOperationException("Media helper failed; do not retry submission. " + result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        if (json.RootElement.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException("Media helper refused.");
        return json.RootElement.GetProperty("data").Deserialize<T>(ToolJson.Options)
            ?? throw new InvalidOperationException("Unreadable media helper output.");
    }
}
