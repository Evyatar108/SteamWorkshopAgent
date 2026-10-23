using System.Xml.Linq;

namespace SteamWorkshopAgent;

public sealed class ModInspector(ProcessRunner processRunner)
{
    public Task<ModInspection> InspectAsync(string repoPath) => InspectAsync(repoPath, folderOnly: false);

    public Task<ModInspection> InspectFolderAsync(string folder) => InspectAsync(folder, folderOnly: true);

    private async Task<ModInspection> InspectAsync(string repoPath, bool folderOnly)
    {
        var absoluteRepo = Path.GetFullPath(ExpandHome(repoPath));
        if (!Directory.Exists(absoluteRepo))
            throw new DirectoryNotFoundException($"Repository path does not exist: {absoluteRepo}");

        var aboutXmlPath = Path.Combine(absoluteRepo, "About", "About.xml");
        if (!File.Exists(aboutXmlPath))
            throw new FileNotFoundException("About/About.xml was not found.", aboutXmlPath);

        var propsPath = Path.Combine(absoluteRepo, "Directory.Build.props");
        string? modFileNameFromProps = null;
        string? modNameFromProps = null;
        string? repositoryUrlFromProps = null;
        string? modVersionFromProps = null;
        string? projectPath = null;

        if (!folderOnly && File.Exists(propsPath))
        {
            var props = XDocument.Load(propsPath);
            modFileNameFromProps = RequiredDescendant(props, "ModFileName", propsPath);
            modNameFromProps = OptionalDescendant(props, "ModName");
            repositoryUrlFromProps = OptionalDescendant(props, "Repository");
            modVersionFromProps = OptionalDescendant(props, "ModVersion");
            projectPath = ResolveProjectPath(absoluteRepo, modFileNameFromProps);
        }

        var about = XDocument.Load(aboutXmlPath);
        var root = about.Root ?? throw new InvalidOperationException($"Invalid About.xml: {aboutXmlPath}");
        var modFileName = string.IsNullOrWhiteSpace(modFileNameFromProps)
            ? InferModFileName(absoluteRepo, root, aboutXmlPath)
            : modFileNameFromProps;
        var modName = RequiredChild(root, "name", aboutXmlPath, fallback: modNameFromProps);
        var packageId = RequiredChild(root, "packageId", aboutXmlPath);
        var modVersion = folderOnly
            ? OptionalChild(root, "modVersion") ?? ""
            : RequiredChild(root, "modVersion", aboutXmlPath, fallback: modVersionFromProps);
        var repositoryUrl = repositoryUrlFromProps ?? OptionalChild(root, "url");
        var description = OptionalChild(root, "description");
        var supportedVersions = root.Element("supportedVersions")?
            .Elements("li")
            .Select(e => (e.Value ?? "").Trim())
            .Where(v => v.Length > 0)
            .ToList() ?? [];

        var publishedFileIdPath = Path.Combine(absoluteRepo, "About", "PublishedFileId.txt");
        ulong? publishedFileId = null;
        if (File.Exists(publishedFileIdPath))
        {
            var raw = File.ReadAllText(publishedFileIdPath).Trim();
            if (raw.Length > 0 && ulong.TryParse(raw, out var parsed) && parsed != 0)
                publishedFileId = parsed;
        }

        var previewPath = Path.Combine(absoluteRepo, "About", "Preview.png");
        var previewInfo = File.Exists(previewPath) ? new FileInfo(previewPath) : null;
        var loadFoldersPath = Path.Combine(absoluteRepo, "LoadFolders.xml");
        var gitRemote = folderOnly ? null : await TryGitAsync(absoluteRepo, "remote", "get-url", "origin");
        var gitBranch = folderOnly ? null : await TryGitAsync(absoluteRepo, "branch", "--show-current");

        var workshopUrl = publishedFileId is { } id
            ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"
            : "";

        return new ModInspection(
            absoluteRepo,
            modFileName,
            modName,
            packageId,
            modVersion,
            repositoryUrl,
            gitRemote,
            gitBranch,
            projectPath,
            aboutXmlPath,
            File.Exists(loadFoldersPath) ? loadFoldersPath : null,
            File.Exists(publishedFileIdPath) ? publishedFileIdPath : null,
            publishedFileId,
            previewInfo?.FullName,
            previewInfo?.Length,
            supportedVersions,
            description,
            AgentPaths.RimWorldAppId,
            workshopUrl);
    }

    private static string InferModFileName(string modPath, XElement aboutRoot, string aboutXmlPath)
    {
        var folderName = new DirectoryInfo(modPath).Name.Trim();
        if (!string.IsNullOrWhiteSpace(folderName))
            return folderName;

        return RequiredChild(aboutRoot, "name", aboutXmlPath);
    }

    private static string ResolveProjectPath(string repoPath, string modFileName)
    {
        var candidates = Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        var named = candidates.FirstOrDefault(path => Path.GetFileName(path).Equals($"{modFileName}.csproj", StringComparison.OrdinalIgnoreCase));
        if (named != null)
            return named;

        if (candidates.Count == 1)
            return candidates[0];

        throw new InvalidOperationException($"Expected one project file or {modFileName}.csproj, found {candidates.Count}: {string.Join(", ", candidates)}");
    }

    private async Task<string?> TryGitAsync(string repoPath, params string[] args)
    {
        try
        {
            var result = await processRunner.RunAsync("git", ["-C", repoPath, .. args], timeout: TimeSpan.FromSeconds(10));
            return result.ExitCode == 0 ? result.Stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string RequiredDescendant(XDocument document, string name, string path)
    {
        var value = OptionalDescendant(document, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{path} is missing {name}.")
            : value;
    }

    private static string? OptionalDescendant(XDocument document, string name)
    {
        return document.Descendants(name).FirstOrDefault()?.Value.Trim();
    }

    private static string RequiredChild(XElement root, string name, string path, string? fallback = null)
    {
        var value = OptionalChild(root, name);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;
        throw new InvalidOperationException($"{path} is missing root child {name}.");
    }

    private static string? OptionalChild(XElement root, string name)
    {
        return root.Element(name)?.Value.Trim();
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }
}
