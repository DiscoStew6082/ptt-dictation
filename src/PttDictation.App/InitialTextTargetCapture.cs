namespace PttDictation.App;

internal readonly record struct InitialTextTargetCandidate(IWindowsTextTarget Target, bool IsUnsupportedPane);

// This policy is used only before a recording has accepted its first text target.
internal static class InitialTextTargetCapture
{
    internal static IWindowsTextTarget Capture<TIdentity>(TIdentity? original,
        Func<TIdentity?, InitialTextTargetCandidate> inspect,
        Func<TIdentity?> readFocusedIdentity, Func<TIdentity, TIdentity, bool> isOriginalOrDescendant,
        Func<bool> unchanged, Action<TimeSpan> wait, Action<string, int>? trace = null)
        where TIdentity : class
    {
        var initial = inspect(original);
        if (original is null || !initial.IsUnsupportedPane || initial.Target.SupportsReplacement
            || initial.Target.CanPasteFallback) return initial.Target;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (!unchanged()) return Reject("context_changed", attempt);
            wait(TimeSpan.FromMilliseconds(50));
            if (!unchanged()) return Reject("context_changed", attempt);
            var focused = readFocusedIdentity();
            if (!unchanged()) return Reject("context_changed", attempt);
            if (focused is null || !isOriginalOrDescendant(original, focused))
                return Reject("focused_identity_unrelated", attempt);
            var candidate = inspect(focused);
            if (!unchanged()) return Reject("context_changed", attempt);
            // No final-paste fallback is earned by recovery: the new identity
            // must expose the full, consistent selection needed for replacement.
            if (candidate.Target.SupportsReplacement && candidate.Target.IsFocused
                && candidate.Target.IsPreparedSelectionCurrent && unchanged())
            {
                trace?.Invoke("recovered", attempt);
                return candidate.Target;
            }
            trace?.Invoke("not_ready", attempt);
        }
        return Reject("attempts_exhausted", 3);

        IWindowsTextTarget Reject(string reason, int attempt)
        {
            trace?.Invoke(reason, attempt);
            return initial.Target;
        }
    }
}
