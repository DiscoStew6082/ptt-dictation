using System.Windows.Automation;

namespace PttDictation.App;

// Preparation may refine an unavailable browser container, never a previously
// identified editor into a different editor. It grants no permission to paste.
internal static class BrowserCaptureReadiness
{
    internal static Func<bool> Create()
    {
        return Create(() => AutomationElement.FocusedElement, (original, focused) =>
        {
            if (focused.Current.ProcessId != original.Current.ProcessId) return false;
            var type = original.Current.ControlType;
            var canRefine = type == ControlType.Pane || type == ControlType.Group
                || type == ControlType.Document || type == ControlType.Window;
            var related = Automation.Compare(original, focused);
            if (!related && canRefine)
            {
                var ancestor = focused;
                for (var depth = 0; ancestor is not null && depth < 32; depth++)
                {
                    if (Automation.Compare(original, ancestor)) { related = true; break; }
                    ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
                }
            }
            return related;
        }, focused =>
        {
            if (!focused.Current.HasKeyboardFocus) return false;
            // Use the same capability policy, including guarded legacy focus,
            // as real capture. This is a disposable readiness observation only;
            // the intent guard surrounds it and capture takes a fresh snapshot.
            var target = WindowsTextTarget.CaptureFocusedIdentity(() => 0)();
            return IsReady(target);
        }, Automation.Compare);
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
