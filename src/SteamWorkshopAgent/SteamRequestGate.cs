namespace SteamWorkshopAgent;

// Cooperative handshake with steam_common.py run. The wrapper owns the lock,
// waits and durably records each outbound request before granting permission.
internal static class SteamRequestGate
{
    internal const string Request = "SWA_STEAM_REQUEST_V1";
    internal const string Granted = "SWA_STEAM_GRANTED_V1";
    internal const int RefusedExitCode = 4;
    internal static int Requests { get; private set; }

    internal static async Task BeforeRequestAsync()
    {
        if (Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_THROTTLE_PROTOCOL") != "1")
            throw new MediaRefusalException("Media helpers require the cooperative steam_common.py run wrapper.");
        await Console.Error.WriteLineAsync(Request);
        await Console.Error.FlushAsync();
        if (await Console.In.ReadLineAsync() != Granted)
            throw new MediaRefusalException("Steam throttle did not grant this outbound request.");
        Requests++;
    }
}
