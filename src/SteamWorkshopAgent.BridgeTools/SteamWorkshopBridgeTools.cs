using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RimBridgeServer.Sdk;
using Steamworks;

namespace SteamWorkshopAgent.BridgeTools;

public sealed class SteamWorkshopBridgeTools
{
    private const uint RimWorldAppId = 294100;
    private const string DesktopSessionOffline = "desktop-session-offline";
    private const string RestartSteam = "restart-steam";
    private static readonly SemaphoreSlim PublishLock = new SemaphoreSlim(1, 1);

    [Tool(
        "steam_workshop/session_probe",
        Description = "Read whether RimWorld's current Steamworks session is logged on and ready for Workshop publishing.",
        Tags = new[] { "diagnostic", "read-only" })]
    public static async Task<object> SessionProbe(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken)
    {
        return await ctx.MainThread.InvokeAsync(() =>
        {
            var loggedOn = SteamUser.BLoggedOn();
            var steamId = SteamUser.GetSteamID().m_SteamID;
            var appId = SteamUtils.GetAppID().m_AppId;
            return (object)new
            {
                backend = "rimworld-steamworks",
                steamInitialized = true,
                steamUserLoggedOn = loggedOn,
                steamId,
                steamAppId = appId,
                ready = loggedOn && steamId != 0 && appId == RimWorldAppId,
                message = loggedOn && steamId != 0 && appId == RimWorldAppId
                    ? "RimWorld's Steamworks session is ready to publish."
                    : !loggedOn && steamId != 0 && appId == RimWorldAppId
                        ? NotLoggedOnMessage("RimWorld's initialized Steamworks session")
                        : "RimWorld's Steamworks session is not publish-ready.",
                failureCode = !loggedOn && steamId != 0 && appId == RimWorldAppId
                    ? DesktopSessionOffline
                    : null,
                recoveryAction = !loggedOn && steamId != 0 && appId == RimWorldAppId
                    ? RestartSteam
                    : null
            };
        }, cancellationToken);
    }

    [Tool(
        "steam_workshop/publish_prepared_update",
        Description = "Publish an owner-verified SteamWorkshopAgent request through RimWorld's authenticated Steamworks session.",
        ResultDescription = "A definitive Steamworks result, or an explicit ambiguous result when submission started but no callback completed.")]
    public static async Task<object> PublishPreparedUpdate(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Absolute path to steamworks-request.json under the SteamWorkshopAgent runs directory.")] string requestPath,
        [ToolParameter(Description = "Exact request id from the prepared request.")] string requestId,
        [ToolParameter(Description = "Must be true to submit the Workshop update.", Required = false, DefaultValue = false)] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
            throw new InvalidOperationException("confirm=true is required to submit a prepared Workshop update.");

        using var claim = PublishRequestClaim.Acquire(requestPath,
            Path.Combine(AgentDataPath.GetAppSupportDirectory(), "runs"));
        await PublishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var request = await ReadAndValidateRequestAsync(requestPath, requestId).ConfigureAwait(false);
            var session = await ctx.MainThread.InvokeAsync(ReadSession, cancellationToken).ConfigureAwait(false);
            if (!session.LoggedOn)
                return await PersistAsync(claim, request, Failure(requestPath, request, "session", false, session,
                    NotLoggedOnMessage("RimWorld's initialized Steamworks session"), stopwatch.ElapsedMilliseconds,
                    failureCode: DesktopSessionOffline,
                    recoveryAction: RestartSteam)).ConfigureAwait(false);
            if (session.SteamId != request.ExpectedCreatorSteamId)
                return await PersistAsync(claim, request, Failure(requestPath, request, "ownership", false, session,
                    $"Refusing to publish as Steam account {session.SteamId}; Workshop item creator is {request.ExpectedCreatorSteamId}.",
                    stopwatch.ElapsedMilliseconds, fallbackAllowed: false)).ConfigureAwait(false);
            if (session.AppId != request.AppId)
                return await PersistAsync(claim, request, Failure(requestPath, request, "app-id", false, session,
                    $"Refusing to publish through unexpected Steam app id {session.AppId}.", stopwatch.ElapsedMilliseconds)).ConfigureAwait(false);

            UGCUpdateHandle_t updateHandle = UGCUpdateHandle_t.Invalid;
            SteamAPICall_t apiCall = SteamAPICall_t.Invalid;
            var callbackCompletion = new TaskCompletionSource<SubmitCompletion>();
            CallResult<SubmitItemUpdateResult_t> callResult = null;

            var setupFailure = await ctx.MainThread.InvokeAsync(() =>
            {
                updateHandle = SteamUGC.StartItemUpdate(
                    new AppId_t(request.AppId),
                    new PublishedFileId_t(request.PublishedFileId));
                if (updateHandle == UGCUpdateHandle_t.Invalid)
                    return "SteamUGC.StartItemUpdate returned an invalid handle.";
                if (!SteamUGC.SetItemTitle(updateHandle, request.Title))
                    return "SteamUGC.SetItemTitle returned false.";
                if (request.UpdateDescription && !SteamUGC.SetItemDescription(updateHandle, request.Description ?? ""))
                    return "SteamUGC.SetItemDescription returned false.";
                if (!SteamUGC.SetItemPreview(updateHandle, request.PreviewFile))
                    return "SteamUGC.SetItemPreview returned false.";
                if (!SteamUGC.SetItemContent(updateHandle, request.ContentFolder))
                    return "SteamUGC.SetItemContent returned false.";
                if (request.Visibility.HasValue && !SteamUGC.SetItemVisibility(updateHandle, (ERemoteStoragePublishedFileVisibility)request.Visibility.Value))
                    return "SteamUGC.SetItemVisibility returned false.";
                return null;
            }, cancellationToken).ConfigureAwait(false);

            if (setupFailure != null)
            {
                callResult?.Dispose();
                return await PersistAsync(claim, request, Failure(requestPath, request, "setup", false, session,
                    setupFailure + " No submission was started.", stopwatch.ElapsedMilliseconds)).ConfigureAwait(false);
            }

            await PersistAsync(claim, request, Uncertain(
                requestPath, request, "submit-intent", false, session, stopwatch.ElapsedMilliseconds,
                "SubmitItemUpdate is about to be called. Automatic fallback is disabled until a definitive result is persisted.")).ConfigureAwait(false);

            var submitFailure = await ctx.MainThread.InvokeAsync(() =>
            {
                callResult = CallResult<SubmitItemUpdateResult_t>.Create((result, ioFailure) =>
                    callbackCompletion.TrySetResult(new SubmitCompletion(result, ioFailure)));
                apiCall = SteamUGC.SubmitItemUpdate(updateHandle, request.ChangeNote);
                if (apiCall == SteamAPICall_t.Invalid)
                    return "SteamUGC.SubmitItemUpdate returned an invalid API call handle.";
                callResult.Set(apiCall);
                return null;
            }, CancellationToken.None).ConfigureAwait(false);

            if (submitFailure != null)
            {
                callResult?.Dispose();
                return await PersistAsync(claim, request, Failure(requestPath, request, "submit", false, session,
                    submitFailure + " No submission was started.", stopwatch.ElapsedMilliseconds)).ConfigureAwait(false);
            }

            await PersistAsync(claim, request, Uncertain(
                requestPath, request, "submitted", true, session, stopwatch.ElapsedMilliseconds,
                "SubmitItemUpdate returned a valid API call handle. Verify state and never fall back automatically unless a definitive result replaces this marker.")).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddMinutes(15);
            var progress = new UploadProgress();
            try
            {
                while (!callbackCompletion.Task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    await ctx.Game.NextFrameAsync(CancellationToken.None).ConfigureAwait(false);
                    progress = await ctx.MainThread.InvokeAsync(() => ReadProgress(updateHandle), CancellationToken.None).ConfigureAwait(false);
                }

                if (!callbackCompletion.Task.IsCompleted)
                {
                    return await PersistAsync(claim, request, new PublishResult
                    {
                        Backend = "rimworld-steamworks",
                        Stage = "callback-timeout",
                        Success = false,
                        SubmissionStarted = true,
                        OutcomeDefinitive = false,
                        FallbackAllowed = false,
                        SteamInitialized = true,
                        SteamUserLoggedOn = true,
                        SteamId = session.SteamId,
                        SteamAppId = session.AppId,
                        PublishedFileId = request.PublishedFileId,
                        UploadStatus = progress.Status,
                        BytesProcessed = progress.Processed,
                        BytesTotal = progress.Total,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        RequestPath = requestPath,
                        ResultPath = request.ResultPath,
                        WorkshopUrl = WorkshopUrl(request.PublishedFileId),
                        Message = "The upload callback timed out after submission started. Verify Workshop state; do not retry automatically."
                    }).ConfigureAwait(false);
                }

                var completion = await callbackCompletion.Task.ConfigureAwait(false);
                progress = await ctx.MainThread.InvokeAsync(() => ReadProgress(updateHandle), CancellationToken.None).ConfigureAwait(false);
                var success = !completion.IoFailure && completion.Result.m_eResult == EResult.k_EResultOK;
                return await PersistAsync(claim, request, new PublishResult
                {
                    Backend = "rimworld-steamworks",
                    Stage = "completed",
                    Success = success,
                    SubmissionStarted = true,
                    OutcomeDefinitive = true,
                    FallbackAllowed = false,
                    SteamInitialized = true,
                    SteamUserLoggedOn = true,
                    SteamId = session.SteamId,
                    SteamAppId = session.AppId,
                    PublishedFileId = request.PublishedFileId,
                    SubmitResult = completion.Result.m_eResult.ToString(),
                    UserNeedsToAcceptWorkshopLegalAgreement = completion.Result.m_bUserNeedsToAcceptWorkshopLegalAgreement,
                    UploadStatus = progress.Status,
                    BytesProcessed = progress.Processed,
                    BytesTotal = progress.Total,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    RequestPath = requestPath,
                    ResultPath = request.ResultPath,
                    WorkshopUrl = WorkshopUrl(request.PublishedFileId),
                    Message = success
                        ? "Workshop content update completed through RimWorld's Steamworks session."
                        : $"SteamUGC.SubmitItemUpdate returned {completion.Result.m_eResult}; IOFailure={completion.IoFailure}."
                }).ConfigureAwait(false);
            }
            finally
            {
                callResult.Dispose();
            }
        }
        finally
        {
            PublishLock.Release();
        }
    }

    private static SessionState ReadSession()
    {
        return new SessionState
        {
            LoggedOn = SteamUser.BLoggedOn(),
            SteamId = SteamUser.GetSteamID().m_SteamID,
            AppId = SteamUtils.GetAppID().m_AppId
        };
    }

    private static UploadProgress ReadProgress(UGCUpdateHandle_t updateHandle)
    {
        ulong processed;
        ulong total;
        var status = SteamUGC.GetItemUpdateProgress(updateHandle, out processed, out total);
        return new UploadProgress { Status = status.ToString(), Processed = processed, Total = total };
    }

    private static async Task<PublishRequest> ReadAndValidateRequestAsync(string requestPath, string requestId)
    {
        var fullPath = Path.GetFullPath(requestPath);
        var runsRoot = Path.GetFullPath(Path.Combine(
            AgentDataPath.GetAppSupportDirectory(), "runs")) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(runsRoot, AgentDataPath.PathComparison))
            throw new InvalidOperationException("Workshop publish requests must be stored under the SteamWorkshopAgent runs directory.");

        var request = JsonConvert.DeserializeObject<PublishRequest>(File.ReadAllText(fullPath));
        if (request == null || request.SchemaVersion != 1)
            throw new InvalidOperationException("The Workshop publish request is invalid or unsupported.");
        if (!string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied request id does not match the prepared request.");
        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The Workshop publish request has expired.");
        if (request.AppId != RimWorldAppId || request.PublishedFileId == 0 || request.ExpectedCreatorSteamId == 0)
            throw new InvalidOperationException("The Workshop request contains invalid app, item, or creator ids.");
        if (!Directory.Exists(request.ContentFolder) || !File.Exists(request.PreviewFile))
            throw new InvalidOperationException("The prepared content folder or preview file no longer exists.");
        if (!Path.GetFullPath(request.ResultPath).StartsWith(runsRoot, AgentDataPath.PathComparison))
            throw new InvalidOperationException("The Workshop result path is outside the SteamWorkshopAgent runs directory.");
        if (File.Exists(request.ResultPath))
        {
            PublishResult previous;
            try
            {
                previous = JsonConvert.DeserializeObject<PublishResult>(File.ReadAllText(request.ResultPath));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The previous publish result is unreadable. Read back the Workshop item instead of resubmitting.",
                    exception);
            }
            PublishReplayGuard.EnsureCanSubmit(
                previous?.Stage, previous?.Success ?? false,
                previous?.SubmissionStarted ?? false, previous?.OutcomeDefinitive ?? false);
        }

        var digest = await ComputeContentDigestAsync(request.ContentFolder).ConfigureAwait(false);
        if (!string.Equals(digest, request.ContentDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Workshop content changed after the publish request was prepared.");
        return request;
    }

    private static async Task<string> ComputeContentDigestAsync(string contentFolder)
    {
        var root = Path.GetFullPath(contentFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Relative = path.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')
            })
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToArray();

        using (var hash = SHA256.Create())
        {
            foreach (var file in files)
            {
                var relative = Encoding.UTF8.GetBytes(file.Relative);
                hash.TransformBlock(relative, 0, relative.Length, relative, 0);
                var separator = new byte[] { 0 };
                hash.TransformBlock(separator, 0, separator.Length, separator, 0);

                using (var stream = File.OpenRead(file.Path))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        hash.TransformBlock(buffer, 0, read, buffer, 0);
                }
            }

            hash.TransformFinalBlock(new byte[0], 0, 0);
            return string.Concat(hash.Hash.Select(value => value.ToString("x2")));
        }
    }

    private static async Task<PublishResult> PersistAsync(
        PublishRequestClaim claim, PublishRequest request, PublishResult result)
    {
        if (result.FallbackAllowed && !PublishReplayGuard.IsProvenPreSubmitFailure(
                result.Stage, result.Success, result.SubmissionStarted, result.OutcomeDefinitive))
        {
            result.FallbackAllowed = false;
            result.Message += " The outcome does not prove submission never started. Read back the Workshop item.";
        }
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };
        await claim.PersistResultAsync(result.Stage, result.Success, result.SubmissionStarted,
            result.OutcomeDefinitive, () =>
            {
                using (var stream = new FileStream(request.ResultPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
                {
                    writer.Write(JsonConvert.SerializeObject(result, settings));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    chmod(request.ResultPath, Convert.ToUInt32("600", 8));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        return result;
    }

    private static PublishResult Failure(
        string requestPath,
        PublishRequest request,
        string stage,
        bool submissionStarted,
        SessionState session,
        string message,
        long durationMs,
        bool? fallbackAllowed = null,
        string failureCode = null,
        string recoveryAction = null)
    {
        return new PublishResult
        {
            Backend = "rimworld-steamworks",
            Stage = stage,
            Success = false,
            SubmissionStarted = submissionStarted,
            OutcomeDefinitive = !submissionStarted,
            FallbackAllowed = fallbackAllowed ?? !submissionStarted,
            SteamInitialized = true,
            SteamUserLoggedOn = session.LoggedOn,
            SteamId = session.SteamId,
            SteamAppId = session.AppId,
            PublishedFileId = request.PublishedFileId,
            DurationMs = durationMs,
            RequestPath = requestPath,
            ResultPath = request.ResultPath,
            WorkshopUrl = WorkshopUrl(request.PublishedFileId),
            Message = message,
            FailureCode = failureCode,
            RecoveryAction = recoveryAction
        };
    }

    private static PublishResult Uncertain(
        string requestPath,
        PublishRequest request,
        string stage,
        bool submissionStarted,
        SessionState session,
        long durationMs,
        string message)
    {
        return new PublishResult
        {
            Backend = "rimworld-steamworks",
            Stage = stage,
            Success = false,
            SubmissionStarted = submissionStarted,
            OutcomeDefinitive = false,
            FallbackAllowed = false,
            SteamInitialized = true,
            SteamUserLoggedOn = session.LoggedOn,
            SteamId = session.SteamId,
            SteamAppId = session.AppId,
            PublishedFileId = request.PublishedFileId,
            DurationMs = durationMs,
            RequestPath = requestPath,
            ResultPath = request.ResultPath,
            WorkshopUrl = WorkshopUrl(request.PublishedFileId),
            Message = message
        };
    }

    private static string WorkshopUrl(ulong publishedFileId)
    {
        return $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}";
    }

    private static string NotLoggedOnMessage(string subject)
    {
        return subject + " has no live connection to the Steam servers. "
            + "If Steam is visibly open, another login—especially SteamCMD using the same account—may have replaced the desktop session. "
            + "Fully quit and reopen Steam, wait for it to reconnect, and run the desktop session probe again before starting another RimWorld process or selecting SteamCMD fallback. "
            + "No submission was started.";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    private sealed class SubmitCompletion
    {
        public SubmitCompletion(SubmitItemUpdateResult_t result, bool ioFailure)
        {
            Result = result;
            IoFailure = ioFailure;
        }

        public SubmitItemUpdateResult_t Result { get; }
        public bool IoFailure { get; }
    }

    private sealed class SessionState
    {
        public bool LoggedOn { get; set; }
        public ulong SteamId { get; set; }
        public uint AppId { get; set; }
    }

    private sealed class UploadProgress
    {
        public string Status { get; set; }
        public ulong Processed { get; set; }
        public ulong Total { get; set; }
    }

    private sealed class PublishRequest
    {
        public int SchemaVersion { get; set; }
        public string RequestId { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public uint AppId { get; set; }
        public ulong PublishedFileId { get; set; }
        public ulong ExpectedCreatorSteamId { get; set; }
        public string ContentFolder { get; set; }
        public string PreviewFile { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public bool UpdateDescription { get; set; }
        public int? Visibility { get; set; }
        public string ChangeNote { get; set; }
        public string ContentDigest { get; set; }
        public string ResultPath { get; set; }
    }

    private sealed class PublishResult
    {
        public string Backend { get; set; }
        public string Stage { get; set; }
        public bool Success { get; set; }
        public bool SubmissionStarted { get; set; }
        public bool OutcomeDefinitive { get; set; }
        public bool FallbackAllowed { get; set; }
        public bool SteamInitialized { get; set; }
        public bool SteamUserLoggedOn { get; set; }
        public ulong SteamId { get; set; }
        public uint SteamAppId { get; set; }
        public ulong PublishedFileId { get; set; }
        public string SubmitResult { get; set; }
        public bool UserNeedsToAcceptWorkshopLegalAgreement { get; set; }
        public string UploadStatus { get; set; }
        public ulong BytesProcessed { get; set; }
        public ulong BytesTotal { get; set; }
        public long DurationMs { get; set; }
        public string RequestPath { get; set; }
        public string ResultPath { get; set; }
        public string WorkshopUrl { get; set; }
        public string Message { get; set; }
        public string FailureCode { get; set; }
        public string RecoveryAction { get; set; }
    }
}
