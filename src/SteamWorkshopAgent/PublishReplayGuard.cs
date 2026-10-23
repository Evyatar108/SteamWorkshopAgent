#nullable enable
using System;

namespace SteamWorkshopAgent;

// Linked into the in-game companion. Both submitters apply the same rule to a request's result file.
internal static class PublishReplayGuard
{
    // The backends set fallbackAllowed only for a definitive failure before SubmitItemUpdate.
    // The submit-intent marker clears it before the call, so a crash during submission also refuses.
    internal static bool AllowsAnotherAttempt(bool success, bool submissionStarted, bool fallbackAllowed) =>
        !success && !submissionStarted && fallbackAllowed;

    internal static void EnsureCanSubmit(bool allowed)
    {
        if (!allowed)
            throw new InvalidOperationException(
                "This request already records a possible or completed submission, or its result is unreadable. " +
                "Read back the Workshop item instead of submitting this request again.");
    }
}
