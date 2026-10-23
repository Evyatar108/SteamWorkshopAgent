using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class SteamworksPublisher(
    SteamEnvironment steamEnvironment,
    ProcessRunner processRunner,
    WorkshopPublishRequestStore requestStore)
{
    private static readonly SemaphoreSlim SteamworksLock = new(1, 1);

    public async Task<SteamSessionProbeResult> ProbeAsync()
    {
        var nativeLibraryPath = steamEnvironment.FindSteamworksNativeLibrary();
        if (nativeLibraryPath == null)
            return new SteamSessionProbeResult(
                "steamworks-standalone", false, false, false, null, null, null, false,
                "RimWorld's native Steamworks library was not found.");

        var result = await RunHelperAsync(
            ["steamworks-session-probe-internal", nativeLibraryPath],
            TimeSpan.FromSeconds(30));
        return ReadHelperResult<SteamSessionProbeResult>(result)
            ?? new SteamSessionProbeResult(
                "steamworks-standalone", false, false, false, null, null, nativeLibraryPath, false,
                $"Steamworks session helper failed with exit code {result.ExitCode}. {Truncate(result.Stderr, 2000)}");
    }

    public async Task<WorkshopPublishBackendResult> PublishPreparedAsync(string requestPath)
    {
        PublishRequestClaim.ThrowIfExists(requestPath);
        var request = await requestStore.ReadAndValidateAsync(requestPath);
        var nativeLibraryPath = steamEnvironment.FindSteamworksNativeLibrary();
        if (nativeLibraryPath == null)
            return Failure(request, requestPath, "preflight", false, false, false, null, null,
                "RimWorld's native Steamworks library was not found.");

        await SteamworksLock.WaitAsync();
        try
        {
            var result = await RunHelperAsync(
                ["steamworks-publish-internal", requestPath, nativeLibraryPath],
                TimeSpan.FromMinutes(16));
            var helperResult = ReadHelperResult<WorkshopPublishBackendResult>(result);
            if (helperResult != null)
                return helperResult;

            var persistedResult = await TryReadPersistedResultAsync(request.ResultPath);
            if (persistedResult != null)
                return persistedResult.FallbackAllowed && File.Exists(PublishRequestClaim.ClaimPath(requestPath))
                    ? persistedResult with
                    {
                        FallbackAllowed = false,
                        Message = persistedResult.Message + " The helper did not hand back its claim. Read back the Workshop item."
                    }
                    : persistedResult;

            var claimed = File.Exists(PublishRequestClaim.ClaimPath(requestPath));
            var failure = Failure(
                request,
                requestPath,
                result.TimedOut ? "helper-timeout" : "helper-failure",
                submissionStarted: false,
                steamInitialized: false,
                steamUserLoggedOn: false,
                steamId: null,
                steamAppId: null,
                $"Steamworks publish helper failed with exit code {result.ExitCode}. STDOUT: {Truncate(result.Stdout, 2000)} STDERR: {Truncate(result.Stderr, 2000)}",
                fallbackAllowed: !claimed);
            return claimed ? failure with
            {
                OutcomeDefinitive = false,
                Message = failure.Message + " The request was claimed. Read back the Workshop item; do not resubmit."
            } : failure;
        }
        finally
        {
            SteamworksLock.Release();
        }
    }

    public SteamSessionProbeResult ProbeInCurrentProcess(string nativeLibraryPath)
    {
        var detached = ProcessIsolation.TryDetachFromControllingTerminal(out var isolationMessage);
        if (!detached)
            return new SteamSessionProbeResult(
                "steamworks-standalone", false, false, false, null, null, nativeLibraryPath, false,
                $"Refusing to initialize Steamworks without a detached process session. {isolationMessage}");

        ConfigureSteamEnvironment();
        using var steam = new SteamworksNativeClient(nativeLibraryPath);
        if (!steam.Init())
            return new SteamSessionProbeResult(
                "steamworks-standalone", true, false, false, null, null, nativeLibraryPath, false,
                "SteamAPI_Init failed. Steam may not be running or may not recognize this helper as RimWorld.");

        try
        {
            var loggedOn = steam.UserLoggedOn();
            var steamId = steam.GetSteamId();
            var appId = steam.GetAppId();
            var ready = loggedOn && steamId != 0 && appId == AgentPaths.RimWorldAppId;
            var desktopSessionOffline = !loggedOn && steamId != 0 && appId == AgentPaths.RimWorldAppId;
            return new SteamSessionProbeResult(
                "steamworks-standalone", true, true, loggedOn, steamId == 0 ? null : steamId, appId,
                nativeLibraryPath, ready,
                ready
                    ? "The detached helper is authenticated through the Steam desktop session and ready to publish."
                    : desktopSessionOffline
                        ? SteamSessionRecovery.NotLoggedOnMessage("The initialized desktop Steam session")
                        : $"Steamworks initialized but is not publish-ready. LoggedOn={loggedOn}; SteamId={steamId}; AppId={appId}.",
                FailureCode: desktopSessionOffline ? SteamSessionRecovery.DesktopSessionOffline : null,
                RecoveryAction: desktopSessionOffline ? SteamSessionRecovery.RestartSteam : null);
        }
        finally
        {
            steam.Shutdown();
        }
    }

    public Task<WorkshopPublishBackendResult> PublishInCurrentProcessAsync(
        string requestPath,
        string nativeLibraryPath)
        => PublishClaimedAsync(requestPath,
            request => PublishNativeAsync(requestPath, nativeLibraryPath, request));

    internal async Task<WorkshopPublishBackendResult> PublishClaimedAsync(
        string requestPath, Func<WorkshopPublishRequest, Task<WorkshopPublishBackendResult>> submit)
    {
        using var claim = PublishRequestClaim.Acquire(requestPath, requestStore.RunsDirectory);
        var request = await requestStore.ReadAndValidateAsync(requestPath);
        var result = await submit(request);
        var provenPreSubmit = PublishReplayGuard.IsProvenPreSubmitFailure(
            result.Stage, result.Success, result.SubmissionStarted, result.OutcomeDefinitive);
        if (!provenPreSubmit && result.FallbackAllowed)
            result = result with
            {
                FallbackAllowed = false,
                Message = result.Message + " The outcome does not prove submission never started. Read back the Workshop item."
            };
        // Final persistence occurs after native cleanup. Intermediate markers
        // remain persisted inside the backend; a cleanup exception cannot hand back ownership.
        await claim.PersistResultAsync(result.Stage, result.Success, result.SubmissionStarted,
            result.OutcomeDefinitive, () => requestStore.WriteResultAsync(request.ResultPath, result));
        return result;
    }

    private async Task<WorkshopPublishBackendResult> PublishNativeAsync(
        string requestPath, string nativeLibraryPath, WorkshopPublishRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!ProcessIsolation.TryDetachFromControllingTerminal(out var isolationMessage))
                return await PersistAsync(Failure(
                    request, requestPath, "process-isolation", false, false, false, null, null,
                    $"Refusing to initialize Steamworks without a detached process session. {isolationMessage}", stopwatch.ElapsedMilliseconds));

            ConfigureSteamEnvironment();
            using var steam = new SteamworksNativeClient(nativeLibraryPath);
            if (!steam.Init())
                return await PersistAsync(Failure(
                    request, requestPath, "steam-init", false, false, false, null, null,
                    "SteamAPI_Init failed. The in-game RimBridge fallback may still use Steam's authenticated RimWorld session.", stopwatch.ElapsedMilliseconds));

            try
            {
                var loggedOn = steam.UserLoggedOn();
                var steamId = steam.GetSteamId();
                var appId = steam.GetAppId();
                if (!loggedOn)
                    return await PersistAsync(Failure(
                        request, requestPath, "session", false, true, false, steamId, appId,
                        SteamSessionRecovery.NotLoggedOnMessage("The initialized desktop Steam session"),
                        stopwatch.ElapsedMilliseconds,
                        failureCode: SteamSessionRecovery.DesktopSessionOffline,
                        recoveryAction: SteamSessionRecovery.RestartSteam));
                if (steamId != request.ExpectedCreatorSteamId)
                    return await PersistAsync(Failure(
                        request, requestPath, "ownership", false, true, true, steamId, appId,
                        $"Refusing to publish as Steam account {steamId}; Workshop item creator is {request.ExpectedCreatorSteamId}.", stopwatch.ElapsedMilliseconds,
                        fallbackAllowed: false));
                if (appId != request.AppId)
                    return await PersistAsync(Failure(
                        request, requestPath, "app-id", false, true, true, steamId, appId,
                        $"Refusing to publish through unexpected Steam app id {appId}.", stopwatch.ElapsedMilliseconds));

                var updateHandle = steam.StartItemUpdate(request.AppId, request.PublishedFileId);
                if (updateHandle is 0 or ulong.MaxValue)
                    return await PersistAsync(Failure(
                        request, requestPath, "start-update", false, true, true, steamId, appId,
                        "SteamUGC.StartItemUpdate returned an invalid handle. No submission was started.", stopwatch.ElapsedMilliseconds));

                if (!steam.SetItemTitle(updateHandle, request.Title))
                    return await PersistAsync(Failure(request, requestPath, "set-title", false, true, true, steamId, appId,
                        "SteamUGC.SetItemTitle returned false. No submission was started.", stopwatch.ElapsedMilliseconds));
                if (request.UpdateDescription && !steam.SetItemDescription(updateHandle, request.Description ?? ""))
                    return await PersistAsync(Failure(request, requestPath, "set-description", false, true, true, steamId, appId,
                        "SteamUGC.SetItemDescription returned false. No submission was started.", stopwatch.ElapsedMilliseconds));
                if (!steam.SetItemPreview(updateHandle, request.PreviewFile))
                    return await PersistAsync(Failure(request, requestPath, "set-preview", false, true, true, steamId, appId,
                        "SteamUGC.SetItemPreview returned false. No submission was started.", stopwatch.ElapsedMilliseconds));
                if (!steam.SetItemContent(updateHandle, request.ContentFolder))
                    return await PersistAsync(Failure(request, requestPath, "set-content", false, true, true, steamId, appId,
                        "SteamUGC.SetItemContent returned false. No submission was started.", stopwatch.ElapsedMilliseconds));
                if (request.Visibility is { } visibility && !steam.SetItemVisibility(updateHandle, visibility))
                    return await PersistAsync(Failure(request, requestPath, "set-visibility", false, true, true, steamId, appId,
                        "SteamUGC.SetItemVisibility returned false. No submission was started.", stopwatch.ElapsedMilliseconds));

                await PersistAsync(new WorkshopPublishBackendResult(
                    "steamworks-standalone",
                    "submit-intent",
                    Success: false,
                    SubmissionStarted: false,
                    OutcomeDefinitive: false,
                    FallbackAllowed: false,
                    SteamInitialized: true,
                    SteamUserLoggedOn: true,
                    steamId,
                    appId,
                    request.PublishedFileId,
                    SubmitResult: null,
                    UserNeedsToAcceptWorkshopLegalAgreement: false,
                    UploadStatus: null,
                    BytesProcessed: 0,
                    BytesTotal: 0,
                    stopwatch.ElapsedMilliseconds,
                    requestPath,
                    request.ResultPath,
                    WorkshopUrl(request.PublishedFileId),
                    "SubmitItemUpdate is about to be called. Automatic fallback is disabled until a definitive result is persisted."));

                var apiCall = steam.SubmitItemUpdate(updateHandle, request.ChangeNote);
                if (apiCall == 0)
                    return await PersistAsync(Failure(request, requestPath, "submit", false, true, true, steamId, appId,
                        "SteamUGC.SubmitItemUpdate returned an invalid API call handle. No submission was started.", stopwatch.ElapsedMilliseconds));

                await PersistAsync(new WorkshopPublishBackendResult(
                    "steamworks-standalone",
                    "submitted",
                    Success: false,
                    SubmissionStarted: true,
                    OutcomeDefinitive: false,
                    FallbackAllowed: false,
                    SteamInitialized: true,
                    SteamUserLoggedOn: true,
                    steamId,
                    appId,
                    request.PublishedFileId,
                    SubmitResult: null,
                    UserNeedsToAcceptWorkshopLegalAgreement: false,
                    UploadStatus: null,
                    BytesProcessed: 0,
                    BytesTotal: 0,
                    stopwatch.ElapsedMilliseconds,
                    requestPath,
                    request.ResultPath,
                    WorkshopUrl(request.PublishedFileId),
                    "SubmitItemUpdate returned a valid API call handle. Verify state and never fall back automatically unless a definitive result replaces this marker."));

                var deadline = DateTime.UtcNow.AddMinutes(15);
                var progress = steam.GetItemUpdateProgress(updateHandle);
                while (DateTime.UtcNow < deadline)
                {
                    steam.RunCallbacks();
                    if (steam.TryGetSubmitItemUpdateResult(apiCall, out var submit, out var ioFailure))
                    {
                        progress = steam.GetItemUpdateProgress(updateHandle);
                        var submitResult = SteamworksNativeClient.FormatResult(submit.Result);
                        var success = !ioFailure && submit.Result == 1;
                        return await PersistAsync(new WorkshopPublishBackendResult(
                            "steamworks-standalone",
                            "completed",
                            success,
                            SubmissionStarted: true,
                            OutcomeDefinitive: true,
                            FallbackAllowed: false,
                            SteamInitialized: true,
                            SteamUserLoggedOn: true,
                            steamId,
                            appId,
                            request.PublishedFileId,
                            submitResult,
                            submit.UserNeedsToAcceptWorkshopLegalAgreement,
                            progress.Status,
                            progress.BytesProcessed,
                            progress.BytesTotal,
                            stopwatch.ElapsedMilliseconds,
                            requestPath,
                            request.ResultPath,
                            WorkshopUrl(request.PublishedFileId),
                            success
                                ? "Workshop content update completed through the detached Steamworks helper."
                                : $"SteamUGC.SubmitItemUpdate returned {submitResult}; IOFailure={ioFailure}."));
                    }

                    progress = steam.GetItemUpdateProgress(updateHandle);
                    await Task.Delay(100);
                }

                return await PersistAsync(new WorkshopPublishBackendResult(
                    "steamworks-standalone",
                    "callback-timeout",
                    Success: false,
                    SubmissionStarted: true,
                    OutcomeDefinitive: false,
                    FallbackAllowed: false,
                    SteamInitialized: true,
                    SteamUserLoggedOn: true,
                    steamId,
                    appId,
                    request.PublishedFileId,
                    SubmitResult: null,
                    UserNeedsToAcceptWorkshopLegalAgreement: false,
                    progress.Status,
                    progress.BytesProcessed,
                    progress.BytesTotal,
                    stopwatch.ElapsedMilliseconds,
                    requestPath,
                    request.ResultPath,
                    WorkshopUrl(request.PublishedFileId),
                    "The upload callback timed out after submission started. Verify Workshop state; do not retry automatically."));
            }
            finally
            {
                steam.Shutdown();
            }
        }
        catch (Exception exception)
        {
            // Once SubmitItemUpdate may have been reached, preserve the durable
            // no-fallback marker. Treating an exception as a pre-submit failure
            // could submit the same update twice through another backend.
            var persistedResult = await TryReadPersistedResultAsync(request.ResultPath);
            if (persistedResult is { FallbackAllowed: false })
                return await PersistAsync(persistedResult with
                {
                    Stage = $"{persistedResult.Stage}-exception",
                    Message = $"{persistedResult.Message} Helper exception: {exception.Message}"
                });

            return await PersistAsync(Failure(
                request, requestPath, "exception", false, false, false, null, null,
                exception.Message, stopwatch.ElapsedMilliseconds, fallbackAllowed: false) with
            {
                OutcomeDefinitive = false
            });
        }
    }

    private async Task<WorkshopPublishBackendResult> PersistAsync(WorkshopPublishBackendResult result)
    {
        await requestStore.WriteResultAsync(result.ResultPath, result);
        return result;
    }

    private async Task<ProcessResult> RunHelperAsync(IReadOnlyList<string> internalArgs, TimeSpan timeout)
    {
        var (fileName, args) = CreateHelperInvocation(internalArgs);
        return await processRunner.RunAsync(fileName, args, timeout: timeout);
    }

    private static (string FileName, string[] Args) CreateHelperInvocation(IReadOnlyList<string> internalArgs)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate current executable for the Steamworks helper.");
        if (!Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (processPath, internalArgs.ToArray());

        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? throw new InvalidOperationException("Cannot locate current assembly for the Steamworks helper.");
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException($"Cannot locate current assembly for the Steamworks helper: {assemblyPath}");
        return (processPath, [assemblyPath, .. internalArgs]);
    }

    private static T? ReadHelperResult<T>(ProcessResult result)
    {
        foreach (var line in result.Stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{\"status\":", StringComparison.Ordinal))
                continue;

            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
                continue;
            if (!root.TryGetProperty("data", out var data))
                continue;
            return data.Deserialize<T>(ToolJson.Options);
        }

        return default;
    }

    private static async Task<WorkshopPublishBackendResult?> TryReadPersistedResultAsync(string resultPath)
    {
        if (!File.Exists(resultPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<WorkshopPublishBackendResult>(
                await File.ReadAllTextAsync(resultPath),
                ToolJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private static void ConfigureSteamEnvironment()
    {
        var appId = AgentPaths.RimWorldAppId.ToString();
        Environment.SetEnvironmentVariable("SteamAppId", appId);
        Environment.SetEnvironmentVariable("SteamGameId", appId);
        Environment.SetEnvironmentVariable("SteamOverlayGameId", appId);
        Directory.CreateDirectory(AgentPaths.SteamworksDirectory);
        File.WriteAllText(Path.Combine(AgentPaths.SteamworksDirectory, "steam_appid.txt"), appId);
        Environment.CurrentDirectory = AgentPaths.SteamworksDirectory;
    }

    private static WorkshopPublishBackendResult Failure(
        WorkshopPublishRequest request,
        string requestPath,
        string stage,
        bool submissionStarted,
        bool steamInitialized,
        bool steamUserLoggedOn,
        ulong? steamId,
        uint? steamAppId,
        string message,
        long durationMs = 0,
        bool? fallbackAllowed = null,
        string? failureCode = null,
        string? recoveryAction = null)
    {
        return new WorkshopPublishBackendResult(
            "steamworks-standalone",
            stage,
            Success: false,
            submissionStarted,
            OutcomeDefinitive: !submissionStarted,
            FallbackAllowed: fallbackAllowed ?? !submissionStarted,
            steamInitialized,
            steamUserLoggedOn,
            steamId,
            steamAppId,
            request.PublishedFileId,
            SubmitResult: null,
            UserNeedsToAcceptWorkshopLegalAgreement: false,
            UploadStatus: null,
            BytesProcessed: 0,
            BytesTotal: 0,
            durationMs,
            requestPath,
            request.ResultPath,
            WorkshopUrl(request.PublishedFileId),
            message,
            Plan: null,
            FailureCode: failureCode,
            RecoveryAction: recoveryAction);
    }

    private static string WorkshopUrl(ulong publishedFileId)
    {
        return $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}";
    }

    private static string Truncate(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value[^maxChars..];
    }
}
