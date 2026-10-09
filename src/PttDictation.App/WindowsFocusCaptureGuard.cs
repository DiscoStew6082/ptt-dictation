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
    private readonly Func<long> _tickCount;
    private readonly Func<IntPtr> _statusWindow;
    private long _generation;
    private long _foregroundGeneration;
    private long _preparationStartedAt = -1;
    private long _captureStartedAt = -1;
    private string? _captureRecordingId;
    private bool _confirmationPending;
    private volatile bool _disposed;

    public WindowsFocusCaptureGuard(Func<IntPtr>? statusWindow = null)
        : this(new NativeFocusCaptureEvents(), statusWindow: statusWindow) { }

    public long Generation => Interlocked.Read(ref _generation);

    // Subscribers may only enqueue work; never call an accessibility provider
    // from this native-event callback on the UI thread.
    internal event Action? FocusChanged;

    internal Func<bool> BeginPreparation(Func<long> readInputGeneration)
    {
        Interlocked.Exchange(ref _preparationStartedAt, _tickCount());
        var position = _events.ReadFocus();
        var input = readInputGeneration();
        var foreground = Interlocked.Read(ref _foregroundGeneration);
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        var reported = 0;
        return () =>
        {
            var current = _events.ReadFocus();
            var currentInput = readInputGeneration();
            var currentForeground = Interlocked.Read(ref _foregroundGeneration);
            var accepted = !_disposed && _events.Available && position.Foreground != IntPtr.Zero
                && position.Focused != IntPtr.Zero && input == currentInput
                && foreground == currentForeground && position == current
                && input == readInputGeneration();
            if (!accepted && Interlocked.Exchange(ref reported, 1) == 0)
                DiagnosticTrace.Write("focus_guard.preparation_rejected", new
                {
                    available = _events.Available, disposed = _disposed,
                    originalForeground = position.Foreground.ToInt64(), currentForeground = current.Foreground.ToInt64(),
                    originalFocused = position.Focused.ToInt64(), currentFocused = current.Focused.ToInt64(),
                    originalProcess = position.ProcessId, currentProcess = current.ProcessId,
                    initialInput = input, currentInput, initialForegroundGeneration = foreground,
                    currentForegroundGeneration = currentForeground
                }, recordingId: recordingId);
            return accepted;
        };
    }

    internal WindowsFocusCaptureGuard(IFocusCaptureEvents events, Func<long>? tickCount = null,
        Func<IntPtr>? statusWindow = null)
    {
        _events = events;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _statusWindow = statusWindow ?? (() => IntPtr.Zero);
        _events.Changed += OnChanged;
    }

    public Func<bool> BeginCapture()
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        // WinEvent callbacks are queued on this UI thread. Compare their native
        // occurrence time with this boundary, not the time the queue is drained.
        Interlocked.Exchange(ref _captureStartedAt, _tickCount());
        _captureRecordingId = recordingId;
        _confirmationPending = true;
        var generation = Interlocked.Read(ref _generation);
        var focus = _events.ReadFocus();
        var valid = !_disposed && _events.Available && focus.Foreground != IntPtr.Zero
            && focus.Focused != IntPtr.Zero && generation == Interlocked.Read(ref _generation);
        DiagnosticTrace.Write("focus_guard.begin", new { valid, generation, captureStartedAt = _captureStartedAt,
            hasForeground = focus.Foreground != IntPtr.Zero, hasFocusedControl = focus.Focused != IntPtr.Zero,
            targetProcessId = focus.ProcessId, foregroundWindow = focus.Foreground.ToInt64(),
            focusedWindow = focus.Focused.ToInt64() }, recordingId: recordingId);
        // Process inspection is diagnostic only and never delays target capture.
        if (focus.ProcessId != 0)
            _ = Task.Run(() =>
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)focus.ProcessId);
                    DiagnosticTrace.Write("target.process", new { targetProcessId = focus.ProcessId,
                        processName = process.ProcessName }, recordingId: recordingId);
                }
                catch (Exception error)
                {
                    DiagnosticTrace.Write("target.process_lookup_failed", new { targetProcessId = focus.ProcessId,
                        exceptionType = error.GetType().FullName }, recordingId: recordingId);
                }
            });
        return () =>
        {
            bool SamePosition(FocusCapturePosition current) =>
                focus.Foreground == current.Foreground && focus.Focused == current.Focused;
            var accepted = valid && !_disposed && generation == Interlocked.Read(ref _generation)
                && SamePosition(_events.ReadFocus()) && generation == Interlocked.Read(ref _generation);
            _confirmationPending = false;
            DiagnosticTrace.Write("focus_guard.confirm", new { accepted, valid, disposed = _disposed, initialGeneration = generation, currentGeneration = Interlocked.Read(ref _generation) }, recordingId: recordingId);
            return accepted;
        };
    }

    private void OnChanged(FocusCaptureEvent change)
    {
        var (eventType, window, objectId, childId, timestamp) = change;
        if (_disposed) return;
        // WinForms Show emits a foreground WinEvent for our WS_EX_NOACTIVATE
        // status overlay even though Windows keeps the editor foreground. Ignore
        // only that known overlay's synthetic event, never an actual activation.
        if (eventType == ForegroundEvent && window != IntPtr.Zero
            && window == _statusWindow() && _events.ReadFocus().Foreground != window) return;
        var selection = eventType is TextSelectionEvent or Ia2CaretEvent or Ia2SelectionEvent
            || eventType == LocationEvent && objectId == CaretObject;
        if (eventType != ForegroundEvent
            && (eventType != FocusEvent && !selection || !_events.IsInFocusedHierarchy(window))) return;

        var deliveredAt = _tickCount();
        var captureStartedAt = Interlocked.Read(ref _captureStartedAt);
        // Native timestamps wrap every 2^32 ms. Resolve a recent event against
        // the 64-bit delivery clock. Ambiguous/future timestamps remain changes.
        var age = unchecked((uint)deliveredAt - timestamp);
        var preparationStartedAt = Interlocked.Read(ref _preparationStartedAt);
        var beforePreparation = preparationStartedAt >= 0 && deliveredAt >= preparationStartedAt
            && age <= int.MaxValue && age > deliveredAt - preparationStartedAt;
        if (eventType == ForegroundEvent && !beforePreparation)
        {
            if (preparationStartedAt >= 0 && deliveredAt - preparationStartedAt <= 5000)
                DiagnosticTrace.Write("focus_guard.preparation_foreground", new
                {
                    eventWindow = window.ToInt64(), timestamp, deliveredAt, preparationStartedAt,
                    actualForeground = _events.ReadFocus().Foreground.ToInt64()
                });
            Interlocked.Increment(ref _foregroundGeneration);
        }
        var beforeCapture = captureStartedAt >= 0 && deliveredAt >= captureStartedAt
            && age <= int.MaxValue && age > deliveredAt - captureStartedAt;
        if (!beforeCapture) Interlocked.Increment(ref _generation);
        if (eventType is ForegroundEvent or FocusEvent) FocusChanged?.Invoke();
        // A cancelled/watchdog-expired session may never confirm. Do not keep
        // attributing unrelated editor activity to that abandoned recording.
        if (_confirmationPending && deliveredAt - captureStartedAt > 5000)
            _confirmationPending = false;
        if (_confirmationPending)
            DiagnosticTrace.Write("focus_guard.event", new { eventType, window = window.ToInt64(),
                objectId, childId, timestamp, deliveredAt, captureStartedAt,
                ageMilliseconds = age, ignoredBeforeCapture = beforeCapture,
                generation = Interlocked.Read(ref _generation) }, recordingId: _captureRecordingId);
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
        public event Action<FocusCaptureEvent>? Changed;
        public bool Available { get; }

        public NativeFocusCaptureEvents()
        {
            _callback = (_, eventType, window, objectId, childId, _, eventTime) =>
                Changed?.Invoke(new(eventType, window, objectId, childId, eventTime));
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
            var thread = GetWindowThreadProcessId(foreground, out var process);
            return GetGUIThreadInfo(thread, ref info)
                ? new FocusCapturePosition(foreground, info.Focused) { ProcessId = process } : default;
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

internal readonly record struct FocusCapturePosition(IntPtr Foreground, IntPtr Focused)
{
    public uint ProcessId { get; init; }
}

internal readonly record struct FocusCaptureEvent(uint EventType, IntPtr Window, int ObjectId,
    int ChildId, uint Timestamp);

internal interface IFocusCaptureEvents : IDisposable
{
    bool Available { get; }
    event Action<FocusCaptureEvent>? Changed;
    FocusCapturePosition ReadFocus();
    bool IsInFocusedHierarchy(IntPtr window);
}
