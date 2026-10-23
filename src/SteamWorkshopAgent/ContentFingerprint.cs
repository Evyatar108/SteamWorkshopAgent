#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SteamWorkshopAgent;

// One manifest implementation for the main process and the net472 companion.
internal static class ContentFingerprint
{
    internal const string Format = "sha256-manifest-v1";

    internal static void RequireFormat(string? format)
    {
        if (!string.Equals(format, Format, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported content digest format. Prepare a new request with the current agent.");
    }

    internal static async Task<string> ComputeAsync(string contentFolder)
    {
        var root = Path.GetFullPath(contentFolder);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Relative = path.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/')
            })
            .OrderBy(file => file.Relative, StringComparer.Ordinal);
        using (var manifest = SHA256.Create())
        {
            var header = Encoding.UTF8.GetBytes("SteamWorkshopAgent content manifest v1\0");
            manifest.TransformBlock(header, 0, header.Length, header, 0);
            foreach (var file in files)
            {
                using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                           FileShare.Read, 128 * 1024, useAsync: true))
                using (var fileHash = SHA256.Create())
                using (var record = new MemoryStream())
                using (var writer = new BinaryWriter(record, Encoding.UTF8, leaveOpen: true))
                {
                    long length = 0;
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        fileHash.TransformBlock(buffer, 0, read, buffer, 0);
                        length = checked(length + read);
                    }
                    fileHash.TransformFinalBlock(new byte[0], 0, 0);
                    var path = Encoding.UTF8.GetBytes(file.Relative);
                    // BinaryWriter integers are little-endian. Hashes are raw 32 bytes.
                    writer.Write(path.Length);
                    writer.Write(path);
                    writer.Write(length);
                    writer.Write(fileHash.Hash!);
                    writer.Flush();
                    var bytes = record.ToArray();
                    manifest.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                }
            }
            manifest.TransformFinalBlock(new byte[0], 0, 0);
            return string.Concat(manifest.Hash!.Select(value => value.ToString("x2")));
        }
    }
}
