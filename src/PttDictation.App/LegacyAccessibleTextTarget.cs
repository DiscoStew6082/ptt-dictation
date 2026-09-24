namespace PttDictation.App;

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
        int processId, Func<bool> captureUnchanged, Func<bool> originalWindowCurrent)
    {
        try
        {
            if (processId == 0 || !captureUnchanged() || !originalWindowCurrent()) return null;
            var focused = ResolveFocus(root(), processId);
            if (focused is null || !IsWritableText(focused.Value, processId)
                || !captureUnchanged() || !originalWindowCurrent()) return null;
            var target = new LegacyAccessibleTextTarget(root, focused.Value, processId, originalWindowCurrent);
            return target.IsFocused && captureUnchanged() ? target : null;
        }
        catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error)) { return null; }
    }

    public bool IsFocused
    {
        get
        {
            try
            {
                if (!_originalWindowCurrent()) return false;
                var current = ResolveFocus(_root(), _processId);
                return current is { } focused && focused.ChildId == _original.ChildId
                    && focused.Node.HasSameIdentity(_original.Node)
                    && IsWritableText(focused, _processId) && _originalWindowCurrent();
            }
            catch (Exception error) when (WindowsTextTarget.IsProviderFailure(error)) { return false; }
        }
    }
    public bool SupportsReplacement => false;
    public bool CanPasteFallback => true;
    public bool IsPreparedSelectionCurrent => IsFocused;
    public bool HasAttemptedWrite => false;
    public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
        => TextTargetUpdateResult.Unsupported;

    private static bool IsWritableText(Reference reference, int processId)
    {
        // ROLE_SYSTEM_TEXT with actual keyboard focus. Read-only text, protected
        // fields, unavailable controls and hidden objects must never receive paste.
        const int forbidden = 0x1 | 0x40 | 0x8000 | 0x20000000;
        return reference.Node.ProcessId == processId && reference.Node.Role(reference.ChildId) == 42
            && reference.Node.State(reference.ChildId) is { } state
            && (state & 4) != 0 && (state & forbidden) == 0;
    }

    private static Reference? ResolveFocus(ILegacyAccessibleNode? node, int processId)
    {
        var visited = new List<ILegacyAccessibleNode>();
        for (var depth = 0; node is not null && depth < 16; depth++)
        {
            if (node.ProcessId != processId || visited.Any(node.HasSameIdentity)) return null;
            visited.Add(node);
            // Follow only the provider's explicit keyboard-focus chain. Never
            // enumerate children or choose an editor by name, value or location.
            switch (node.Focus)
            {
                case int childId when childId == 0:
                    return new(node, 0);
                case int childId:
                    var childObject = node.GetChild(childId);
                    if (childObject is null) return new(node, childId);
                    node = childObject;
                    break;
                case ILegacyAccessibleNode child:
                    node = child;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private readonly record struct Reference(ILegacyAccessibleNode Node, int ChildId);
}
