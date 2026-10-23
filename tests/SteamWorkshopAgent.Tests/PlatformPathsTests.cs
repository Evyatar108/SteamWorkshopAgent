using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class PlatformPathsTests
{
    [Fact]
    public void AppData_Uses_Platform_Root_And_Explicit_Override()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var local = Path.Combine(Path.GetTempPath(), "local");
        var custom = Path.Combine(Path.GetTempPath(), "custom");
        Assert.Equal(Path.Combine(local, "SteamWorkshopAgent"), AgentDataPath.Resolve(true, home, local, null));
        Assert.Equal(Path.Combine(home, "Library", "Application Support", "SteamWorkshopAgent"),
            AgentDataPath.Resolve(false, home, local, null));
        Assert.Equal(custom, AgentDataPath.Resolve(true, home, local, custom));
        Assert.Equal(custom, AgentDataPath.Resolve(false, home, local, custom));
    }

    [Fact]
    public void FindOnPath_Honors_PathExt_Order_And_Explicit_Extension()
    {
        using var fixture = TestModRepo.CreateDeployed();
        File.WriteAllText(Path.Combine(fixture.Root, "tool.EXE"), "");
        File.WriteAllText(Path.Combine(fixture.Root, "tool.CMD"), "");
        Assert.EndsWith("tool.CMD", ProcessRunner.FindOnPath("tool", fixture.Root, ".CMD;.EXE", windows: true));
        Assert.EndsWith("tool.EXE", ProcessRunner.FindOnPath("tool.EXE", fixture.Root, ".CMD", windows: true));
        Assert.Null(ProcessRunner.FindOnPath("tool", fixture.Root, ".CMD;.EXE", windows: false));
    }

    [Theory]
    [InlineData(true, "steam_api64.dll")]
    [InlineData(true, "RimWorldWin64_Data/Plugins/x86_64/steam_api64.dll")]
    [InlineData(true, "RimWorldWin64_Data/Plugins/steam_api64.dll")]
    [InlineData(false, "RimWorld.app/Contents/MacOS/libsteam_api.dylib")]
    [InlineData(false, "RimWorldMac.app/Contents/PlugIns/steam_api.bundle/Contents/MacOS/libsteam_api.dylib")]
    public void Discovery_Finds_Manifest_And_Library_In_Secondary_Steam_Library(bool windows, string relativeDll)
    {
        using var fixture = TestModRepo.CreateDeployed();
        var steamRoot = Path.Combine(fixture.Root, "Steam");
        var otherLibrary = Path.Combine(fixture.Root, "Secondary Library");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        Directory.CreateDirectory(Path.Combine(otherLibrary, "steamapps"));
        var escaped = otherLibrary.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steamRoot.Replace("\\", "\\\\")}}" }
                "1"
                {
                    "path" "{{escaped}}"
                    "apps" { "294100" "1234" }
                }
            }
            """);
        var manifest = Path.Combine(otherLibrary, "steamapps", "appmanifest_294100.acf");
        File.WriteAllText(manifest, "\"AppState\"\n{\n\"installdir\" \"RimWorld Custom\"\n}\n");
        var native = Path.Combine(otherLibrary, "steamapps", "common", "RimWorld Custom",
            relativeDll.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllBytes(native, []); // Discovery only: never load this fake library.

        Assert.Equal(manifest, SteamEnvironment.FindManifest(steamRoot));
        Assert.Equal(native, SteamEnvironment.FindNativeLibrary(steamRoot, windows));
        Assert.Null(SteamEnvironment.FindNativeLibrary(steamRoot, !windows));
        Assert.Equal([steamRoot, otherLibrary], SteamEnvironment.ParseLibraryPaths(steamRoot,
            $"\"0\" \"{escaped}\"")); // legacy libraryfolders.vdf
    }
}
