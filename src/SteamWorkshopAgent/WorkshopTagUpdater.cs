using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class WorkshopTagUpdater(SteamEnvironment steamEnvironment, ModInspector modInspector, ProcessRunner processRunner)
{
    private const string BackendName = "steamworks";
    private static readonly SemaphoreSlim SteamworksLock = new(1, 1);

    public async Task<object> SetTagsAsync(
        string modPathOrPublishedFileId,
        IReadOnlyList<string> tags,
        bool confirm,
        string changeNote = "Set Workshop tags")
    {
        var plan = await CreatePlanAsync(modPathOrPublishedFileId, tags, changeNote);
        if (!confirm)
            return plan;

        Validation.ThrowIfErrors(plan.ValidationIssues);
        return await SubmitUpdateViaHelperAsync(plan);
    }

    public async Task<object> SetChangeNoteAsync(
        string modPathOrPublishedFileId,
        string changeNote,
        bool confirm)
    {
        var plan = await CreateChangeNotePlanAsync(modPathOrPublishedFileId, changeNote);
        if (!confirm)
            return plan;

        Validation.ThrowIfErrors(plan.ValidationIssues);
        return await SubmitUpdateViaHelperAsync(plan);
    }

    public async Task<WorkshopTagUpdateResult> SetTagsAsync(
        ulong publishedFileId,
        IReadOnlyList<string> tags,
        bool confirm,
        string changeNote = "Set Workshop tags")
    {
        var normalizedTags = NormalizeTags(tags);
        var plan = CreatePlan(publishedFileId, normalizedTags, changeNote);
        if (!confirm)
            return new WorkshopTagUpdateResult(
                Success: false,
                publishedFileId,
                normalizedTags,
                plan.ChangeNote,
                BackendName,
                plan.NativeLibraryPath,
                SteamInitialized: false,
                SteamUserLoggedOn: false,
                SteamAppId: null,
                SubmitResult: null,
                UserNeedsToAcceptWorkshopLegalAgreement: false,
                TimedOut: false,
                Message: "Dry run only. Pass confirm=true to submit the Steamworks tag update.");

        Validation.ThrowIfErrors(plan.ValidationIssues);

        return await SubmitUpdateViaHelperAsync(plan);
    }

    public async Task<WorkshopTagUpdateResult> SetChangeNoteAsync(
        ulong publishedFileId,
        string changeNote,
        bool confirm)
    {
        var plan = CreatePlan(publishedFileId, [], changeNote, requireTags: false);
        if (!confirm)
            return new WorkshopTagUpdateResult(
                Success: false,
                publishedFileId,
                [],
                plan.ChangeNote,
                BackendName,
                plan.NativeLibraryPath,
                SteamInitialized: false,
                SteamUserLoggedOn: false,
                SteamAppId: null,
                SubmitResult: null,
                UserNeedsToAcceptWorkshopLegalAgreement: false,
                TimedOut: false,
                Message: "Dry run only. Pass confirm=true to submit the Steamworks changenote update.");

        Validation.ThrowIfErrors(plan.ValidationIssues);

        return await SubmitUpdateViaHelperAsync(plan);
    }

    public WorkshopTagUpdateResult SetTagsInCurrentProcess(
        ulong publishedFileId,
        IReadOnlyList<string> tags,
        string changeNote,
        string nativeLibraryPath)
    {
        var plan = new WorkshopTagUpdatePlan(
            publishedFileId,
            NormalizeTags(tags),
            changeNote,
            AgentPaths.RimWorldAppId,
            nativeLibraryPath,
            []);

        return SubmitUpdate(plan);
    }

    public async Task<WorkshopTagUpdatePlan> CreatePlanAsync(
        string modPathOrPublishedFileId,
        IReadOnlyList<string> tags,
        string changeNote = "Set Workshop tags")
    {
        if (ulong.TryParse(modPathOrPublishedFileId, out var directId))
            return CreatePlan(directId, NormalizeTags(tags), changeNote);

        var mod = await modInspector.InspectAsync(modPathOrPublishedFileId);
        var resolvedTags = tags.Count > 0
            ? NormalizeTags(tags)
            : WorkshopPlanner.CreateDefaultTags(mod);

        return CreatePlan(
            mod.PublishedFileId.GetValueOrDefault(),
            resolvedTags,
            changeNote,
            requirePublishedFileId: true);
    }

    public async Task<WorkshopTagUpdatePlan> CreateChangeNotePlanAsync(
        string modPathOrPublishedFileId,
        string changeNote)
    {
        if (ulong.TryParse(modPathOrPublishedFileId, out var directId))
            return CreatePlan(directId, [], changeNote, requireTags: false);

        var mod = await modInspector.InspectAsync(modPathOrPublishedFileId);
        return CreatePlan(
            mod.PublishedFileId.GetValueOrDefault(),
            [],
            changeNote,
            requirePublishedFileId: true,
            requireTags: false);
    }

    private WorkshopTagUpdatePlan CreatePlan(
        ulong publishedFileId,
        IReadOnlyList<string> tags,
        string changeNote,
        bool requirePublishedFileId = false,
        bool requireTags = true)
    {
        var issues = new List<ValidationIssue>();
        if ((requirePublishedFileId || publishedFileId == 0) && publishedFileId == 0)
            issues.Add(new ValidationIssue("missing_published_file_id", "A nonzero Workshop published file id is required.", "error"));
        if (requireTags && tags.Count == 0)
            issues.Add(new ValidationIssue("missing_tags", "At least one Workshop tag is required.", "error"));
        if (string.IsNullOrWhiteSpace(changeNote))
            issues.Add(new ValidationIssue("missing_change_note", "A nonempty Workshop changenote is required.", "error"));

        var nativeLibraryPath = steamEnvironment.FindSteamworksNativeLibrary();
        if (nativeLibraryPath == null)
            issues.Add(new ValidationIssue("missing_steamworks_native_library", "RimWorld's libsteam_api.dylib was not found. Set STEAMWORKS_NATIVE_LIB to the native Steamworks library path.", "error"));

        return new WorkshopTagUpdatePlan(
            publishedFileId,
            tags,
            NormalizeChangeNote(changeNote),
            AgentPaths.RimWorldAppId,
            nativeLibraryPath,
            issues);
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeChangeNote(string changeNote)
    {
        return changeNote.Trim();
    }

    private async Task<WorkshopTagUpdateResult> SubmitUpdateViaHelperAsync(WorkshopTagUpdatePlan plan)
    {
        await SteamworksLock.WaitAsync();
        try
        {
            var (fileName, args) = CreateHelperInvocation(plan);
            var result = await processRunner.RunAsync(
                fileName,
                args,
                timeout: TimeSpan.FromMinutes(3));

            var helperResult = TryReadHelperResult(result.Stdout);
            if (helperResult != null)
                return helperResult;

            return Failure(
                plan,
                steamInitialized: false,
                steamUserLoggedOn: false,
                steamAppId: null,
                submitResult: null,
                userNeedsAgreement: false,
                timedOut: result.TimedOut,
                $"Steamworks update helper failed with exit code {result.ExitCode}. STDOUT: {Truncate(result.Stdout, 2000)} STDERR: {Truncate(result.Stderr, 2000)}");
        }
        finally
        {
            SteamworksLock.Release();
        }
    }

    private static (string FileName, string[] Args) CreateHelperInvocation(WorkshopTagUpdatePlan plan)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate current executable for Steamworks tag helper.");
        var internalArgs = new[]
        {
            "steamworks-set-tags-internal",
            plan.PublishedFileId.ToString(),
            EncodeJson(plan.Tags),
            EncodeJson(plan.ChangeNote),
            plan.NativeLibraryPath!
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("Cannot locate current assembly for Steamworks tag helper.");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
                throw new InvalidOperationException($"Cannot locate current assembly for Steamworks tag helper: {assemblyPath}");

            return (processPath, [assemblyPath, .. internalArgs]);
        }

        return (processPath, internalArgs);
    }

    private static WorkshopTagUpdateResult? TryReadHelperResult(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{\"status\":", StringComparison.Ordinal))
                continue;

            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
                return null;
            if (!root.TryGetProperty("data", out var data))
                return null;

            return data.Deserialize<WorkshopTagUpdateResult>(ToolJson.Options);
        }

        return null;
    }

    private static string EncodeJson<T>(T value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ToolJson.Options)));
    }

    public static T DecodeJson<T>(string encoded)
    {
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), ToolJson.Options)
            ?? throw new InvalidOperationException($"Failed to decode {typeof(T).Name}.");
    }

    private static WorkshopTagUpdateResult SubmitUpdate(WorkshopTagUpdatePlan plan)
    {
        if (!ProcessIsolation.TryDetachFromControllingTerminal(out var isolationMessage))
            return Failure(plan, false, false, null, null, false, false,
                $"Refusing to initialize Steamworks without a detached process session. {isolationMessage}");

        Environment.SetEnvironmentVariable("SteamAppId", plan.RimWorldAppId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", plan.RimWorldAppId.ToString());
        Environment.SetEnvironmentVariable("SteamOverlayGameId", plan.RimWorldAppId.ToString());

        var previousDirectory = Environment.CurrentDirectory;
        var steamAppIdDirectory = Path.Combine(AgentPaths.AppSupportDirectory, "steamworks");
        Directory.CreateDirectory(steamAppIdDirectory);
        File.WriteAllText(Path.Combine(steamAppIdDirectory, "steam_appid.txt"), plan.RimWorldAppId.ToString());
        Environment.CurrentDirectory = steamAppIdDirectory;

        try
        {
            using var steam = new SteamworksNative(plan.NativeLibraryPath!);
            if (!steam.Init())
                return Failure(plan, false, false, null, null, false, false, "SteamAPI_Init failed. Start Steam, log in, and make sure RimWorld is installed for this account.");

            try
            {
                var userLoggedOn = steam.UserLoggedOn();
                var steamAppId = steam.GetAppId();
                if (!userLoggedOn)
                    return Failure(plan, true, false, steamAppId, null, false, false, "Steamworks initialized, but SteamUser.BLoggedOn returned false. SteamCMD login is separate from this path; the desktop Steam client must be online and logged on.");

                var updateHandle = steam.StartItemUpdate(plan.RimWorldAppId, plan.PublishedFileId);
                if (updateHandle == 0)
                    return Failure(plan, true, true, steamAppId, null, false, false, "SteamUGC.StartItemUpdate returned handle 0.");

                if (plan.Tags.Count > 0 && !steam.SetItemTags(updateHandle, plan.Tags))
                    return Failure(plan, true, true, steamAppId, null, false, false, "SteamUGC.SetItemTags returned false.");

                var call = steam.SubmitItemUpdate(updateHandle, plan.ChangeNote);
                var deadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < deadline)
                {
                    steam.RunCallbacks();
                    if (steam.TryGetSubmitItemUpdateResult(call, out var submit, out var ioFailure))
                    {
                        var result = ((SteamResult)submit.Result).ToString();
                        var success = !ioFailure && submit.Result == (int)SteamResult.OK;
                        return new WorkshopTagUpdateResult(
                            success,
                            plan.PublishedFileId,
                            plan.Tags,
                            plan.ChangeNote,
                            BackendName,
                            plan.NativeLibraryPath,
                            SteamInitialized: true,
                            SteamUserLoggedOn: true,
                            steamAppId,
                            result,
                            submit.UserNeedsToAcceptWorkshopLegalAgreement,
                            TimedOut: false,
                            success
                                ? plan.Tags.Count > 0
                                    ? "Workshop tags and changenote submitted through Steamworks."
                                    : "Workshop changenote submitted through Steamworks."
                                : $"SteamUGC.SubmitItemUpdate returned {result}; IOFailure={ioFailure}.");
                    }

                    Thread.Sleep(100);
                }

                return Failure(plan, true, true, steamAppId, null, false, true, "Timed out waiting for SubmitItemUpdateResult_t.");
            }
            finally
            {
                steam.Shutdown();
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static WorkshopTagUpdateResult Failure(
        WorkshopTagUpdatePlan plan,
        bool steamInitialized,
        bool steamUserLoggedOn,
        uint? steamAppId,
        string? submitResult,
        bool userNeedsAgreement,
        bool timedOut,
        string message)
    {
        return new WorkshopTagUpdateResult(
            Success: false,
            plan.PublishedFileId,
            plan.Tags,
            plan.ChangeNote,
            BackendName,
            plan.NativeLibraryPath,
            steamInitialized,
            steamUserLoggedOn,
            steamAppId,
            submitResult,
            userNeedsAgreement,
            timedOut,
            message);
    }

    private static string Truncate(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value[^maxChars..];
    }

    private sealed class SteamworksNative : IDisposable
    {
        private readonly IntPtr library;
        private readonly SteamApiInit steamApiInit;
        private readonly SteamApiShutdown steamApiShutdown;
        private readonly SteamApiRunCallbacks steamApiRunCallbacks;
        private readonly SteamApiGetHSteamPipe steamApiGetHSteamPipe;
        private readonly SteamApiSteamUser steamApiSteamUser;
        private readonly SteamApiSteamUtils steamApiSteamUtils;
        private readonly SteamApiSteamUgc steamApiSteamUgc;
        private readonly SteamApiUserLoggedOn steamApiUserLoggedOn;
        private readonly SteamApiUtilsGetAppId steamApiUtilsGetAppId;
        private readonly SteamApiUgcStartItemUpdate steamApiUgcStartItemUpdate;
        private readonly SteamApiUgcSetItemTags steamApiUgcSetItemTags;
        private readonly SteamApiUgcSubmitItemUpdate steamApiUgcSubmitItemUpdate;
        private readonly SteamApiManualDispatchInit steamApiManualDispatchInit;
        private readonly SteamApiManualDispatchRunFrame steamApiManualDispatchRunFrame;
        private readonly SteamApiManualDispatchGetApiCallResult steamApiManualDispatchGetApiCallResult;

        public SteamworksNative(string nativeLibraryPath)
        {
            library = NativeLibrary.Load(nativeLibraryPath);
            steamApiInit = Load<SteamApiInit>("SteamAPI_Init");
            steamApiShutdown = Load<SteamApiShutdown>("SteamAPI_Shutdown");
            steamApiRunCallbacks = Load<SteamApiRunCallbacks>("SteamAPI_RunCallbacks");
            steamApiGetHSteamPipe = Load<SteamApiGetHSteamPipe>("SteamAPI_GetHSteamPipe");
            steamApiSteamUser = Load<SteamApiSteamUser>("SteamAPI_SteamUser_v021");
            steamApiSteamUtils = Load<SteamApiSteamUtils>("SteamAPI_SteamUtils_v010");
            steamApiSteamUgc = Load<SteamApiSteamUgc>("SteamAPI_SteamUGC_v016");
            steamApiUserLoggedOn = Load<SteamApiUserLoggedOn>("SteamAPI_ISteamUser_BLoggedOn");
            steamApiUtilsGetAppId = Load<SteamApiUtilsGetAppId>("SteamAPI_ISteamUtils_GetAppID");
            steamApiUgcStartItemUpdate = Load<SteamApiUgcStartItemUpdate>("SteamAPI_ISteamUGC_StartItemUpdate");
            steamApiUgcSetItemTags = Load<SteamApiUgcSetItemTags>("SteamAPI_ISteamUGC_SetItemTags");
            steamApiUgcSubmitItemUpdate = Load<SteamApiUgcSubmitItemUpdate>("SteamAPI_ISteamUGC_SubmitItemUpdate");
            steamApiManualDispatchInit = Load<SteamApiManualDispatchInit>("SteamAPI_ManualDispatch_Init");
            steamApiManualDispatchRunFrame = Load<SteamApiManualDispatchRunFrame>("SteamAPI_ManualDispatch_RunFrame");
            steamApiManualDispatchGetApiCallResult = Load<SteamApiManualDispatchGetApiCallResult>("SteamAPI_ManualDispatch_GetAPICallResult");
        }

        public bool Init()
        {
            var initialized = steamApiInit();
            if (initialized)
                steamApiManualDispatchInit();
            return initialized;
        }

        public void Shutdown() => steamApiShutdown();

        public bool UserLoggedOn() => steamApiUserLoggedOn(steamApiSteamUser());

        public uint GetAppId() => steamApiUtilsGetAppId(steamApiSteamUtils());

        public ulong StartItemUpdate(uint appId, ulong publishedFileId)
        {
            return steamApiUgcStartItemUpdate(steamApiSteamUgc(), appId, publishedFileId);
        }

        public bool SetItemTags(ulong updateHandle, IReadOnlyList<string> tags)
        {
            var stringPointers = tags.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            var arrayPointer = Marshal.AllocHGlobal(IntPtr.Size * stringPointers.Length);
            try
            {
                for (var i = 0; i < stringPointers.Length; i++)
                    Marshal.WriteIntPtr(arrayPointer, i * IntPtr.Size, stringPointers[i]);

                var tagArray = new SteamParamStringArray(arrayPointer, stringPointers.Length);
                return steamApiUgcSetItemTags(steamApiSteamUgc(), updateHandle, ref tagArray, false);
            }
            finally
            {
                foreach (var pointer in stringPointers)
                    Marshal.FreeCoTaskMem(pointer);
                Marshal.FreeHGlobal(arrayPointer);
            }
        }

        public ulong SubmitItemUpdate(ulong updateHandle, string changeNote)
        {
            return steamApiUgcSubmitItemUpdate(steamApiSteamUgc(), updateHandle, changeNote);
        }

        public void RunCallbacks()
        {
            steamApiRunCallbacks();
            steamApiManualDispatchRunFrame(steamApiGetHSteamPipe());
        }

        public bool TryGetSubmitItemUpdateResult(ulong apiCall, out SteamSubmitItemUpdateResult result, out bool ioFailure)
        {
            var size = SteamCallbackPacking.SubmitResultSize(OperatingSystem.IsWindows());
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var available = steamApiManualDispatchGetApiCallResult(
                    steamApiGetHSteamPipe(),
                    apiCall,
                    buffer,
                    size,
                    SteamSubmitItemUpdateResult.CallbackId,
                    out ioFailure);

                result = available
                    ? SteamCallbackPacking.ReadSubmitResult(buffer, OperatingSystem.IsWindows())
                    : default;
                return available;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            NativeLibrary.Free(library);
        }

        private T Load<T>(string name) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SteamParamStringArray(IntPtr Strings, int NumStrings);

    private enum SteamResult
    {
        OK = 1,
        Fail = 2,
        NoConnection = 3,
        InvalidPassword = 5,
        LoggedInElsewhere = 6,
        InvalidProtocolVer = 7,
        InvalidParam = 8,
        FileNotFound = 9,
        Busy = 10,
        InvalidState = 11,
        InvalidName = 12,
        InvalidEmail = 13,
        DuplicateName = 14,
        AccessDenied = 15,
        Timeout = 16,
        Banned = 17,
        AccountNotFound = 18,
        InvalidSteamID = 19,
        ServiceUnavailable = 20,
        NotLoggedOn = 21,
        Pending = 22,
        EncryptionFailure = 23,
        InsufficientPrivilege = 24,
        LimitExceeded = 25,
        Revoked = 26,
        Expired = 27,
        AlreadyRedeemed = 28,
        DuplicateRequest = 29,
        AlreadyOwned = 30,
        IPNotFound = 31,
        PersistFailed = 32,
        LockingFailed = 33,
        LogonSessionReplaced = 34,
        ConnectFailed = 35,
        HandshakeFailed = 36,
        IOFailure = 37,
        RemoteDisconnect = 38,
        ShoppingCartNotFound = 39,
        Blocked = 40,
        Ignored = 41,
        NoMatch = 42,
        AccountDisabled = 43,
        ServiceReadOnly = 44,
        AccountNotFeatured = 45,
        AdministratorOK = 46,
        ContentVersion = 47,
        TryAnotherCM = 48,
        PasswordRequiredToKickSession = 49,
        AlreadyLoggedInElsewhere = 50,
        Suspended = 51,
        Cancelled = 52,
        DataCorruption = 53,
        DiskFull = 54,
        RemoteCallFailed = 55,
        PasswordUnset = 56,
        ExternalAccountUnlinked = 57,
        PSNTicketInvalid = 58,
        ExternalAccountAlreadyLinked = 59,
        RemoteFileConflict = 60,
        IllegalPassword = 61,
        SameAsPreviousValue = 62,
        AccountLogonDenied = 63,
        CannotUseOldPassword = 64,
        InvalidLoginAuthCode = 65,
        AccountLogonDeniedNoMail = 66,
        HardwareNotCapableOfIPT = 67,
        IPTInitError = 68,
        ParentalControlRestricted = 69,
        FacebookQueryError = 70,
        ExpiredLoginAuthCode = 71,
        IPLoginRestrictionFailed = 72,
        AccountLockedDown = 73,
        AccountLogonDeniedVerifiedEmailRequired = 74,
        NoMatchingURL = 75,
        BadResponse = 76,
        RequirePasswordReEntry = 77,
        ValueOutOfRange = 78,
        UnexpectedError = 79,
        Disabled = 80,
        InvalidCEGSubmission = 81,
        RestrictedDevice = 82,
        RegionLocked = 83,
        RateLimitExceeded = 84,
        AccountLoginDeniedNeedTwoFactor = 85,
        ItemDeleted = 86,
        AccountLoginDeniedThrottle = 87,
        TwoFactorCodeMismatch = 88,
        TwoFactorActivationCodeMismatch = 89,
        AccountAssociatedToMultiplePartners = 90,
        NotModified = 91,
        NoMobileDevice = 92,
        TimeNotSynced = 93,
        SmsCodeFailed = 94,
        AccountLimitExceeded = 95,
        AccountActivityLimitExceeded = 96,
        PhoneActivityLimitExceeded = 97,
        RefundToWallet = 98,
        EmailSendFailure = 99,
        NotSettled = 100,
        NeedCaptcha = 101,
        GSLTDenied = 102,
        GSOwnerDenied = 103,
        InvalidItemType = 104,
        IPBanned = 105,
        GSLTExpired = 106,
        InsufficientFunds = 107,
        TooManyPending = 108,
        NoSiteLicensesFound = 109,
        WGNetworkSendExceeded = 110,
        AccountNotFriends = 111,
        LimitedUserAccount = 112,
        CantRemoveItem = 113,
        AccountDeleted = 114,
        ExistingUserCancelledLicense = 115,
        CommunityCooldown = 116,
        NoLauncherSpecified = 117,
        MustAgreeToSSA = 118,
        LauncherMigrated = 119,
        SteamRealmMismatch = 120,
        InvalidSignature = 121,
        ParseFailure = 122,
        NoVerifiedPhone = 123,
        InsufficientBattery = 124,
        ChargerRequired = 125,
        CachedCredentialInvalid = 126,
        PhoneNumberIsVOIP = 127,
        NotSupported = 128,
        FamilySizeLimitExceeded = 129,
        OfflineAppCacheInvalid = 130
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool SteamApiInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiRunCallbacks();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SteamApiGetHSteamPipe();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SteamApiSteamUser();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SteamApiSteamUtils();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SteamApiSteamUgc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool SteamApiUserLoggedOn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SteamApiUtilsGetAppId(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUgcStartItemUpdate(IntPtr self, uint appId, ulong publishedFileId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool SteamApiUgcSetItemTags(IntPtr self, ulong updateHandle, ref SteamParamStringArray tags, bool allowAdminTags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUgcSubmitItemUpdate(IntPtr self, ulong updateHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string changeNote);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiManualDispatchInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiManualDispatchRunFrame(int steamPipe);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool SteamApiManualDispatchGetApiCallResult(
        int steamPipe,
        ulong apiCall,
        IntPtr callback,
        int callbackSize,
        int expectedCallback,
        out bool failed);
}
