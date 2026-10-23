namespace SteamWorkshopAgent;

public sealed record ModInspection(
    string RepoPath,
    string ModFileName,
    string ModName,
    string PackageId,
    string ModVersion,
    string? RepositoryUrl,
    string? GitRemoteUrl,
    string? GitBranch,
    string? ProjectPath,
    string AboutXmlPath,
    string? LoadFoldersPath,
    string? PublishedFileIdPath,
    ulong? PublishedFileId,
    string? PreviewImagePath,
    long? PreviewImageBytes,
    IReadOnlyList<string> SupportedVersions,
    string? Description,
    uint RimWorldAppId,
    string WorkshopUrl)
{
    public bool HasBuildProject => !string.IsNullOrWhiteSpace(ProjectPath);
}

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    string Body,
    string Url,
    string ChangeNote);

public sealed record ValidationIssue(string Code, string Message, string Severity);

public sealed record WorkshopReleasePlan(
    ModInspection Mod,
    GitHubReleaseInfo? Release,
    string RunDirectory,
    string StagingRoot,
    string ContentFolder,
    string PreviewFile,
    string VdfPath,
    string VdfContent,
    string ChangeNote,
    bool UpdateDescription,
    bool TagsPreserved,
    IReadOnlyList<string> IntendedTags,
    IReadOnlyList<ValidationIssue> ValidationIssues);

public sealed record WorkshopFolderPlan(
    string Title,
    ulong PublishedFileId,
    int FileCount,
    string ContentDigest,
    long PreviewBytes,
    string ChangeNote);

public sealed record WorkshopNewModPlan(
    ModInspection Mod,
    string RunDirectory,
    string StagingRoot,
    string ContentFolder,
    string PreviewFile,
    string VdfPath,
    string VdfContent,
    string Visibility,
    string ChangeNote,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ValidationIssue> ValidationIssues);

public sealed record SteamStatusResult(
    string? SteamCmdPath,
    bool SteamCmdFound,
    string? SteamCmdUser,
    bool SteamAppManifestFound,
    string? SteamAppManifestPath,
    bool SteamworksNativeLibraryFound,
    string? SteamworksNativeLibraryPath,
    uint RimWorldAppId,
    IReadOnlyList<string> WorkshopLogPaths,
    ProcessResult? SteamCmdQuitResult,
    string SetupHint,
    string TagUpdateHint);

public sealed record PublishResult(
    bool Success,
    int SteamCmdExitCode,
    string WorkshopUrl,
    string RunDirectory,
    string VdfPath,
    string ContentFolder,
    string SteamCmdStdout,
    string SteamCmdStderr,
    IReadOnlyList<string> LogTails,
    WorkshopTagUpdateResult? TagUpdate,
    WorkshopReleasePlan Plan);

public sealed record WorkshopPublishRequest(
    int SchemaVersion,
    string RequestId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    uint AppId,
    ulong PublishedFileId,
    ulong ExpectedCreatorSteamId,
    string ContentFolder,
    string PreviewFile,
    string Title,
    string? Description,
    bool UpdateDescription,
    bool PreserveTags,
    int? Visibility,
    string ChangeNote,
    string SourceTag,
    string SourceCommit,
    string ContentDigest,
    string ResultPath)
{
    public string? PreviewDigest { get; init; }
    public string? ContentDigestFormat { get; init; }
}

public sealed record WorkshopPublishPreparation(
    string Backend,
    string RequestPath,
    string ResultPath,
    string ContentDigest,
    WorkshopPublishRequest Request,
    WorkshopReleasePlan? Plan);

public sealed record SteamSessionProbeResult(
    string Backend,
    bool DetachedSession,
    bool SteamInitialized,
    bool SteamUserLoggedOn,
    ulong? SteamId,
    uint? SteamAppId,
    string? NativeLibraryPath,
    bool Ready,
    string Message,
    string? FailureCode = null,
    string? RecoveryAction = null);

public sealed record WorkshopPublishBackendResult(
    string Backend,
    string Stage,
    bool Success,
    bool SubmissionStarted,
    bool OutcomeDefinitive,
    bool FallbackAllowed,
    bool SteamInitialized,
    bool SteamUserLoggedOn,
    ulong? SteamId,
    uint? SteamAppId,
    ulong PublishedFileId,
    string? SubmitResult,
    bool UserNeedsToAcceptWorkshopLegalAgreement,
    string? UploadStatus,
    ulong BytesProcessed,
    ulong BytesTotal,
    long DurationMs,
    string RequestPath,
    string ResultPath,
    string WorkshopUrl,
    string Message,
    WorkshopReleasePlan? Plan = null,
    string? FailureCode = null,
    string? RecoveryAction = null);

public sealed record WorkshopPublishVerificationResult(
    bool Success,
    ulong PublishedFileId,
    string WorkshopUrl,
    string ExpectedTitle,
    string ActualTitle,
    long? TimeUpdated,
    DateTimeOffset RequestCreatedAtUtc,
    bool TitleMatches,
    bool UpdateTimestampMatches,
    string RequestPath,
    string Message);

public sealed record PrivateWorkshopItemCreationResult(
    bool Success,
    bool SteamInitialized,
    bool SteamUserLoggedOn,
    ulong? SteamId,
    uint? SteamAppId,
    ulong? PublishedFileId,
    string? SteamResult,
    bool UserNeedsToAcceptWorkshopLegalAgreement,
    bool TimedOut,
    string Message);

public sealed record PrivateWorkshopValidationResult(
    bool Success,
    bool ReusedExistingItem,
    string ItemMetadataPath,
    ulong PublishedFileId,
    string RequestPath,
    PrivateWorkshopItemCreationResult? Creation,
    WorkshopPublishBackendResult Publish,
    string Message);

public sealed record CreateNewModResult(
    bool Success,
    int SteamCmdExitCode,
    ulong? PublishedFileId,
    string PublishedFileIdPath,
    string WorkshopUrl,
    string RunDirectory,
    string VdfPath,
    string ContentFolder,
    string SteamCmdStdout,
    string SteamCmdStderr,
    IReadOnlyList<string> LogTails,
    WorkshopTagUpdateResult? TagUpdate,
    WorkshopNewModPlan Plan);

public sealed record WorkshopDescriptionPlan(
    ulong PublishedFileId,
    string? ModPath,
    string? ModName,
    string WorkshopUrl,
    uint RimWorldAppId,
    string RunDirectory,
    string VdfPath,
    string Description,
    int DescriptionCharacters,
    string? Title,
    string? ChangeNote,
    string VdfContent,
    IReadOnlyList<ValidationIssue> ValidationIssues);

public sealed record WorkshopDescriptionTarget(
    ulong PublishedFileId,
    string? ModPath,
    string? ModName);

public sealed record WorkshopDescriptionSnapshot(
    ulong PublishedFileId,
    string? ModPath,
    string? ModName,
    string WorkshopUrl,
    int Result,
    string Title,
    string Description,
    int DescriptionCharacters,
    int? Visibility,
    long? TimeCreated,
    long? TimeUpdated,
    uint? ConsumerAppId,
    string? Creator,
    string? PreviewUrl,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ValidationIssue> ValidationIssues);

public sealed record WorkshopDescriptionUpdateResult(
    bool Success,
    int SteamCmdExitCode,
    string WorkshopUrl,
    string RunDirectory,
    string VdfPath,
    string SteamCmdStdout,
    string SteamCmdStderr,
    IReadOnlyList<string> LogTails,
    WorkshopDescriptionPlan Plan);

public sealed record WorkshopTagUpdatePlan(
    ulong PublishedFileId,
    IReadOnlyList<string> Tags,
    string ChangeNote,
    uint RimWorldAppId,
    string? NativeLibraryPath,
    IReadOnlyList<ValidationIssue> ValidationIssues);

public sealed record WorkshopTagUpdateResult(
    bool Success,
    ulong PublishedFileId,
    IReadOnlyList<string> Tags,
    string ChangeNote,
    string Backend,
    string? NativeLibraryPath,
    bool SteamInitialized,
    bool SteamUserLoggedOn,
    uint? SteamAppId,
    string? SubmitResult,
    bool UserNeedsToAcceptWorkshopLegalAgreement,
    bool TimedOut,
    string Message);

public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    bool TimedOut,
    bool StdoutTruncated,
    bool StderrTruncated,
    long StdoutChars,
    long StderrChars);
