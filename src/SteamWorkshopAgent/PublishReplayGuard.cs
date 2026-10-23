#nullable enable
using System;

namespace SteamWorkshopAgent;

// Shared by the detached publisher and the in-game companion.
internal static class PublishReplayGuard
{
    internal static void EnsureCanSubmit(
        string? stage, bool submissionStarted, bool outcomeDefinitive, bool fallbackAllowed)
    {
        if (string.IsNullOrWhiteSpace(stage) || submissionStarted
            || stage.StartsWith("submit-intent", StringComparison.OrdinalIgnoreCase)
            || stage.StartsWith("submitted", StringComparison.OrdinalIgnoreCase)
            || stage.StartsWith("ambiguous", StringComparison.OrdinalIgnoreCase)
            || stage.StartsWith("completed", StringComparison.OrdinalIgnoreCase)
            || (!outcomeDefinitive && !fallbackAllowed))
            throw new InvalidOperationException(
                "This request already records a possible or completed submission (or an unreadable result). " +
                "Read back the Workshop item instead of submitting this request again.");
    }
}
