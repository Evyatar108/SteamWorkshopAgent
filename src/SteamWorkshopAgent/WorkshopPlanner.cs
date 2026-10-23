namespace SteamWorkshopAgent;

public sealed class WorkshopPlanner(ModInspector modInspector, GitHubReleaseReader releaseReader)
{
    private static readonly IReadOnlyDictionary<string, string> VisibilityValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "0",
        ["public"] = "0",
        ["1"] = "1",
        ["friends"] = "1",
        ["friends-only"] = "1",
        ["friendsonly"] = "1",
        ["2"] = "2",
        ["private"] = "2",
        ["3"] = "3",
        ["unlisted"] = "3"
    };

    public async Task<WorkshopReleasePlan> CreateFolderPlanAsync(
        string folder, ulong expectedId, string changeNoteFile)
    {
        if (expectedId == 0)
            throw new ArgumentException("The expected Workshop item id must be nonzero.", nameof(expectedId));
        var mod = await modInspector.InspectFolderAsync(folder);
        if (mod.PublishedFileId is not { } id || id == 0)
            throw new InvalidOperationException("About/PublishedFileId.txt must contain a nonzero Workshop item id.");
        if (id != expectedId)
            throw new InvalidOperationException($"About/PublishedFileId.txt contains {id}, not the expected item {expectedId}.");
        var changeNote = await File.ReadAllTextAsync(changeNoteFile);
        if (string.IsNullOrWhiteSpace(changeNote))
            throw new InvalidOperationException("The change-note file must not be empty.");

        var runDir = AgentPaths.NewRunDirectory(mod.ModFileName, $"folder-{Guid.NewGuid():N}");
        var contentRoot = Path.TrimEndingDirectorySeparator(mod.RepoPath) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(runDir).StartsWith(contentRoot, AgentDataPath.PathComparison))
            throw new InvalidOperationException(
                "The agent data root must be outside the content folder. Choose a different STEAM_WORKSHOP_AGENT_DATA_ROOT.");
        var previewFile = Path.Combine(mod.RepoPath, "About", "Preview.png");
        var fields = CreateVdfFields(mod, changeNote, mod.RepoPath, previewFile, updateDescription: false);
        var issues = Validation.ValidateForWorkshop(mod, previewFile, mod.RepoPath, requireAssembly: false);
        Validation.ThrowIfErrors(issues);
        return new WorkshopReleasePlan(
            mod, Release: null, runDir, Path.Combine(runDir, "mods"), mod.RepoPath,
            previewFile, Path.Combine(runDir, "workshop.vdf"), VdfWriter.WriteWorkshopItem(fields),
            changeNote, UpdateDescription: false, TagsPreserved: true, IntendedTags: [], issues);
    }

    public async Task<WorkshopReleasePlan> CreateReleasePlanAsync(
        string repoPath,
        string tag,
        bool updateDescription = false,
        string? runDirectory = null,
        string? contentFolderOverride = null,
        string? changeNote = null)
    {
        var mod = await modInspector.InspectAsync(repoPath);
        if (!mod.HasBuildProject)
            throw new InvalidOperationException("Release publishing requires a source repository with Directory.Build.props and a .csproj. Use new-mod for an already-built mod folder.");

        var release = await releaseReader.ReadAsync(mod.RepoPath, tag);
        var runDir = runDirectory ?? AgentPaths.NewRunDirectory(mod.ModFileName, tag);
        var stagingRoot = Path.Combine(runDir, "mods");
        var contentFolder = contentFolderOverride ?? Path.Combine(stagingRoot, mod.ModFileName);
        var previewFile = Path.Combine(contentFolder, "About", "Preview.png");
        var vdfPath = Path.Combine(runDir, "workshop.vdf");
        var steamChangeNote = CreateSteamChangeNote(mod, release, changeNote);

        var fields = CreateVdfFields(mod, steamChangeNote, contentFolder, previewFile, updateDescription);
        var vdfContent = VdfWriter.WriteWorkshopItem(fields);

        var validationPreviewPath = File.Exists(previewFile) ? previewFile : mod.PreviewImagePath ?? previewFile;
        var issues = Validation.ValidateForWorkshop(mod, validationPreviewPath, contentFolder)
            .Concat(new[]
            {
                new ValidationIssue(
                    "workshop_tags_preserved",
                    "Existing Workshop tags are preserved; release publishing does not refresh them.",
                    "info")
            })
            .ToList();

        var intendedTags = new List<string> { "Mod" };
        intendedTags.AddRange(mod.SupportedVersions);

        return new WorkshopReleasePlan(
            mod,
            release,
            runDir,
            stagingRoot,
            contentFolder,
            previewFile,
            vdfPath,
            vdfContent,
            steamChangeNote,
            updateDescription,
            TagsPreserved: true,
            intendedTags,
            issues);
    }

    public async Task<WorkshopNewModPlan> CreateNewModPlanAsync(
        string repoPath,
        string visibility = "private",
        string changeNote = "Initial upload",
        string? runDirectory = null,
        string? contentFolderOverride = null)
    {
        var mod = await modInspector.InspectAsync(repoPath);
        var normalizedVisibility = NormalizeVisibility(visibility);
        var runDir = runDirectory ?? AgentPaths.NewRunDirectory(mod.ModFileName, "new-mod");
        var stagingRoot = Path.Combine(runDir, "mods");
        var contentFolder = contentFolderOverride ?? (mod.HasBuildProject ? Path.Combine(stagingRoot, mod.ModFileName) : mod.RepoPath);
        var previewFile = Path.Combine(contentFolder, "About", "Preview.png");
        var vdfPath = Path.Combine(runDir, "workshop.vdf");
        var tags = CreateDefaultTags(mod);

        var fields = CreateNewModVdfFields(mod, contentFolder, previewFile, normalizedVisibility, changeNote);
        var vdfContent = VdfWriter.WriteWorkshopItem(fields, tags);

        var validationPreviewPath = File.Exists(previewFile) ? previewFile : mod.PreviewImagePath ?? previewFile;
        var issues = Validation.ValidateForWorkshop(
                mod,
                validationPreviewPath,
                contentFolder,
                requirePublishedFileId: false)
            .ToList();

        if (mod.PublishedFileId is not null and not 0)
            issues.Add(new ValidationIssue(
                "published_file_id_exists",
                $"About/PublishedFileId.txt already contains Workshop item id {mod.PublishedFileId}. Use the release publish flow to update it instead of creating a duplicate item.",
                "error"));

        return new WorkshopNewModPlan(
            mod,
            runDir,
            stagingRoot,
            contentFolder,
            previewFile,
            vdfPath,
            vdfContent,
            normalizedVisibility,
            changeNote,
            tags,
            issues);
    }

    public static string CreateSteamChangeNote(ModInspection mod, GitHubReleaseInfo release, string? changeNoteOverride = null)
    {
        var heading = $"v{FormatSteamVersion(mod.ModVersion)}";
        var legacyHeading = $"{mod.ModName} {heading}";
        var text = string.IsNullOrWhiteSpace(changeNoteOverride)
            ? release.ChangeNote.Trim()
            : changeNoteOverride.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return heading;

        if (HasSteamReleaseHeading(text, heading) || HasSteamReleaseHeading(text, legacyHeading))
            return text;

        return $"{heading}\n\n{text}";
    }

    public static IReadOnlyDictionary<string, string> CreateVdfFields(
        ModInspection mod,
        string changeNote,
        string contentFolder,
        string previewFile,
        bool updateDescription)
    {
        if (mod.PublishedFileId is null)
            throw new InvalidOperationException("Cannot create a Workshop update VDF without PublishedFileId.");

        var fields = new Dictionary<string, string>
        {
            ["appid"] = AgentPaths.RimWorldAppId.ToString(),
            ["publishedfileid"] = mod.PublishedFileId.Value.ToString(),
            ["contentfolder"] = contentFolder,
            ["previewfile"] = previewFile,
            ["title"] = mod.ModName,
            ["changenote"] = changeNote
        };

        if (updateDescription)
            fields["description"] = mod.Description ?? "";

        return fields;
    }

    public static IReadOnlyDictionary<string, string> CreateNewModVdfFields(
        ModInspection mod,
        string contentFolder,
        string previewFile,
        string visibility,
        string changeNote)
    {
        var fields = new Dictionary<string, string>
        {
            ["appid"] = AgentPaths.RimWorldAppId.ToString(),
            ["publishedfileid"] = "0",
            ["contentfolder"] = contentFolder,
            ["previewfile"] = previewFile,
            ["title"] = mod.ModName,
            ["description"] = mod.Description ?? "",
            ["visibility"] = NormalizeVisibility(visibility),
            ["changenote"] = changeNote
        };

        return fields;
    }

    public static string NormalizeVisibility(string visibility)
    {
        var value = visibility.Trim();
        if (VisibilityValues.TryGetValue(value, out var normalized))
            return normalized;

        throw new ArgumentException("Visibility must be one of: public, friends, private, unlisted, 0, 1, 2, or 3.", nameof(visibility));
    }

    public static IReadOnlyList<string> CreateDefaultTags(ModInspection mod)
    {
        return new[] { "Mod" }
            .Concat(mod.SupportedVersions)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatSteamVersion(string version)
    {
        var text = version.Trim();
        var parts = text.Split('.');

        return parts.Length == 4 && parts[3] == "0"
            ? string.Join('.', parts.Take(3))
            : text;
    }

    private static bool HasSteamReleaseHeading(string text, string heading)
    {
        if (string.Equals(text, heading, StringComparison.OrdinalIgnoreCase))
            return true;

        return text.StartsWith($"{heading}\n", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith($"{heading}\r\n", StringComparison.OrdinalIgnoreCase);
    }
}
