#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SteamWorkshopAgent;

// Atomic filesystem ownership, shared by standalone helpers and the companion.
// A closed or crashed attempt deliberately leaves its claim behind.
internal sealed class PublishRequestClaim : IDisposable
{
    private readonly FileStream stream;

    private PublishRequestClaim(FileStream stream) => this.stream = stream;

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
            return new PublishRequestClaim(claim);
        }
        catch
        {
            claim.Dispose(); // The incomplete claim also fails closed.
            throw;
        }
    }

    private static InvalidOperationException Refusal(Exception? inner = null) => new(
        "This publish request is already claimed or cannot be claimed safely. " +
        "Read back the Workshop item instead of submitting again. Claims never expire automatically.", inner);

    public void Dispose() => stream.Dispose(); // Never delete the durable claim.
}
