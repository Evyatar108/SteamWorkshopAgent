namespace SteamWorkshopAgent.Tests;

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Exercises Unix shell startup files and chmod; not a Windows feature.";
    }
}
