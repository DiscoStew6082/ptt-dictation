using System.Windows.Automation;

namespace PttDictation.App;

internal sealed record TextTargetReadiness(Func<bool> Probe, Func<IWindowsTextTarget?>? ReadTarget = null)
{
    internal IWindowsTextTarget? Target => ReadTarget?.Invoke();
}

// Preparation may refine an unavailable browser container, never a previously
// identified editor into a different editor. It grants no permission to paste.
internal static class BrowserCaptureReadiness
{
    internal static TextTargetReadiness Create(Func<long> readFocusGeneration)
        => CreatePrepared(() => TextProviderDiagnostics.Observe("preparation.focused_element", () => AutomationElement.FocusedElement), (original, focused) =>
            Related(original, focused),
            focused => TextProviderDiagnostics.Observe("preparation.original_element", () => focused.Current.HasKeyboardFocus)
                ? WindowsTextTarget.CaptureIdentity(focused, readFocusGeneration)()
                : null,
            Automation.Compare);

    internal static TextTargetReadiness CreatePrepared<T>(Func<T?> readFocused, Func<T, T, bool> related,
        Func<T, IWindowsTextTarget?> capture, Func<T, T, bool> same) where T : class
    {
        IWindowsTextTarget? prepared = null;
        var probe = Create(readFocused, related, focused =>
        {
            // Use the same capability policy, including guarded legacy focus,
            // as real capture. Retain the original provider and live generation
            // reader; handoff verifies its exact snapshot rather than bootstrapping
            // a second target after recording has already started.
            var target = capture(focused);
            if (target is null || !IsReady(target)) return false;
            prepared = target;
            return true;
        }, same);
        return new(probe, () => prepared);
    }

    private static bool Related(AutomationElement original, AutomationElement focused)
    {
        if (focused.Current.ProcessId != original.Current.ProcessId) return false;
        var type = original.Current.ControlType;
        var canRefine = type == ControlType.Pane || type == ControlType.Group
            || type == ControlType.Document || type == ControlType.Window;
        if (Automation.Compare(original, focused)) return true;
        if (!canRefine) return false;
        var ancestor = focused;
        for (var depth = 0; ancestor is not null && depth < 32; depth++)
        {
            if (Automation.Compare(original, ancestor)) return true;
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
        }
        return false;
    }

    internal static Func<bool> Create<T>(Func<T?> readFocused, Func<T, T, bool> related,
        Func<T, bool> ready, Func<T, T, bool>? same = null) where T : class
    {
        T? original = null;
        var pinned = false;
        return () =>
        {
            var focused = readFocused();
            if (focused is null) return false;
            // A cold provider may expose nothing on the first read. Pin its
            // first available identity; never adopt another after that point.
            original ??= focused;
            if (!(pinned ? (same ?? related)(original, focused) : related(original, focused)))
                throw new InvalidOperationException("The textbox changed while dictation was preparing.");
            if (pinned) return true;
            if (!ready(focused)) return false;
            // The capability probe itself may initialize a browser's precise
            // editor. Verify that identity on the next pass instead of pinning
            // the stale container observed before the provider call.
            var after = readFocused();
            if (after is null || !(same ?? related)(focused, after)) return false;
            original = focused;
            pinned = true;
            return true;
        };
    }

    internal static bool IsReady(IWindowsTextTarget target) => target.IsFocused
        && target.IsPreparedSelectionCurrent && (target.SupportsReplacement || target.CanPasteFallback);
}
