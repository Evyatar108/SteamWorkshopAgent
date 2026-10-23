namespace SteamWorkshopAgent;

public static class Validation
{
    public static IReadOnlyList<ValidationIssue> ValidateForWorkshop(
        ModInspection mod,
        string previewPath,
        string contentFolder,
        bool requirePublishedFileId = true,
        bool requireAssembly = true)
    {
        var issues = new List<ValidationIssue>();

        if (requirePublishedFileId && mod.PublishedFileId is (null or 0))
            issues.Add(new ValidationIssue("missing_published_file_id", "About/PublishedFileId.txt is missing or does not contain a nonzero Workshop item id.", "error"));

        if (string.IsNullOrWhiteSpace(mod.ModFileName))
            issues.Add(new ValidationIssue("missing_mod_file_name", "Directory.Build.props is missing ModFileName.", "error"));

        if (!File.Exists(previewPath))
        {
            issues.Add(new ValidationIssue("missing_preview", $"Preview image was not found: {previewPath}", "error"));
        }
        else
        {
            var previewBytes = new FileInfo(previewPath).Length;
            if (previewBytes >= 1024 * 1024)
                issues.Add(new ValidationIssue("preview_too_large", $"Preview image is {previewBytes} bytes; Steam requires it to be under 1 MB.", "error"));
            if (previewBytes < 16)
                issues.Add(new ValidationIssue("preview_too_small", $"Preview image is {previewBytes} bytes; Steam may reject previews smaller than 16 bytes.", "error"));
        }

        if (mod.LoadFoldersPath == null)
            issues.Add(new ValidationIssue("missing_loadfolders", "LoadFolders.xml was not found. The upload may still work, but RimWorld version folder selection will not be explicit.", "warning"));

        if (Directory.Exists(contentFolder))
        {
            var about = Path.Combine(contentFolder, "About", "About.xml");
            if (!File.Exists(about))
                issues.Add(new ValidationIssue("staged_missing_about", $"Staged About/About.xml was not found: {about}", "error"));

            if (requireAssembly)
            {
                var hasAssembly = Directory.EnumerateFiles(contentFolder, "*.dll", SearchOption.AllDirectories)
                    .Any(path => path.Split(Path.DirectorySeparatorChar).Contains("Assemblies"));
                if (!hasAssembly)
                    issues.Add(new ValidationIssue("staged_missing_assembly", "No staged DLL under an Assemblies folder was found.", "error"));
            }
        }

        return issues;
    }

    public static void ThrowIfErrors(IEnumerable<ValidationIssue> issues)
    {
        var errors = issues.Where(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Validation failed: " + string.Join("; ", errors.Select(error => $"{error.Code}: {error.Message}")));
    }
}
