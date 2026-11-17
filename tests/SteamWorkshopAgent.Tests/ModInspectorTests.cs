using System.Xml.Linq;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class ModInspectorTests
{
    [Fact]
    public async Task InspectAsync_Reads_RimWorld_Mod_Metadata()
    {
        using var fixture = TestModRepo.Create();
        var inspector = new ModInspector(new ProcessRunner());

        var result = await inspector.InspectAsync(fixture.Root);

        Assert.Equal("TestMod", result.ModFileName);
        Assert.Equal("Test Mod", result.ModName);
        Assert.Equal("brrainz.testmod", result.PackageId);
        Assert.Equal("1.2.3.4", result.ModVersion);
        Assert.Equal((ulong)123456789, result.PublishedFileId);
        Assert.Equal((uint)294100, result.RimWorldAppId);
        Assert.Contains("1.6", result.SupportedVersions);
        Assert.NotNull(result.ProjectPath);
        Assert.EndsWith("TestMod.csproj", result.ProjectPath);
        Assert.True(result.HasBuildProject);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=123456789", result.WorkshopUrl);
    }

    [Fact]
    public async Task InspectAsync_Picks_The_Source_Project_Among_Tests_Tools_And_Hidden_Probes()
    {
        using var fixture = TestModRepo.Create();
        File.Move(Path.Combine(fixture.Root, "Source", "TestMod.csproj"), Path.Combine(fixture.Root, "Source", "Renamed.csproj"));
        foreach (var extra in new[] { "Tests/Smoke/Smoke.csproj", "Tools/Meta/Meta.csproj", ".runs/probe/TestMod.csproj" })
        {
            var path = Path.Combine(fixture.Root, extra);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<Project />");
        }

        var result = await new ModInspector(new ProcessRunner()).InspectAsync(fixture.Root);

        Assert.Equal(Path.Combine(fixture.Root, "Source", "Renamed.csproj"), result.ProjectPath);
    }

    [Fact]
    public async Task InspectAsync_Reads_Deployed_Mod_Folder_Without_DirectoryBuildProps()
    {
        using var fixture = TestModRepo.CreateDeployed();
        var inspector = new ModInspector(new ProcessRunner());

        var result = await inspector.InspectAsync(fixture.Root);

        Assert.Equal(Path.GetFileName(fixture.Root), result.ModFileName);
        Assert.Equal("Deployed Test Mod", result.ModName);
        Assert.Equal("brrainz.deployedtestmod", result.PackageId);
        Assert.Equal("2.0.0", result.ModVersion);
        Assert.Null(result.ProjectPath);
        Assert.False(result.HasBuildProject);
        Assert.Contains("1.6", result.SupportedVersions);
        Assert.Equal(Path.Combine(fixture.Root, "About", "About.xml"), result.AboutXmlPath);
    }

    [Fact]
    public async Task InspectAsync_Does_Not_Read_Dependency_PackageId_As_Mod_PackageId()
    {
        using var fixture = TestModRepo.Create();
        var about = XDocument.Load(Path.Combine(fixture.Root, "About", "About.xml"));
        about.Root!.Element("modDependencies")!.AddFirst(new XElement("li", new XElement("packageId", "wrong.dependency")));
        about.Save(Path.Combine(fixture.Root, "About", "About.xml"));

        var inspector = new ModInspector(new ProcessRunner());
        var result = await inspector.InspectAsync(fixture.Root);

        Assert.Equal("brrainz.testmod", result.PackageId);
    }
}
