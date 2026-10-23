namespace SteamWorkshopAgent;

public static class AgentPaths
{
    public const uint RimWorldAppId = 294100;

    public static string AppSupportDirectory => AgentDataPath.GetAppSupportDirectory();

    public static string RunsDirectory => Path.Combine(AppSupportDirectory, "runs");

    public static string SteamworksDirectory => Path.Combine(AppSupportDirectory, "steamworks");

    public static string HeadlessProfileDirectory => Path.Combine(AppSupportDirectory, "rimworld-headless-profile");

    public static string NewRunDirectory(string modFileName, string tag)
    {
        var safeTag = MakeSafePathPart(tag);
        var safeMod = MakeSafePathPart(modFileName);
        return Path.Combine(RunsDirectory, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{safeMod}-{safeTag}");
    }

    public static string MakeSafePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray();
        return new string(chars).Trim('-');
    }
}
