using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class TestModRepo : IDisposable
{
    public string Root { get; }

    public string PreviewPath => Path.Combine(Root, "About", "Preview.png");

    private TestModRepo(string root)
    {
        Root = root;
    }

    public static TestModRepo Create(int previewBytes = 128)
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-workshop-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "About"));
        Directory.CreateDirectory(Path.Combine(root, "Source"));
        Directory.CreateDirectory(Path.Combine(root, "1.6", "Assemblies"));

        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <ModName>Test Mod</ModName>
    <ModFileName>TestMod</ModFileName>
    <Repository>https://github.com/example/TestMod</Repository>
    <ModVersion>1.2.3.4</ModVersion>
  </PropertyGroup>
</Project>
""");

        File.WriteAllText(Path.Combine(root, "About", "About.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Test Mod</name>
  <author>Andreas Pardeike</author>
  <supportedVersions>
    <li>1.6</li>
  </supportedVersions>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
    </li>
  </modDependencies>
  <packageId>brrainz.testmod</packageId>
  <modVersion>1.2.3.4</modVersion>
  <description>Test description</description>
</ModMetaData>
""");

        File.WriteAllText(Path.Combine(root, "About", "PublishedFileId.txt"), "123456789");
        File.WriteAllText(Path.Combine(root, "LoadFolders.xml"), "<loadFolders />");
        File.WriteAllText(Path.Combine(root, "Source", "TestMod.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllBytes(Path.Combine(root, "About", "Preview.png"), Enumerable.Repeat((byte)1, previewBytes).ToArray());
        File.WriteAllBytes(Path.Combine(root, "1.6", "Assemblies", "TestMod.dll"), [1, 2, 3]);

        return new TestModRepo(root);
    }

    public static TestModRepo CreateDeployed(int previewBytes = 128, bool includePublishedFileId = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-workshop-agent-tests", $"DeployedTestMod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "About"));
        Directory.CreateDirectory(Path.Combine(root, "1.6", "Assemblies"));

        File.WriteAllText(Path.Combine(root, "About", "About.xml"), """
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Deployed Test Mod</name>
  <author>Andreas Pardeike</author>
  <supportedVersions>
    <li>1.6</li>
  </supportedVersions>
  <packageId>brrainz.deployedtestmod</packageId>
  <modVersion>2.0.0</modVersion>
  <description>Deployed test description</description>
</ModMetaData>
""");

        if (includePublishedFileId)
            File.WriteAllText(Path.Combine(root, "About", "PublishedFileId.txt"), "123456789");

        File.WriteAllText(Path.Combine(root, "LoadFolders.xml"), "<loadFolders />");
        File.WriteAllBytes(Path.Combine(root, "About", "Preview.png"), Enumerable.Repeat((byte)1, previewBytes).ToArray());
        File.WriteAllBytes(Path.Combine(root, "1.6", "Assemblies", "DeployedTestMod.dll"), [1, 2, 3]);

        return new TestModRepo(root);
    }

    public static ModInspection SampleInspection(
        ulong? publishedFileId = 123456789,
        string? description = "Test description",
        string? previewImagePath = "/tmp/Preview.png")
    {
        return new ModInspection(
            RepoPath: "/tmp/TestMod",
            ModFileName: "TestMod",
            ModName: "Test Mod",
            PackageId: "brrainz.testmod",
            ModVersion: "1.2.3.4",
            RepositoryUrl: "https://github.com/example/TestMod",
            GitRemoteUrl: "https://github.com/example/TestMod",
            GitBranch: "master",
            ProjectPath: "/tmp/TestMod/Source/TestMod.csproj",
            AboutXmlPath: "/tmp/TestMod/About/About.xml",
            LoadFoldersPath: "/tmp/TestMod/LoadFolders.xml",
            PublishedFileIdPath: "/tmp/TestMod/About/PublishedFileId.txt",
            PublishedFileId: publishedFileId,
            PreviewImagePath: previewImagePath,
            PreviewImageBytes: 128,
            SupportedVersions: ["1.6"],
            Description: description,
            RimWorldAppId: AgentPaths.RimWorldAppId,
            WorkshopUrl: publishedFileId is { } id ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}" : "");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
