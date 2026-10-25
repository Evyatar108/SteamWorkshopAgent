using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamWorkshopAgent;

public sealed record AdditionalPreview(uint Index, string Name, string UrlOrVideoId, int Type);
public sealed record MediaFile(string Name, string Path, string Sha256);
public sealed record MediaAction(string Kind, string Name, uint? Index);
public sealed record MediaState(ulong ItemId, ulong Creator, int Visibility, IReadOnlyList<AdditionalPreview> Previews);
public sealed record MediaPlan(MediaState Before, int? Visibility, IReadOnlyList<MediaFile> Files,
    IReadOnlyList<MediaAction> Actions, string Digest);
internal sealed record MediaRequest(MediaPlan Plan, DateTimeOffset Expires);
internal sealed record MediaReceipt(string Stage, bool Success, bool SubmissionStarted, bool OutcomeDefinitive);

// Pure planning is separate from Steam, files, claims and the CLI.
public static class WorkshopMediaPlan
{
    public static MediaPlan Create(MediaState before, IReadOnlyList<MediaFile> files, int? visibility = null)
    {
        if (before.ItemId == 0 || before.Creator == 0 || before.Visibility is < 0 or > 3)
            throw new ArgumentException("Item, creator and current visibility must be verified.");
        if (visibility is < 0 or > 3)
            throw new ArgumentException("Invalid visibility.");
        if (before.Previews.Select(p => p.Index).Distinct().Count() != before.Previews.Count)
            throw new ArgumentException("Duplicate remote preview index.");
        var images = before.Previews.Where(p => p.Type == 0).ToArray();
        if (images.Any(p => string.IsNullOrWhiteSpace(p.Name))
            || images.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length)
            throw new ArgumentException("Remote image names must be present and unique; reconcile manually.");
        if (files.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new ArgumentException("Duplicate local image name.");
        var actions = new List<MediaAction>();
        if (visibility is null)
        {
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var match = images.SingleOrDefault(p => p.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
                actions.Add(new(match is null ? "add" : "replace", file.Name, match?.Index));
            }
            actions.AddRange(images.Where(p => !files.Any(f => f.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.Index).Select(p => new MediaAction("remove", p.Name, p.Index)));
        }
        else if (visibility != before.Visibility)
            actions.Add(new("visibility", $"{before.Visibility} -> {visibility}", null));
        var plan = new MediaPlan(before, visibility, files, actions, "");
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Before = before, Visibility = visibility,
                Files = files.Select(f => new { f.Name, f.Sha256 }), Actions = actions }, ToolJson.Options)))
            .ToLowerInvariant();
        return plan with { Digest = digest };
    }

    public static IReadOnlyList<MediaFile> ReadFolder(string folder)
    {
        var root = Path.GetFullPath(folder);
        PublishSnapshotPaths.EnsureNoLinks(root);
        var paths = Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length is < 1 or > 10)
            throw new ArgumentException("Gallery house policy requires 1..10 static PNG/JPG images.");
        return paths.Select(path =>
        {
            PublishSnapshotPaths.EnsureNoLinks(path);
            var name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, @"^[a-z0-9][a-z0-9_-]*\.(png|jpg|jpeg)$")
                || !File.Exists(path) || new FileInfo(path).Length is <= 0 or >= 1_000_000)
                throw new ArgumentException($"Invalid gallery file {name}; run workshop_media.py check first.");
            var bytes = File.ReadAllBytes(path);
            var png = bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
            var jpg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            if (!(name.EndsWith(".png") ? png : jpg))
                throw new ArgumentException($"Image signature disagrees with extension: {name}");
            return new MediaFile(name, path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }).ToArray();
    }
}

internal interface IWorkshopMediaClient
{
    Task<IReadOnlyList<AdditionalPreview>> QueryAsync(ulong item);
    ulong Start(ulong item);
    bool Add(ulong handle, string path);
    bool Replace(ulong handle, uint index, string path);
    bool Remove(ulong handle, uint index);
    bool Visibility(ulong handle, int visibility);
    Task SubmitAsync(ulong handle);
}

internal sealed class WorkshopMediaService(WorkshopPublishRequestStore store)
{
    internal async Task ExecuteAsync(string requestPath, IWorkshopMediaClient client, ulong steamId)
    {
        using var claim = PublishRequestClaim.Acquire(requestPath, store.RunsDirectory);
        var request = JsonSerializer.Deserialize<MediaRequest>(await File.ReadAllTextAsync(requestPath), ToolJson.Options)
            ?? throw new InvalidOperationException("Unreadable media request.");
        var resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "media-result.json");
        if (File.Exists(resultPath))
        {
            var old = JsonSerializer.Deserialize<MediaReceipt>(await File.ReadAllTextAsync(resultPath), ToolJson.Options);
            PublishReplayGuard.EnsureCanSubmit(old?.Stage, old?.Success ?? false,
                old?.SubmissionStarted ?? true, old?.OutcomeDefinitive ?? false);
        }
        var plan = request.Plan;
        if (request.Expires <= DateTimeOffset.UtcNow || steamId != plan.Before.Creator)
            throw new InvalidOperationException("Media request expired or Steam account is not the item creator.");
        // Query indexes and URLs again, even for visibility-only changes. Fail before StartItemUpdate.
        var previews = await client.QueryAsync(plan.Before.ItemId);
        if (JsonSerializer.Serialize(previews, ToolJson.Options)
            != JsonSerializer.Serialize(plan.Before.Previews, ToolJson.Options))
            throw new InvalidOperationException("Remote previews changed; make and approve a new plan.");
        foreach (var file in plan.Files)
        {
            if (Path.GetFullPath(file.Path) != Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "snapshot", file.Name))
                throw new InvalidOperationException("Media requests must use run-owned snapshot files.");
            PublishSnapshotPaths.EnsureNoLinks(file.Path);
            if (PublishSnapshotPaths.FileDigest(file.Path) != file.Sha256)
                throw new InvalidOperationException("Gallery bytes changed; make and approve a new plan.");
        }
        // Re-derive actions instead of trusting a hand-edited request's remove list.
        var derived = WorkshopMediaPlan.Create(plan.Before, plan.Files, plan.Visibility);
        if (derived.Digest != plan.Digest
            || JsonSerializer.Serialize(derived.Actions, ToolJson.Options) != JsonSerializer.Serialize(plan.Actions, ToolJson.Options))
            throw new InvalidOperationException("Media plan digest is invalid.");
        var handle = client.Start(plan.Before.ItemId);
        if (handle is 0 or ulong.MaxValue)
            throw new InvalidOperationException("Invalid update handle; no submission started.");
        // Replacements use original indexes; removals are descending. Videos are untouched.
        foreach (var action in plan.Actions.Where(a => a.Kind == "replace"))
            Require(client.Replace(handle, action.Index!.Value, plan.Files.Single(f => f.Name == action.Name).Path));
        foreach (var action in plan.Actions.Where(a => a.Kind == "remove").OrderByDescending(a => a.Index))
            Require(client.Remove(handle, action.Index!.Value));
        foreach (var action in plan.Actions.Where(a => a.Kind == "add"))
            Require(client.Add(handle, plan.Files.Single(f => f.Name == action.Name).Path));
        if (plan.Visibility is { } visibility)
            Require(client.Visibility(handle, visibility));
        await WorkshopPublishRequestStore.WriteOwnerOnlyJsonAsync(resultPath,
            new MediaReceipt("submit-intent", false, false, false));
        await client.SubmitAsync(handle);
        await claim.PersistResultAsync("completed", true, true, true,
            () => WorkshopPublishRequestStore.WriteOwnerOnlyJsonAsync(resultPath,
                new MediaReceipt("completed", true, true, true)));
        // Any exception leaves the claim, including pre-submit exceptions. No automatic retry.
    }

    private static void Require(bool ok)
    {
        if (!ok) throw new InvalidOperationException("Steam rejected a media setter; no submission started.");
    }
}
