using PttDictation.Core;

namespace PttDictation.App;

internal sealed record LegacyFocusDiagnostic(string Phase, string Reason, int? Depth = null,
    int? ProcessId = null, int? Role = null, int? State = null, string? ExceptionType = null);

internal interface ILegacyAccessibleNode
{
    object? Focus { get; }
    ILegacyAccessibleNode? GetChild(int childId);
    int? Role(int childId);
    int? State(int childId);
    int ProcessId { get; }
    bool HasSameIdentity(ILegacyAccessibleNode other);
}

// An MSAA target only permits one final paste. It never attempts range replacement.
internal sealed class LegacyAccessibleTextTarget : IWindowsTextTarget
{
    private readonly Func<ILegacyAccessibleNode?> _root;
    private readonly Reference _original;
    private readonly int _processId;
    private readonly Func<bool> _originalWindowCurrent;

    private LegacyAccessibleTextTarget(Func<ILegacyAccessibleNode?> root, Reference original,
        int processId, Func<bool> originalWindowCurrent)
        => (_root, _original, _processId, _originalWindowCurrent) = (root, original, processId, originalWindowCurrent);

    internal static IWindowsTextTarget? TryCapture(Func<ILegacyAccessibleNode?> root,
        int processId, Func<bool> captureUnchanged, Func<bool> originalWindowCurrent,
        Action<LegacyFocusDiagnostic>? trace = null)
    {
        void Report(LegacyFocusDiagnostic diagnostic)
        {
            DiagnosticTrace.Write("target.legacy_capture_detail", diagnostic);
            trace?.Invoke(diagnostic);
        }
        IWindowsTextTarget? Reject(string reason, string phase)
        {
            Report(new(phase, reason));
            return null;
        }
        try
        {
            if (processId == 0) return Reject("missing_process", "begin");
            if (!captureUnchanged()) return Reject("capture_changed", "begin");
            if (!originalWindowCurrent()) return Reject("window_changed", "begin");
            var focused = ResolveFocus(root(), processId, Report);
            if (focused is null || !IsWritableText(focused.Value, processId, Report)) return null;
            if (!captureUnchanged()) return Reject("capture_changed", "resolved");
            if (!originalWindowCurrent()) return Reject("window_changed", "resolved");
            var target = new LegacyAccessibleTextTarget(root, focused.Value, processId, originalWindowCurrent);
            if (!target.IsFocusedCore(Report)) return null;
            if (!captureUnchanged()) return Reject("capture_changed", "confirmed");
            Report(new("confirmed", "accepted"));
            return target;
        }
        catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error))
        {
            Report(new("capture", "provider_exception", ExceptionType: error.GetType().FullName));
            return null;
        }
    }

    public bool IsFocused
    {
        get
        {
            try
            {
                return IsFocusedCore();
            }
            catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error)) { return false; }
        }
    }

    private bool IsFocusedCore(Action<LegacyFocusDiagnostic>? trace = null)
    {
        if (!_originalWindowCurrent())
        {
            trace?.Invoke(new("confirm", "window_changed"));
            return false;
        }
        var current = ResolveFocus(_root(), _processId, trace);
        if (current is not { } focused) return false;
        if (focused.ChildId != _original.ChildId || !focused.Node.HasSameIdentity(_original.Node))
        {
            trace?.Invoke(new("confirm", "identity_changed"));
            return false;
        }
        if (!IsWritableText(focused, _processId, trace)) return false;
        if (_originalWindowCurrent()) return true;
        trace?.Invoke(new("confirm", "window_changed"));
        return false;
    }
    public bool SupportsReplacement => false;
    public bool CanPasteFallback => true;
    public bool IsPreparedSelectionCurrent => IsFocused;
    public bool HasAttemptedWrite => false;
    public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
        => TextTargetUpdateResult.Unsupported;

    private static bool IsWritableText(Reference reference, int processId,
        Action<LegacyFocusDiagnostic>? trace = null)
    {
        // ROLE_SYSTEM_TEXT with actual keyboard focus. Read-only text, protected
        // fields, unavailable controls and hidden objects must never receive paste.
        const int forbidden = 0x1 | 0x40 | 0x8000 | 0x20000000;
        var actualProcess = reference.Node.ProcessId;
        if (actualProcess != processId)
        {
            trace?.Invoke(new("writable", "process_mismatch", ProcessId: actualProcess));
            return false;
        }
        var role = reference.Node.Role(reference.ChildId);
        if (role != 42)
        {
            trace?.Invoke(new("writable", "role_not_text", ProcessId: actualProcess, Role: role));
            return false;
        }
        var state = reference.Node.State(reference.ChildId);
        var reason = state is null ? "state_unavailable"
            : (state.Value & 4) == 0 ? "not_focused"
            : (state.Value & forbidden) != 0 ? "forbidden_state" : null;
        if (reason is null) return true;
        trace?.Invoke(new("writable", reason, ProcessId: actualProcess, Role: role, State: state));
        return false;
    }

    private static Reference? ResolveFocus(ILegacyAccessibleNode? node, int processId,
        Action<LegacyFocusDiagnostic>? trace = null)
    {
        if (node is null)
        {
            trace?.Invoke(new("resolve", "root_unavailable"));
            return null;
        }
        var visited = new List<ILegacyAccessibleNode>();
        for (var depth = 0; node is not null && depth < 16; depth++)
        {
            var actualProcess = node.ProcessId;
            if (actualProcess != processId)
            {
                trace?.Invoke(new("resolve", "process_mismatch", depth, actualProcess));
                return null;
            }
            if (visited.Any(node.HasSameIdentity))
            {
                trace?.Invoke(new("resolve", "focus_cycle", depth, actualProcess));
                return null;
            }
            visited.Add(node);
            // Follow only the provider's explicit keyboard-focus chain. Never
            // enumerate children or choose an editor by name, value or location.
            switch (node.Focus)
            {
                case int childId when childId == 0:
                    trace?.Invoke(new("resolve", "self", depth, actualProcess));
                    return new(node, 0);
                case int childId:
                    var childObject = node.GetChild(childId);
                    if (childObject is null)
                    {
                        trace?.Invoke(new("resolve", "simple_child", depth, actualProcess));
                        return new(node, childId);
                    }
                    node = childObject;
                    break;
                case ILegacyAccessibleNode child:
                    node = child;
                    break;
                default:
                    trace?.Invoke(new("resolve", "focus_unavailable", depth, actualProcess));
                    return null;
            }
        }
        trace?.Invoke(new("resolve", "depth_exhausted", visited.Count));
        return null;
    }

    private readonly record struct Reference(ILegacyAccessibleNode Node, int ChildId);
}
