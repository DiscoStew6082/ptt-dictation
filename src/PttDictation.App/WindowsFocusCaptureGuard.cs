using System.Runtime.InteropServices;
using PttDictation.Core;

namespace PttDictation.App;

// Construct and dispose on the UI thread: out-of-context WinEvents are delivered
// by that thread's message loop. This guard supplements synchronous UIA identity
// capture; native HWND identity alone cannot distinguish Chromium text fields.
internal sealed class WindowsFocusCaptureGuard : IDisposable
{
    internal const uint ForegroundEvent = 0x0003;
    internal const uint FocusEvent = 0x8005;
    internal const uint LocationEvent = 0x800B;
    internal const uint TextSelectionEvent = 0x8014;
    internal const uint Ia2CaretEvent = 0x011B;
    internal const uint Ia2SelectionEvent = 0x0121;
    internal const int CaretObject = -8;
    private readonly IFocusCaptureEvents _events;
    private long _generation;
    private volatile bool _disposed;

    public WindowsFocusCaptureGuard() : this(new NativeFocusCaptureEvents()) { }

    internal WindowsFocusCaptureGuard(IFocusCaptureEvents events)
    {
        _events = events;
        _events.Changed += OnChanged;
    }

    public Func<bool> BeginCapture()
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        var generation = Interlocked.Read(ref _generation);
        var focus = _events.ReadFocus();
        var valid = !_disposed && _events.Available && focus.Foreground != IntPtr.Zero
            && focus.Focused != IntPtr.Zero && generation == Interlocked.Read(ref _generation);
        DiagnosticTrace.Write("focus_guard.begin", new { valid, generation, hasForeground = focus.Foreground != IntPtr.Zero, hasFocusedControl = focus.Focused != IntPtr.Zero }, recordingId: recordingId);
        return () =>
        {
            var accepted = valid && !_disposed && generation == Interlocked.Read(ref _generation)
                && focus == _events.ReadFocus() && generation == Interlocked.Read(ref _generation);
            DiagnosticTrace.Write("focus_guard.confirm", new { accepted, valid, disposed = _disposed, initialGeneration = generation, currentGeneration = Interlocked.Read(ref _generation) }, recordingId: recordingId);
            return accepted;
        };
    }

    private void OnChanged(uint eventType, IntPtr window, int objectId)
    {
        if (_disposed) return;
        if (eventType == ForegroundEvent)
        {
            Interlocked.Increment(ref _generation);
            return;
        }
        var selection = eventType is TextSelectionEvent or Ia2CaretEvent or Ia2SelectionEvent
            || eventType == LocationEvent && objectId == CaretObject;
        if (eventType != FocusEvent && !selection) return;
        if (_events.IsInFocusedHierarchy(window)) Interlocked.Increment(ref _generation);
    }

    public void Dispose()
    {
        _disposed = true;
        _events.Changed -= OnChanged;
        _events.Dispose();
    }

    private sealed class NativeFocusCaptureEvents : IFocusCaptureEvents
    {
        private readonly WinEventCallback _callback;
        private readonly List<IntPtr> _hooks = [];
        private GCHandle _callbackRoot;
        public event Action<uint, IntPtr, int>? Changed;
        public bool Available { get; }

        public NativeFocusCaptureEvents()
        {
            _callback = (_, eventType, window, objectId, _, _, _) => Changed?.Invoke(eventType, window, objectId);
            _callbackRoot = GCHandle.Alloc(_callback);
            foreach (var eventType in new[] { ForegroundEvent, FocusEvent, LocationEvent, TextSelectionEvent, Ia2CaretEvent, Ia2SelectionEvent })
            {
                var hook = SetWinEventHook(eventType, eventType, IntPtr.Zero, _callback, 0, 0, 0);
                if (hook != IntPtr.Zero) _hooks.Add(hook);
            }
            Available = _hooks.Count == 6;
        }

        public FocusCapturePosition ReadFocus()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return default;
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            var thread = GetWindowThreadProcessId(foreground, out _);
            return GetGUIThreadInfo(thread, ref info)
                ? new FocusCapturePosition(foreground, info.Focused) : default;
        }

        public bool IsInFocusedHierarchy(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            var focus = ReadFocus();
            if (focus.Focused == IntPtr.Zero || GetAncestor(window, 2) != GetAncestor(focus.Foreground, 2)) return false;
            return window == focus.Focused || IsChild(window, focus.Focused) || IsChild(focus.Focused, window);
        }

        public void Dispose()
        {
            foreach (var hook in _hooks) UnhookWinEvent(hook);
            _hooks.Clear();
            if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        }

        private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId,
            int childId, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focused, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module,
            WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr child);
    }
}

internal readonly record struct FocusCapturePosition(IntPtr Foreground, IntPtr Focused);

internal interface IFocusCaptureEvents : IDisposable
{
    bool Available { get; }
    event Action<uint, IntPtr, int>? Changed;
    FocusCapturePosition ReadFocus();
    bool IsInFocusedHierarchy(IntPtr window);
}
