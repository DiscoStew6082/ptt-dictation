using System.Runtime.InteropServices;
using Accessibility;

namespace PttDictation.App;

// MSAA's accFocus returns either CHILDID_SELF, a child ID, or a child IAccessible.
// Keep that identity rather than translating it back through a possibly stale UIA proxy.
internal sealed class NativeLegacyAccessibleNode(IAccessible accessible) : ILegacyAccessibleNode
{
    internal static ILegacyAccessibleNode? FromWindow(IntPtr window)
    {
        var iid = typeof(IAccessible).GUID;
        return AccessibleObjectFromWindow(window, unchecked((uint)-4), ref iid, out var accessible) >= 0
            && accessible is not null ? new NativeLegacyAccessibleNode(accessible) : null;
    }

    public object? Focus => accessible.accFocus switch
    {
        IAccessible child => new NativeLegacyAccessibleNode(child),
        int childId => childId,
        _ => null
    };

    public ILegacyAccessibleNode? GetChild(int childId)
        => accessible.get_accChild(childId) is IAccessible child ? new NativeLegacyAccessibleNode(child) : null;

    public int? Role(int childId) => accessible.get_accRole(childId) is int role ? role : null;
    public int? State(int childId) => accessible.get_accState(childId) is int state ? state : null;

    public int ProcessId
    {
        get
        {
            if (WindowFromAccessibleObject(accessible, out var window) < 0 || window == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(window, out var processId);
            return processId;
        }
    }

    public bool HasSameIdentity(ILegacyAccessibleNode other)
    {
        if (other is not NativeLegacyAccessibleNode candidate) return false;
        var originalIdentity = IntPtr.Zero;
        var candidateIdentity = IntPtr.Zero;
        try
        {
            originalIdentity = Marshal.GetIUnknownForObject(accessible);
            candidateIdentity = Marshal.GetIUnknownForObject(candidate.Accessible);
            return originalIdentity == candidateIdentity;
        }
        finally
        {
            if (candidateIdentity != IntPtr.Zero) Marshal.Release(candidateIdentity);
            if (originalIdentity != IntPtr.Zero) Marshal.Release(originalIdentity);
        }
    }

    private IAccessible Accessible => accessible;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")]
    private static extern int WindowFromAccessibleObject(IAccessible accessible, out IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
