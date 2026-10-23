using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SteamWorkshopAgent;

[McpServerToolType]
public static class SteamTools
{
    [McpServerTool, Description("Inspect local SteamCMD/RimWorld Workshop upload prerequisites without publishing anything.")]
    public static Task<string> SteamStatus(
        [Description("When true, run `steamcmd +quit` to validate the SteamCMD executable. Defaults to false.")]
        bool runSteamCmdQuit = false)
    {
        return ToolJson.TryAsync(async () =>
        {
            var environment = ServiceLocator.Get<SteamEnvironment>();
            return await environment.GetStatusAsync(runSteamCmdQuit);
        });
    }

    [McpServerTool, Description("Probe whether a detached helper can use the logged-on Steam desktop session as RimWorld. Does not modify Workshop state. If recoveryAction is restart-steam, fully quit and reopen desktop Steam, wait for it to reconnect, and probe again before using any fallback backend.")]
    public static Task<string> SteamSessionProbe()
    {
        return ToolJson.TryAsync(async () =>
        {
            var publisher = ServiceLocator.Get<SteamworksPublisher>();
            return await publisher.ProbeAsync();
        });
    }
}

[McpServerToolType]
public static class RimWorldModTools
{
    [McpServerTool, Description("Inspect RimWorld mod metadata used for Steam Workshop publishing from a source repository or deployed mod folder.")]
    public static Task<string> RimWorldModInspect(
        [Description("Absolute path to the RimWorld mod source repository or deployed mod folder.")]
        string repoPath)
    {
        return ToolJson.TryAsync(async () =>
        {
            var inspector = ServiceLocator.Get<ModInspector>();
            return await inspector.InspectAsync(repoPath);
        });
    }
}

[McpServerToolType]
public static class WorkshopTools
{
    [McpServerTool, Description("Read the current main Steam Workshop item title and description through Steam's public item details API.")]
    public static Task<string> WorkshopGetDescription(
        [Description("Absolute path to a RimWorld mod source/deployed folder with About/PublishedFileId.txt, or a numeric Workshop published file id.")]
        string modPathOrPublishedFileId)
    {
        return ToolJson.TryAsync(async () =>
        {
            var reader = ServiceLocator.Get<WorkshopDescriptionReader>();
            return await reader.GetDescriptionAsync(modPathOrPublishedFileId);
        });
    }

    [McpServerTool, Description("Create a dry-run Steam Workshop release plan and VDF content from a GitHub release.")]
    public static Task<string> WorkshopReleasePlan(
        [Description("Absolute path to the RimWorld mod repository.")]
        string repoPath,
        [Description("GitHub release tag, e.g. v3.6.2.0.")]
        string tag,
        [Description("When true, include About/About.xml description in the VDF. Defaults to false to preserve the existing Workshop description on updates.")]
        bool updateDescription = false,
        [Description("Optional Steam Workshop changenote override. If omitted, the GitHub release body is used.")]
        string? changeNote = null)
    {
        return ToolJson.TryAsync(async () =>
        {
            var planner = ServiceLocator.Get<WorkshopPlanner>();
            return await planner.CreateReleasePlanAsync(repoPath, tag, updateDescription, changeNote: changeNote);
        });
    }

    [McpServerTool, Description("Publish a GitHub release build to Steam Workshop. The default detached Steamworks backend uses the logged-on desktop Steam session; SteamCMD is an explicit emergency fallback. Requires confirm=true.")]
    public static Task<string> WorkshopPublishRelease(
        [Description("Absolute path to the RimWorld mod repository.")]
        string repoPath,
        [Description("GitHub release tag, e.g. v3.6.2.0.")]
        string tag,
        [Description("Must be true to run the release build and SteamCMD upload. False returns the dry-run plan.")]
        bool confirm = false,
        [Description("Steam username for `steamcmd +login`. If omitted, STEAMCMD_USER is used. Passwords are never accepted or stored.")]
        string? steamUser = null,
        [Description("When true, update the main Workshop description from About/About.xml. Defaults to false.")]
        bool updateDescription = false,
        [Description("Optional Steam Workshop changenote override. If omitted, the GitHub release body is used.")]
        string? changeNote = null,
        [Description("Upload backend: auto or standalone uses detached Steamworks; steamcmd is the explicit credential-token fallback. Defaults to auto.")]
        string backend = "auto")
    {
        return ToolJson.TryAsync(async () =>
        {
            var publisher = ServiceLocator.Get<WorkshopPublisher>();
            return await publisher.PublishReleaseAsync(repoPath, tag, confirm, updateDescription, steamUser, changeNote, backend);
        });
    }

    [McpServerTool, Description("Publish an already-built deployed RimWorld mod folder using GitHub release metadata for the changenote. Does not rebuild content or refresh tags. The default backend uses the logged-on desktop Steam session. Requires confirm=true.")]
    public static Task<string> WorkshopPublishDeployedRelease(
        [Description("Absolute path to the RimWorld mod source repository used for GitHub release metadata.")]
        string repoPath,
        [Description("GitHub release tag, e.g. v3.6.2.0.")]
        string tag,
        [Description("Absolute path to the already-built deployed mod folder to upload as Workshop content.")]
        string contentFolder,
        [Description("Must be true to run SteamCMD upload. False returns the dry-run plan.")]
        bool confirm = false,
        [Description("Steam username for `steamcmd +login`. If omitted, STEAMCMD_USER is used. Passwords are never accepted or stored.")]
        string? steamUser = null,
        [Description("When true, update the main Workshop description from About/About.xml. Defaults to false.")]
        bool updateDescription = false,
        [Description("Optional Steam Workshop changenote override. If omitted, the GitHub release body is used.")]
        string? changeNote = null,
        [Description("Upload backend: auto or standalone uses detached Steamworks; steamcmd is the explicit credential-token fallback. Defaults to auto.")]
        string backend = "auto")
    {
        return ToolJson.TryAsync(async () =>
        {
            var publisher = ServiceLocator.Get<WorkshopPublisher>();
            return await publisher.PublishDeployedReleaseAsync(repoPath, tag, contentFolder, confirm, updateDescription, steamUser, changeNote, backend);
        });
    }

    [McpServerTool, Description("Publish an already-staged folder as is, without a repository, GitHub release or build. Requires a matching About/PublishedFileId.txt and an exact nonempty change-note file. Confirmation requires expectDigest from the approved dry run. Preserves description, visibility and tags. Without confirm, returns a local-only plan.")]
    public static Task<string> WorkshopPublishFolder(
        [Description("Absolute path to the already-staged mod folder. Never modified.")]
        string folder,
        [Description("Expected nonzero Workshop item id, which must match About/PublishedFileId.txt.")]
        ulong expectId,
        [Description("Path to a nonempty UTF-8 text file. Its text is submitted without a heading or prefix.")]
        string changeNoteFile,
        bool confirm = false,
        [Description("Upload backend: auto or standalone. SteamCMD is not supported for this command.")]
        string backend = "auto",
        [Description("Required when confirm=true: the 64-character contentDigest printed by the approved dry run. Refuses changed content before and after creator lookup.")]
        string? expectDigest = null)
    {
        return ToolJson.TryAsync(() => ServiceLocator.Get<WorkshopPublisher>()
            .PublishFolderAsync(folder, expectId, changeNoteFile, confirm, backend, expectDigest));
    }

    [McpServerTool, Description("Submit an already-prepared owner-verified Workshop request through the detached Steamworks helper. Use only for a request produced by this agent. Requires no password.")]
    public static Task<string> WorkshopPublishPrepared(
        [Description("Absolute path to steamworks-request.json under the SteamWorkshopAgent runs directory.")]
        string requestPath)
    {
        return ToolJson.TryAsync(async () =>
        {
            var publisher = ServiceLocator.Get<SteamworksPublisher>();
            return await publisher.PublishPreparedAsync(requestPath);
        });
    }

    [McpServerTool, Description("Verify a prepared Workshop update against Steam's public item details after submission. This read-only check never retries an upload.")]
    public static Task<string> WorkshopVerifyAfterPublish(
        [Description("Absolute path to steamworks-request.json under the SteamWorkshopAgent runs directory.")]
        string requestPath,
        [Description("Optional polling window from 0 to 180 seconds for Steam's public metadata to update.")]
        int waitSeconds = 0)
    {
        return ToolJson.TryAsync(async () =>
        {
            var verifier = ServiceLocator.Get<WorkshopPublishVerifier>();
            return await verifier.VerifyAsync(requestPath, waitSeconds);
        });
    }

    [McpServerTool, Description("Create or update a dedicated private Workshop validation item through the detached Steamworks backend. This command refuses the public Zombieland item id and requires confirm=true.")]
    public static Task<string> WorkshopValidatePrivatePublish(
        [Description("Must be true to create or update the dedicated private validation item.")]
        bool confirm = false)
    {
        return ToolJson.TryAsync(async () =>
        {
            var validator = ServiceLocator.Get<PrivateWorkshopValidator>();
            return await validator.ValidateAsync(confirm);
        });
    }

    [McpServerTool, Description("Prepare, but do not submit, a fresh request for the dedicated private validation item. This is intended for testing the RimWorld companion fallback.")]
    public static Task<string> WorkshopPreparePrivateValidation(
        [Description("Must be true to prepare the request.")]
        bool confirm = false)
    {
        return ToolJson.TryAsync(async () =>
        {
            var validator = ServiceLocator.Get<PrivateWorkshopValidator>();
            return await validator.PrepareExistingAsync(confirm);
        });
    }

    [McpServerTool, Description("Create a new private Steam Workshop item for a RimWorld mod source repository or deployed mod folder using SteamCMD, submit Mod/version tags through local Steamworks, and write the returned id to About/PublishedFileId.txt. Requires confirm=true and a Steam username or STEAMCMD_USER.")]
    public static Task<string> WorkshopCreateNewMod(
        [Description("Absolute path to the RimWorld mod source repository or deployed mod folder.")]
        string repoPath,
        [Description("Must be true to create the Workshop item and write About/PublishedFileId.txt. Source repositories are built first; deployed mod folders are uploaded directly. False returns the dry-run plan.")]
        bool confirm = false,
        [Description("Steam username for `steamcmd +login`. If omitted, STEAMCMD_USER is used. Passwords are never accepted or stored.")]
        string? steamUser = null,
        [Description("Initial Workshop visibility: private, friends, public, or unlisted. Defaults to private.")]
        string visibility = "private",
        [Description("Initial Workshop changenote. Defaults to Initial upload.")]
        string changeNote = "Initial upload")
    {
        return ToolJson.TryAsync(async () =>
        {
            var publisher = ServiceLocator.Get<WorkshopPublisher>();
            return await publisher.CreateNewModAsync(repoPath, confirm, steamUser, visibility, changeNote);
        });
    }

    [McpServerTool, Description("Set Workshop tags on an existing RimWorld Workshop item using the local Steamworks UGC path. Requires confirm=true and a logged-on Steam desktop client session.")]
    public static Task<string> WorkshopSetTags(
        [Description("Absolute path to a RimWorld mod source/deployed folder with About/PublishedFileId.txt, or a numeric Workshop published file id.")]
        string modPathOrPublishedFileId,
        [Description("Tags to set. If omitted for a mod path, defaults to Mod plus supported RimWorld versions from About/About.xml.")]
        string[]? tags = null,
        [Description("Must be true to submit the tag update. False returns the dry-run plan.")]
        bool confirm = false,
        [Description("Workshop changenote for the tag update. Defaults to Set Workshop tags.")]
        string changeNote = "Set Workshop tags")
    {
        return ToolJson.TryAsync(async () =>
        {
            var updater = ServiceLocator.Get<WorkshopTagUpdater>();
            return await updater.SetTagsAsync(modPathOrPublishedFileId, tags ?? [], confirm, changeNote);
        });
    }

    [McpServerTool, Description("Update the main Steam Workshop item description only, using SteamCMD metadata upload. Does not rebuild content, change preview images, or set tags. Requires confirm=true and a Steam username or STEAMCMD_USER.")]
    public static Task<string> WorkshopUpdateDescription(
        [Description("Absolute path to a RimWorld mod source/deployed folder with About/PublishedFileId.txt, or a numeric Workshop published file id.")]
        string modPathOrPublishedFileId,
        [Description("The complete main Workshop page description to set.")]
        string description,
        [Description("Must be true to run SteamCMD. False returns the dry-run VDF plan.")]
        bool confirm = false,
        [Description("Steam username for `steamcmd +login`. If omitted, STEAMCMD_USER is used. Passwords are never accepted or stored.")]
        string? steamUser = null,
        [Description("Optional replacement title. Omit to preserve the existing Workshop title.")]
        string? title = null,
        [Description("Optional Workshop changenote. Omit to avoid intentionally writing release/update note text.")]
        string? changeNote = null)
    {
        return ToolJson.TryAsync(async () =>
        {
            var updater = ServiceLocator.Get<WorkshopDescriptionUpdater>();
            return await updater.UpdateDescriptionAsync(modPathOrPublishedFileId, description, confirm, steamUser, title, changeNote);
        });
    }

    [McpServerTool, Description("Submit a changenote update for an existing RimWorld Workshop item through local Steamworks. Requires confirm=true and a logged-on Steam desktop client session.")]
    public static Task<string> WorkshopSetChangeNote(
        [Description("Absolute path to a RimWorld mod source/deployed folder with About/PublishedFileId.txt, or a numeric Workshop published file id.")]
        string modPathOrPublishedFileId,
        [Description("Workshop changenote text to submit.")]
        string changeNote,
        [Description("Must be true to submit the changenote update. False returns the dry-run plan.")]
        bool confirm = false)
    {
        return ToolJson.TryAsync(async () =>
        {
            var updater = ServiceLocator.Get<WorkshopTagUpdater>();
            return await updater.SetChangeNoteAsync(modPathOrPublishedFileId, changeNote, confirm);
        });
    }
}
