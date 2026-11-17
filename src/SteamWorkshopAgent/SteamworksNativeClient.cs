using System.Runtime.InteropServices;

namespace SteamWorkshopAgent;

internal sealed class SteamworksNativeClient : IDisposable
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
    private readonly SteamApiUserGetSteamId steamApiUserGetSteamId;
    private readonly SteamApiUtilsGetAppId steamApiUtilsGetAppId;
    private readonly SteamApiUgcStartItemUpdate steamApiUgcStartItemUpdate;
    private readonly SteamApiUgcCreateItem steamApiUgcCreateItem;
    private readonly SteamApiUgcSetItemString steamApiUgcSetItemTitle;
    private readonly SteamApiUgcSetItemString steamApiUgcSetItemDescription;
    private readonly SteamApiUgcSetItemString steamApiUgcSetItemPreview;
    private readonly SteamApiUgcSetItemString steamApiUgcSetItemContent;
    private readonly SteamApiUgcSetItemVisibility steamApiUgcSetItemVisibility;
    private readonly SteamApiUgcSubmitItemUpdate steamApiUgcSubmitItemUpdate;
    private readonly SteamApiUgcGetItemUpdateProgress steamApiUgcGetItemUpdateProgress;
    private readonly SteamApiManualDispatchInit steamApiManualDispatchInit;
    private readonly SteamApiManualDispatchRunFrame steamApiManualDispatchRunFrame;
    private readonly SteamApiManualDispatchGetApiCallResult steamApiManualDispatchGetApiCallResult;

    public SteamworksNativeClient(string nativeLibraryPath)
    {
        library = NativeLibrary.Load(nativeLibraryPath);
        steamApiInit = Load<SteamApiInit>("SteamAPI_Init", "SteamAPI_InitSafe");
        steamApiShutdown = Load<SteamApiShutdown>("SteamAPI_Shutdown");
        steamApiRunCallbacks = Load<SteamApiRunCallbacks>("SteamAPI_RunCallbacks");
        steamApiGetHSteamPipe = Load<SteamApiGetHSteamPipe>("SteamAPI_GetHSteamPipe");
        steamApiSteamUser = Load<SteamApiSteamUser>("SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v023");
        steamApiSteamUtils = Load<SteamApiSteamUtils>("SteamAPI_SteamUtils_v010", "SteamAPI_SteamUtils_v011");
        steamApiSteamUgc = Load<SteamApiSteamUgc>("SteamAPI_SteamUGC_v016", "SteamAPI_SteamUGC_v017", "SteamAPI_SteamUGC_v018", "SteamAPI_SteamUGC_v020", "SteamAPI_SteamUGC_v021");
        steamApiUserLoggedOn = Load<SteamApiUserLoggedOn>("SteamAPI_ISteamUser_BLoggedOn");
        steamApiUserGetSteamId = Load<SteamApiUserGetSteamId>("SteamAPI_ISteamUser_GetSteamID");
        steamApiUtilsGetAppId = Load<SteamApiUtilsGetAppId>("SteamAPI_ISteamUtils_GetAppID");
        steamApiUgcStartItemUpdate = Load<SteamApiUgcStartItemUpdate>("SteamAPI_ISteamUGC_StartItemUpdate");
        steamApiUgcCreateItem = Load<SteamApiUgcCreateItem>("SteamAPI_ISteamUGC_CreateItem");
        steamApiUgcSetItemTitle = Load<SteamApiUgcSetItemString>("SteamAPI_ISteamUGC_SetItemTitle");
        steamApiUgcSetItemDescription = Load<SteamApiUgcSetItemString>("SteamAPI_ISteamUGC_SetItemDescription");
        steamApiUgcSetItemPreview = Load<SteamApiUgcSetItemString>("SteamAPI_ISteamUGC_SetItemPreview");
        steamApiUgcSetItemContent = Load<SteamApiUgcSetItemString>("SteamAPI_ISteamUGC_SetItemContent");
        steamApiUgcSetItemVisibility = Load<SteamApiUgcSetItemVisibility>("SteamAPI_ISteamUGC_SetItemVisibility");
        steamApiUgcSubmitItemUpdate = Load<SteamApiUgcSubmitItemUpdate>("SteamAPI_ISteamUGC_SubmitItemUpdate");
        steamApiUgcGetItemUpdateProgress = Load<SteamApiUgcGetItemUpdateProgress>("SteamAPI_ISteamUGC_GetItemUpdateProgress");
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

    public void RunCallbacks()
    {
        steamApiRunCallbacks();
        steamApiManualDispatchRunFrame(steamApiGetHSteamPipe());
    }

    public bool UserLoggedOn() => steamApiUserLoggedOn(steamApiSteamUser());

    public ulong GetSteamId() => steamApiUserGetSteamId(steamApiSteamUser());

    public uint GetAppId() => steamApiUtilsGetAppId(steamApiSteamUtils());

    public ulong StartItemUpdate(uint appId, ulong publishedFileId)
    {
        return steamApiUgcStartItemUpdate(steamApiSteamUgc(), appId, publishedFileId);
    }

    public ulong CreateItem(uint appId) => steamApiUgcCreateItem(steamApiSteamUgc(), appId, 0);

    public bool SetItemTitle(ulong updateHandle, string value) => steamApiUgcSetItemTitle(steamApiSteamUgc(), updateHandle, value);

    public bool SetItemDescription(ulong updateHandle, string value) => steamApiUgcSetItemDescription(steamApiSteamUgc(), updateHandle, value);

    public bool SetItemPreview(ulong updateHandle, string value) => steamApiUgcSetItemPreview(steamApiSteamUgc(), updateHandle, value);

    public bool SetItemContent(ulong updateHandle, string value) => steamApiUgcSetItemContent(steamApiSteamUgc(), updateHandle, value);

    public bool SetItemVisibility(ulong updateHandle, int visibility) => steamApiUgcSetItemVisibility(steamApiSteamUgc(), updateHandle, visibility);

    public ulong SubmitItemUpdate(ulong updateHandle, string changeNote)
    {
        return steamApiUgcSubmitItemUpdate(steamApiSteamUgc(), updateHandle, changeNote);
    }

    public SteamItemUpdateProgress GetItemUpdateProgress(ulong updateHandle)
    {
        var status = steamApiUgcGetItemUpdateProgress(
            steamApiSteamUgc(),
            updateHandle,
            out var processed,
            out var total);
        return new SteamItemUpdateProgress(((SteamItemUpdateStatus)status).ToString(), processed, total);
    }

    public bool TryGetSubmitItemUpdateResult(ulong apiCall, out SteamSubmitItemUpdateResult result, out bool ioFailure)
    {
        var size = Marshal.SizeOf<SteamSubmitItemUpdateResult>();
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
                ? Marshal.PtrToStructure<SteamSubmitItemUpdateResult>(buffer)
                : default;
            return available;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool TryGetCreateItemResult(ulong apiCall, out SteamCreateItemResult result, out bool ioFailure)
    {
        return TryGetApiCallResult(apiCall, SteamCreateItemResult.CallbackId, out result, out ioFailure);
    }

    public void Dispose()
    {
        NativeLibrary.Free(library);
    }

    public static string FormatResult(int result)
    {
        return Enum.IsDefined(typeof(SteamResult), result)
            ? ((SteamResult)result).ToString()
            : result.ToString();
    }

    // Steamworks renames the init function and the interface accessors between SDK versions. The first name the library exports wins.
    private T Load<T>(params string[] names) where T : Delegate
    {
        foreach (var name in names)
            if (NativeLibrary.TryGetExport(library, name, out var export))
                return Marshal.GetDelegateForFunctionPointer<T>(export);
        throw new EntryPointNotFoundException($"The Steamworks library exports none of: {string.Join(", ", names)}.");
    }

    private bool TryGetApiCallResult<T>(ulong apiCall, int callbackId, out T result, out bool ioFailure) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var available = steamApiManualDispatchGetApiCallResult(
                steamApiGetHSteamPipe(), apiCall, buffer, size, callbackId, out ioFailure);
            result = available ? Marshal.PtrToStructure<T>(buffer) : default;
            return available;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
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
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiUserLoggedOn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUserGetSteamId(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SteamApiUtilsGetAppId(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUgcStartItemUpdate(IntPtr self, uint appId, ulong publishedFileId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUgcCreateItem(IntPtr self, uint appId, int fileType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiUgcSetItemString(
        IntPtr self,
        ulong updateHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiUgcSetItemVisibility(IntPtr self, ulong updateHandle, int visibility);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong SteamApiUgcSubmitItemUpdate(
        IntPtr self,
        ulong updateHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string changeNote);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SteamApiUgcGetItemUpdateProgress(
        IntPtr self,
        ulong updateHandle,
        out ulong bytesProcessed,
        out ulong bytesTotal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiManualDispatchInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SteamApiManualDispatchRunFrame(int pipe);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SteamApiManualDispatchGetApiCallResult(
        int pipe,
        ulong apiCall,
        IntPtr callback,
        int callbackSize,
        int expectedCallback,
        [MarshalAs(UnmanagedType.I1)] out bool ioFailure);

    private enum SteamItemUpdateStatus
    {
        Invalid = 0,
        PreparingConfig = 1,
        PreparingContent = 2,
        UploadingContent = 3,
        UploadingPreviewFile = 4,
        CommittingChanges = 5
    }

    private enum SteamResult
    {
        OK = 1,
        Fail = 2,
        NoConnection = 3,
        InvalidPassword = 5,
        LoggedInElsewhere = 6,
        InvalidParam = 8,
        FileNotFound = 9,
        Busy = 10,
        InvalidState = 11,
        AccessDenied = 15,
        Timeout = 16,
        Banned = 17,
        ServiceUnavailable = 20,
        NotLoggedOn = 21,
        Pending = 22,
        InsufficientPrivilege = 24,
        LimitExceeded = 25,
        Revoked = 26,
        Expired = 27,
        DuplicateRequest = 29,
        AlreadyOwned = 30,
        ServiceReadOnly = 44,
        AccountNotFeatured = 45,
        TryAnotherCM = 48,
        AccountLoginDeniedThrottle = 87
    }
}

internal readonly record struct SteamItemUpdateProgress(string Status, ulong BytesProcessed, ulong BytesTotal);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SteamSubmitItemUpdateResult
{
    public const int CallbackId = 3404;

    public int Result;

    [MarshalAs(UnmanagedType.I1)]
    public bool UserNeedsToAcceptWorkshopLegalAgreement;

    public ulong PublishedFileId;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SteamCreateItemResult
{
    public const int CallbackId = 3403;

    public int Result;
    public ulong PublishedFileId;

    [MarshalAs(UnmanagedType.I1)]
    public bool UserNeedsToAcceptWorkshopLegalAgreement;
}
