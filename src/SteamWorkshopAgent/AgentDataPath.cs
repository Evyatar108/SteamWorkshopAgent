#nullable enable
using System;
using System.IO;

namespace SteamWorkshopAgent;

// Linked into the in-game companion so both processes accept the same run root.
internal static class AgentDataPath
{
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string GetAppSupportDirectory() => Resolve(
        OperatingSystem.IsWindows(),
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
