#nullable enable
using System;
using System.IO;

namespace SteamWorkshopAgent;

// Shared with the net472 companion so both processes accept the same run root.
internal static class AgentDataPath
{
    internal static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;
    internal static StringComparison PathComparison =>
        IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string GetAppSupportDirectory() => Resolve(
        IsWindows,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DATA_ROOT"));

    internal static string Resolve(bool windows, string home, string localAppData, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return windows
            ? Path.Combine(localAppData, "SteamWorkshopAgent")
            : Path.Combine(home, "Library", "Application Support", "SteamWorkshopAgent");
    }
}
