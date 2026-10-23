#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SteamWorkshopAgent;

// Shared with the companion: a prepared upload may only consume run-owned files.
internal static class PublishSnapshotPaths
{
    internal static void Validate(string requestPath, string contentFolder, string previewFile)
    {
        var snapshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "snapshot");
        if (!string.Equals(Path.GetFullPath(contentFolder),
                Path.Combine(snapshot, "content"), AgentDataPath.PathComparison)
            || !Path.GetFullPath(previewFile).StartsWith(
                snapshot + Path.DirectorySeparatorChar, AgentDataPath.PathComparison))
            throw new InvalidOperationException("Prepared uploads must use the request's verified snapshot, not live content paths.");
        EnsureNoLinks(snapshot);
    }

    internal static void EnsureNoLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Upload snapshots cannot contain symbolic links or junctions.");
        if (!Directory.Exists(path))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            EnsureNoLinks(entry);
    }

    internal static string FileDigest(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
    }
}
