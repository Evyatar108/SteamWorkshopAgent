using System.Runtime.InteropServices;
using System.Text;

namespace SteamWorkshopAgent;

internal sealed partial class SteamworksNativeClient
{
    // Loaded only when media commands run, so existing publishing exports stay unchanged.
    internal async Task<MediaState> QueryMediaStateAsync(ulong item)
    {
        var self = steamApiSteamUgc();
        var query = Load<CreateDetails>("SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest")(self, [item], 1);
        if (query is 0 or ulong.MaxValue) throw new InvalidOperationException("Invalid UGC query handle.");
        try
        {
            if (!Load<QueryBool>("SteamAPI_ISteamUGC_SetReturnAdditionalPreviews")(self, query, true)
                || !Load<QueryAge>("SteamAPI_ISteamUGC_SetAllowCachedResponse")(self, query, 0))
                throw new InvalidOperationException("Cannot configure media query.");
            await SteamRequestGate.BeforeRequestAsync();
            var call = Load<QuerySend>("SteamAPI_ISteamUGC_SendQueryUGCRequest")(self, query);
            if (call == 0) throw new InvalidOperationException("Invalid UGC query call.");
            // SteamUGCQueryCompleted_t grew a next-cursor field in newer SDKs.
            // Obtain its actual byte size from SteamAPICallCompleted_t, not a guessed layout.
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                var completed = false;
                var size = 0;
                while (DateTime.UtcNow < deadline)
                {
                    RunCallbacks();
                    if (size == 0) size = QueryCallbackSize(call);
                    if (size > 0 && steamApiManualDispatchGetApiCallResult(steamApiGetHSteamPipe(), call, buffer,
                            size, 3401, out var failure))
                    {
                        if (failure || Marshal.ReadInt64(buffer) != unchecked((long)query)
                            || Marshal.ReadInt32(buffer, 8) != 1 || Marshal.ReadInt32(buffer, 12) != 1)
                            throw new InvalidOperationException("Steam media query failed or item not found.");
                        completed = true;
                        break;
                    }
                    await Task.Delay(25);
                }
                if (!completed) throw new TimeoutException("Steam media query timed out; not retried.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            var count = Load<QueryCount>("SteamAPI_ISteamUGC_GetQueryUGCNumAdditionalPreviews")(self, query, 0);
            if (count > 1000) throw new InvalidOperationException("Implausible preview count.");
            var results = new List<AdditionalPreview>();
            var get = Load<GetPreview>("SteamAPI_ISteamUGC_GetQueryUGCAdditionalPreview");
            for (uint i = 0; i < count; i++)
            {
                var url = new byte[8192];
                var name = new byte[4096];
                if (!get(self, query, 0, i, url, 8192, name, 4096, out var type))
                    throw new InvalidOperationException("Cannot read additional preview; refusing partial list.");
                results.Add(new(i, Utf8(name), Utf8(url), type));
            }
            var details = Marshal.AllocHGlobal(16384);
            try
            {
                if (!Load<GetDetails>("SteamAPI_ISteamUGC_GetQueryUGCResult")(self, query, 0, details))
                    throw new InvalidOperationException("Cannot read current item details.");
                return ReadMediaDetails(details, item, results, OperatingSystem.IsWindows());
            }
            finally { Marshal.FreeHGlobal(details); }
        }
        finally { Load<QueryRelease>("SteamAPI_ISteamUGC_ReleaseQueryUGCRequest")(self, query); }
    }

    internal static MediaState ReadMediaDetails(IntPtr details, ulong expectedItem,
        IReadOnlyList<AdditionalPreview> previews, bool windows)
    {
        // SteamUGCDetails_t's fixed prefix: id, result, type, creator/consumer
        // app IDs, title[129], description[8000], owner, three timestamps,
        // visibility. The SDK uses pack 8 on Windows and pack 4 elsewhere.
        var result = Marshal.ReadInt32(details, 8);
        if (result != 1)
            throw new SteamNativeFailureException($"Steam item query failed: {FormatResult(result)}.");
        var ownerOffset = windows ? 8160 : 8156;
        var item = unchecked((ulong)Marshal.ReadInt64(details));
        var creator = unchecked((ulong)Marshal.ReadInt64(details, ownerOffset));
        var visibility = Marshal.ReadInt32(details, ownerOffset + 20);
        if (item != expectedItem
            || Marshal.ReadInt32(details, 20) != AgentPaths.RimWorldAppId
            || creator == 0 || visibility is < 0 or > 3)
            throw new MediaRefusalException("Cannot verify current item, creator, app and visibility.");
        return new(item, creator, visibility, previews);
    }

    private static string Utf8(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0 || end == bytes.Length - 1)
            throw new InvalidOperationException("Possibly truncated media query string.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, end);
    }

    private int QueryCallbackSize(ulong call)
    {
        var next = Load<NextCallback>("SteamAPI_ManualDispatch_GetNextCallback");
        var free = Load<FreeCallback>("SteamAPI_ManualDispatch_FreeLastCallback");
        var pipe = steamApiGetHSteamPipe();
        while (next(pipe, out var callback))
        {
            try
            {
                if (callback.Id == 703 && callback.Size >= 16
                    && unchecked((ulong)Marshal.ReadInt64(callback.Data)) == call
                    && Marshal.ReadInt32(callback.Data, 8) == 3401)
                {
                    var size = Marshal.ReadInt32(callback.Data, 12);
                    if (size is < 21 or > 4096)
                        throw new InvalidOperationException("Invalid query callback size.");
                    return size;
                }
            }
            finally { free(pipe); }
        }
        return 0;
    }

    internal bool AddPreview(ulong handle, string path) =>
        Load<AddPreviewFile>("SteamAPI_ISteamUGC_AddItemPreviewFile")(steamApiSteamUgc(), handle, path, 0);
    internal bool ReplacePreview(ulong handle, uint index, string path) =>
        Load<ReplacePreviewFile>("SteamAPI_ISteamUGC_UpdateItemPreviewFile")(steamApiSteamUgc(), handle, index, path);
    internal bool RemovePreview(ulong handle, uint index) =>
        Load<RemovePreviewFile>("SteamAPI_ISteamUGC_RemoveItemPreview")(steamApiSteamUgc(), handle, index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CreateDetails(IntPtr self, [In] ulong[] ids, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool QueryBool(IntPtr self, ulong query, [MarshalAs(UnmanagedType.I1)] bool value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool QueryAge(IntPtr self, ulong query, uint seconds);
    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackMessage { public int User; public int Id; public IntPtr Data; public int Size; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NextCallback(int pipe, out CallbackMessage message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCallback(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong QuerySend(IntPtr self, ulong query);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool QueryRelease(IntPtr self, ulong query);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint QueryCount(IntPtr self, ulong query, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetDetails(IntPtr self, ulong query, uint index, IntPtr details);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetPreview(IntPtr self, ulong query, uint index, uint preview,
        [Out] byte[] url, uint urlSize, [Out] byte[] name, uint nameSize, out int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AddPreviewFile(IntPtr self, ulong handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ReplacePreviewFile(IntPtr self, ulong handle, uint index, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool RemovePreviewFile(IntPtr self, ulong handle, uint index);
}

internal sealed class NativeMediaClient(SteamworksNativeClient steam) : IWorkshopMediaClient
{
    public Task<MediaState> QueryAsync(ulong item) => steam.QueryMediaStateAsync(item);
    public ulong Start(ulong item) => steam.StartItemUpdate(AgentPaths.RimWorldAppId, item);
    public bool Add(ulong handle, string path) => steam.AddPreview(handle, path);
    public bool Replace(ulong handle, uint index, string path) => steam.ReplacePreview(handle, index, path);
    public bool Remove(ulong handle, uint index) => steam.RemovePreview(handle, index);
    public bool Visibility(ulong handle, int visibility) => steam.SetItemVisibility(handle, visibility);
    public async Task SubmitAsync(ulong handle)
    {
        await SteamRequestGate.BeforeRequestAsync();
        var call = steam.SubmitItemUpdate(handle, "Update Workshop page media or visibility");
        if (call == 0) throw new InvalidOperationException("Invalid submit handle; read back before retrying.");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            steam.RunCallbacks();
            if (steam.TryGetSubmitItemUpdateResult(call, out var result, out var failure))
            {
                if (failure || result.Result != 1 || result.UserNeedsToAcceptWorkshopLegalAgreement)
                    throw new InvalidOperationException("Steam update not proven successful; read back before retrying.");
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Steam update timed out; read back, do not resubmit.");
    }
}
