namespace SteamWorkshopAgent;

// Cooperative handshake with steam_common.py run. The wrapper owns the lock,
// waits and durably records each outbound request before granting permission.
internal class SteamNativeFailureException(string message) : InvalidOperationException(message);
internal sealed class SteamProtocolException(string message) : SteamNativeFailureException(message);

internal static class SteamRequestGate
{
    internal const string Request = "SWA_STEAM_REQUEST_V2";
    internal const string Granted = "SWA_STEAM_GRANTED_V2";
    internal const string Consumed = "SWA_STEAM_CONSUMED_V2";
    internal const int RefusedExitCode = 4;
    internal static int Requests { get; private set; }

    internal static async Task BeforeRequestAsync()
    {
        if (Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_THROTTLE_PROTOCOL") != "2")
            throw new MediaRefusalException("Media helpers require the cooperative steam_common.py run wrapper.");
        await ExchangeAsync(Guid.NewGuid().ToString("N"), Console.Error, Console.In);
        Requests++;
    }

    internal static async Task ExchangeAsync(string identifier, TextWriter output, TextReader input)
    {
        await output.WriteLineAsync($"{Request} {identifier}");
        await output.FlushAsync();
        if (await input.ReadLineAsync() != $"{Granted} {identifier}")
            throw new SteamProtocolException("Stale, unsolicited or missing Steam throttle grant.");
        // The wrapper will not grant another request until this one was consumed.
        await output.WriteLineAsync($"{Consumed} {identifier}");
        await output.FlushAsync();
    }

    internal static bool IsLocalRefusal(Exception exception, int requests) =>
        exception is not SteamNativeFailureException
        && (exception is MediaRefusalException || requests == 0);
}
