#nullable enable
using System;

namespace SteamWorkshopAgent;

// Shared by the detached publisher and the in-game companion.
internal static class PublishReplayGuard
{
    internal static void EnsureCanSubmit(
        string? stage, bool success, bool submissionStarted, bool outcomeDefinitive)
    {
        if (!IsProvenPreSubmitFailure(stage, success, submissionStarted, outcomeDefinitive))
            throw new InvalidOperationException(
                "This request already records a possible or completed submission (or an unreadable result). " +
                "Read back the Workshop item instead of submitting this request again.");
    }

    // Only these backend failures prove SubmitItemUpdate did not start an upload.
    // Unknown stages and inconsistent flags fail closed, even if fallback was requested.
    internal static bool IsProvenPreSubmitFailure(
        string? stage, bool success, bool submissionStarted, bool outcomeDefinitive)
    {
        if (stage is null || success || submissionStarted || !outcomeDefinitive)
            return false;
        switch (stage.ToLowerInvariant())
        {
            case "preflight":
            case "process-isolation":
            case "steam-init":
            case "session":
            case "ownership":
            case "app-id":
            case "start-update":
            case "set-title":
            case "set-description":
            case "set-preview":
            case "set-content":
            case "set-visibility":
            case "setup":
            case "submit": // Invalid API call handle, explicitly reported as not submitted.
                return true;
            default:
                return false;
        }
    }
}
