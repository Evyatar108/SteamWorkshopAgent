#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SteamWorkshopAgent;

// Atomic filesystem ownership, shared by standalone helpers and the companion.
// Only a persisted, proven pre-submit failure hands ownership back.
internal sealed class PublishRequestClaim : IDisposable
{
    private readonly FileStream stream;
    private readonly string path;

    private PublishRequestClaim(FileStream stream, string path)
    {
        this.stream = stream;
        this.path = path;
    }

    internal static string ClaimPath(string requestPath) => Path.GetFullPath(requestPath) + ".claim";

    internal static void ThrowIfExists(string requestPath)
    {
        if (File.Exists(ClaimPath(requestPath)))
            throw Refusal();
    }

    internal static PublishRequestClaim Acquire(string requestPath, string runsDirectory)
    {
        var fullPath = Path.GetFullPath(requestPath);
        var root = Path.GetFullPath(runsDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, AgentDataPath.PathComparison)
            || !string.Equals(Path.GetFileName(fullPath), "steamworks-request.json", AgentDataPath.PathComparison))
            throw new InvalidOperationException("Claims require a canonical steamworks-request.json under the agent runs directory.");
        ThrowIfExists(fullPath);
        // Do not let a request alias acquire a separate claim on the same payload.
        for (var path = fullPath; path.Length >= root.Length; path = Path.GetDirectoryName(path)!)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Publish request paths cannot contain symbolic links or junctions.");
        }
        FileStream claim;
        try
        {
            claim = new FileStream(ClaimPath(fullPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw Refusal(exception);
        }
        try
        {
            var metadata = Encoding.UTF8.GetBytes(
                $"pid={Process.GetCurrentProcess().Id}\ncreatedAtUtc={DateTimeOffset.UtcNow:O}\n");
            claim.Write(metadata, 0, metadata.Length);
            claim.Flush(flushToDisk: true);
            return new PublishRequestClaim(claim, ClaimPath(fullPath));
        }
        catch
        {
            claim.Dispose(); // The incomplete claim also fails closed.
            throw;
        }
    }

    internal async Task PersistResultAsync(
        string? stage, bool success, bool submissionStarted, bool outcomeDefinitive,
        Func<Task> persist)
    {
        if (!stream.CanWrite)
            throw new InvalidOperationException("This publish claim is no longer held.");
        await persist().ConfigureAwait(false);
        if (PublishReplayGuard.IsProvenPreSubmitFailure(stage, success, submissionStarted, outcomeDefinitive))
        {
            // Closing is necessary before deletion on Windows. Until deletion,
            // CreateNew still refuses competitors. Never delete again on Dispose.
            stream.Dispose();
            File.Delete(path);
        }
    }

    private static InvalidOperationException Refusal(Exception? inner = null) => new(
        "This publish request is already claimed or cannot be claimed safely. " +
        "Read back the Workshop item instead of submitting again. Claims never expire automatically.", inner);

    public void Dispose() => stream.Dispose(); // An incomplete/uncertain attempt keeps its claim.
}
