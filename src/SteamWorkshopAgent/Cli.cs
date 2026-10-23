namespace SteamWorkshopAgent;

internal static class Cli
{
    public static bool ShouldHandle(string[] args)
    {
        return args.Length > 0
            && !string.Equals(args[0], "server", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = args[0].ToLowerInvariant();
            var services = CreateServices();
            var data = command switch
            {
                "status" => await services.SteamEnvironment.GetStatusAsync(args.Contains("--run-steamcmd-quit")),
                "inspect" => await services.ModInspector.InspectAsync(RequireArg(args, 1, "repoPath")),
                "plan" => await services.WorkshopPlanner.CreateReleasePlanAsync(
                    RequireArg(args, 1, "repoPath"),
                    RequireArg(args, 2, "tag"),
                    HasFlag(args, "--update-description"),
                    changeNote: GetOption(args, "--changenote")),
                "publish" => await services.WorkshopPublisher.PublishReleaseAsync(
                    RequireArg(args, 1, "repoPath"),
                    RequireArg(args, 2, "tag"),
                    HasFlag(args, "--confirm"),
                    HasFlag(args, "--update-description"),
                    GetOption(args, "--steam-user"),
                    GetOption(args, "--changenote"),
                    GetOption(args, "--backend") ?? "auto"),
                "publish-deployed" => await services.WorkshopPublisher.PublishDeployedReleaseAsync(
                    RequireArg(args, 1, "repoPath"),
                    RequireArg(args, 2, "tag"),
                    RequireArg(args, 3, "contentFolder"),
                    HasFlag(args, "--confirm"),
                    HasFlag(args, "--update-description"),
                    GetOption(args, "--steam-user"),
                    GetOption(args, "--changenote"),
                    GetOption(args, "--backend") ?? "auto"),
                "publish-folder" => await services.WorkshopPublisher.PublishFolderAsync(
                    RequireArg(args, 1, "folder"),
                    ulong.Parse(RequireOption(args, "--expect-id")),
                    RequireOption(args, "--changenote-file"),
                    HasFlag(args, "--confirm"),
                    GetOption(args, "--backend") ?? "auto",
                    GetOption(args, "--expect-digest")),
                "new-mod" => await services.WorkshopPublisher.CreateNewModAsync(
                    RequireArg(args, 1, "modPath"),
                    HasFlag(args, "--confirm"),
                    GetOption(args, "--steam-user"),
                    GetOption(args, "--visibility") ?? "private",
                    GetOption(args, "--changenote") ?? "Initial upload"),
                "description-get" => await services.WorkshopDescriptionReader.GetDescriptionAsync(
                    RequireArg(args, 1, "modPathOrPublishedFileId")),
                "description" => await services.WorkshopDescriptionUpdater.UpdateDescriptionAsync(
                    RequireArg(args, 1, "modPathOrPublishedFileId"),
                    await File.ReadAllTextAsync(RequireArg(args, 2, "descriptionFile")),
                    HasFlag(args, "--confirm"),
                    GetOption(args, "--steam-user"),
                    GetOption(args, "--title"),
                    GetOption(args, "--changenote")),
                "set-tags" => await services.WorkshopTagUpdater.SetTagsAsync(
                    RequireArg(args, 1, "modPathOrPublishedFileId"),
                    GetTags(args, startIndex: 2),
                    HasFlag(args, "--confirm"),
                    GetOption(args, "--changenote") ?? "Set Workshop tags"),
                "set-changenote" => await services.WorkshopTagUpdater.SetChangeNoteAsync(
                    RequireArg(args, 1, "modPathOrPublishedFileId"),
                    RequireOption(args, "--changenote"),
                    HasFlag(args, "--confirm")),
                "steam-session-probe" => await services.SteamworksPublisher.ProbeAsync(),
                "publish-prepared" => await services.SteamworksPublisher.PublishPreparedAsync(
                    RequireArg(args, 1, "requestPath")),
                "verify" => await services.WorkshopPublishVerifier.VerifyAsync(
                    RequireArg(args, 1, "requestPath"),
                    int.TryParse(GetOption(args, "--wait-seconds"), out var waitSeconds) ? waitSeconds : 0),
                "private-validation" => await services.PrivateWorkshopValidator.ValidateAsync(
                    HasFlag(args, "--confirm")),
                "private-validation-prepare" => await services.PrivateWorkshopValidator.PrepareExistingAsync(
                    HasFlag(args, "--confirm")),
                "steamworks-session-probe-internal" => services.SteamworksPublisher.ProbeInCurrentProcess(
                    RequireArg(args, 1, "nativeLibraryPath")),
                "steamworks-publish-internal" => await services.SteamworksPublisher.PublishInCurrentProcessAsync(
                    RequireArg(args, 1, "requestPath"),
                    RequireArg(args, 2, "nativeLibraryPath")),
                "steamworks-create-private-validation-item-internal" => services.PrivateWorkshopValidator.CreatePrivateItemInCurrentProcess(
                    RequireArg(args, 1, "nativeLibraryPath")),
                "steamworks-set-tags-internal" => services.WorkshopTagUpdater.SetTagsInCurrentProcess(
                    ulong.Parse(RequireArg(args, 1, "publishedFileId")),
                    WorkshopTagUpdater.DecodeJson<IReadOnlyList<string>>(RequireArg(args, 2, "tagsJsonBase64")),
                    WorkshopTagUpdater.DecodeJson<string>(RequireArg(args, 3, "changeNoteJsonBase64")),
                    RequireArg(args, 4, "nativeLibraryPath")),
                "help" or "--help" or "-h" => Usage(),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.\n{Usage()}")
            };

            Console.WriteLine(ToolJson.Ok(data));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(ToolJson.Error(exception));
            return 1;
        }
    }

    private static Services CreateServices()
    {
        var processRunner = new ProcessRunner();
        var steamEnvironment = new SteamEnvironment(processRunner);
        var modInspector = new ModInspector(processRunner);
        var releaseReader = new GitHubReleaseReader(processRunner);
        var planner = new WorkshopPlanner(modInspector, releaseReader);
        var targetResolver = new WorkshopTargetResolver(modInspector);
        var descriptionReader = new WorkshopDescriptionReader(targetResolver);
        var tagUpdater = new WorkshopTagUpdater(steamEnvironment, modInspector, processRunner);
        var releaseWorktree = new GitReleaseWorktree(processRunner);
        var requestStore = new WorkshopPublishRequestStore(processRunner);
        var steamworksPublisher = new SteamworksPublisher(steamEnvironment, processRunner, requestStore);
        var publishVerifier = new WorkshopPublishVerifier(requestStore, descriptionReader);
        var privateWorkshopValidator = new PrivateWorkshopValidator(
            steamEnvironment,
            processRunner,
            requestStore,
            steamworksPublisher);
        var publisher = new WorkshopPublisher(
            processRunner,
            steamEnvironment,
            planner,
            tagUpdater,
            releaseWorktree,
            descriptionReader,
            requestStore,
            steamworksPublisher);
        var descriptionUpdater = new WorkshopDescriptionUpdater(processRunner, steamEnvironment, targetResolver);

        return new Services(
            steamEnvironment,
            modInspector,
            planner,
            tagUpdater,
            publisher,
            descriptionReader,
            descriptionUpdater,
            steamworksPublisher,
            publishVerifier,
            privateWorkshopValidator);
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Contains(flag, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string RequireOption(string[] args, string name)
    {
        return GetOption(args, name)
            ?? throw new ArgumentException($"Missing required option: {name}\n{Usage()}");
    }

    private static string RequireArg(string[] args, int index, string name)
    {
        if (args.Length <= index || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing required argument: {name}\n{Usage()}");

        return args[index];
    }

    private static IReadOnlyList<string> GetTags(string[] args, int startIndex)
    {
        return args
            .Skip(startIndex)
            .TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .ToList();
    }

    private static string Usage()
    {
        return """
            Usage:
              SteamWorkshopAgent server
              SteamWorkshopAgent status [--run-steamcmd-quit]
              SteamWorkshopAgent inspect <repoPath>
              SteamWorkshopAgent plan <repoPath> <tag> [--update-description] [--changenote TEXT]
              SteamWorkshopAgent publish <repoPath> <tag> [--confirm] [--backend auto|standalone|steamcmd] [--steam-user USER] [--update-description] [--changenote TEXT]
              SteamWorkshopAgent publish-deployed <repoPath> <tag> <contentFolder> [--confirm] [--backend auto|standalone|steamcmd] [--steam-user USER] [--update-description] [--changenote TEXT]
              SteamWorkshopAgent publish-folder <folder> --expect-id <id> --changenote-file <path> [--backend auto|standalone] [--confirm --expect-digest <approved contentDigest>]
              SteamWorkshopAgent new-mod <modPath> [--confirm] [--steam-user USER] [--visibility private|friends|public|unlisted] [--changenote TEXT]
              SteamWorkshopAgent description-get <modPath|publishedFileId>
              SteamWorkshopAgent description <modPath|publishedFileId> <descriptionFile> [--confirm] [--steam-user USER] [--title TEXT] [--changenote TEXT]
              SteamWorkshopAgent set-tags <modPath|publishedFileId> [tag ...] [--confirm] [--changenote TEXT]
              SteamWorkshopAgent set-changenote <modPath|publishedFileId> --changenote TEXT [--confirm]
              SteamWorkshopAgent steam-session-probe
              SteamWorkshopAgent publish-prepared <requestPath>
              SteamWorkshopAgent verify <requestPath> [--wait-seconds 0..180]
              SteamWorkshopAgent private-validation [--confirm]
              SteamWorkshopAgent private-validation-prepare [--confirm]
            """;
    }

    private sealed record Services(
        SteamEnvironment SteamEnvironment,
        ModInspector ModInspector,
        WorkshopPlanner WorkshopPlanner,
        WorkshopTagUpdater WorkshopTagUpdater,
        WorkshopPublisher WorkshopPublisher,
        WorkshopDescriptionReader WorkshopDescriptionReader,
        WorkshopDescriptionUpdater WorkshopDescriptionUpdater,
        SteamworksPublisher SteamworksPublisher,
        WorkshopPublishVerifier WorkshopPublishVerifier,
        PrivateWorkshopValidator PrivateWorkshopValidator);
}
