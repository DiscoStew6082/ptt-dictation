using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using PttDictation.Core;

namespace PttDictation.App;

// Keep browser accessibility initialized before CaptureTarget establishes its
// immutable focus/selection boundary. Never cache an insertion target here.
internal sealed class WindowsAccessibilityWarmup : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _requested = new(false);
    private readonly ManualResetEvent _stopped = new(false);
    private readonly SemaphoreSlim _providerGate = new(0, 1);
    private readonly Func<Action, IDisposable> _subscribe;
    private readonly Func<bool> _probe;
    private readonly Func<bool> _isIdle;
    private readonly ApartmentWorker? _providerWorker;
    private readonly TimeSpan _interval;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal Task Completion => _completion.Task;

    internal WindowsAccessibilityWarmup(Func<bool> isIdle, ApartmentWorker? providerWorker = null)
        : this(Subscribe, new BrowserProbe().Read, isIdle, providerWorker: providerWorker) { }

    internal WindowsAccessibilityWarmup(Func<Action, IDisposable> subscribe, Func<bool> probe,
        Func<bool>? isIdle = null, TimeSpan? interval = null, ApartmentWorker? providerWorker = null)
    {
        _subscribe = subscribe;
        _probe = probe;
        _isIdle = isIdle ?? (() => true);
        _providerWorker = providerWorker;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        var thread = new Thread(Run) { IsBackground = true, Name = "Dictation browser accessibility" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Request()
    {
        lock (_gate) { if (!_disposed) _requested.Set(); }
    }

    internal async Task<IDisposable> AcquireCaptureAsync(CancellationToken cancellationToken)
    {
        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed) return new CaptureLease(_providerGate);
        }
        _providerGate.Release();
        throw new ObjectDisposedException(nameof(WindowsAccessibilityWarmup));
    }

    private sealed class CaptureLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }

    public void Dispose()
    {
        // Provider calls cannot be cancelled. Shutdown must not wait on one.
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopped.Set();
        }
    }

    private void Run()
    {
        IDisposable? subscription = null;
        try
        {
            try { subscription = OnProviderWorker(() => _subscribe(Request)); }
            catch (Exception error) { TraceFailure("subscribe", error); }
            finally { _providerGate.Release(); }
            var signals = new WaitHandle[] { _stopped, _requested };
            string? lastFailure = null;
            while (!_stopped.WaitOne(0))
            {
                for (var attempt = 0; attempt < 3 && !_stopped.WaitOne(0); attempt++)
                {
                    if (!_isIdle() || !_providerGate.Wait(0)) break;
                    try
                    {
                        var ready = OnProviderWorker(_probe);
                        lastFailure = null;
                        if (ready) break;
                    }
                    catch (Exception error)
                    {
                        var failure = error.GetType().FullName;
                        if (failure != lastFailure) TraceFailure("probe", error);
                        lastFailure = failure;
                        break;
                    }
                    finally { _providerGate.Release(); }
                    if (_stopped.WaitOne(TimeSpan.FromMilliseconds(50))) break;
                }
                // Coalesce event bursts without losing a request received during
                // a provider call. A quiet fallback also covers a new browser tab
                // whose cold provider hasn't started sending focus events yet.
                if (_stopped.WaitOne(TimeSpan.FromMilliseconds(100))) break;
                if (WaitHandle.WaitAny(signals, _interval) == 0) break;
            }
        }
        finally
        {
            try { OnProviderWorker(() => { subscription?.Dispose(); return true; }); }
            catch (Exception error) { TraceFailure("unsubscribe", error); }
            lock (_gate)
            {
                _disposed = true;
                _requested.Dispose();
                _stopped.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    // The coordinator owns only scheduling and the provider lease. Native UIA
    // subscription, warmup, capture and removal share one serial MTA client.
    private T OnProviderWorker<T>(Func<T> action)
    {
        if (_providerWorker is null) return action();
        T result = default!;
        _providerWorker.InvokeAsync(() => result = action()).GetAwaiter().GetResult();
        return result;
    }

    private static void TraceFailure(string operation, Exception error) =>
        DiagnosticTrace.Write("accessibility.warmup_failed", new { operation, exceptionType = error.GetType().FullName });

    private static IDisposable Subscribe(Action request)
    {
        AutomationFocusChangedEventHandler handler = (_, _) => request();
        Automation.AddAutomationFocusChangedEventHandler(handler);
        DiagnosticTrace.Write("accessibility.listening_started");
        return new FocusSubscription(handler);
    }

    private sealed class FocusSubscription(AutomationFocusChangedEventHandler handler) : IDisposable
    {
        public void Dispose() => Automation.RemoveAutomationFocusChangedEventHandler(handler);
    }

    private sealed class BrowserProbe
    {
        private (IntPtr Window, string ControlType, bool HasTextPattern)? _last;

        public bool Read()
        {
            var window = GetForegroundWindow();
            var className = new StringBuilder(256);
            if (window == IntPtr.Zero || GetClassName(window, className, className.Capacity) == 0) return true;
            var name = className.ToString();
            if (!name.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)
                && name != "MozillaWindowClass") { _last = null; return true; }
            var focused = AutomationElement.FocusedElement;
            if (focused is null || GetForegroundWindow() != window) return true;
            // Requesting TextPattern activates browser web-text accessibility. Do
            // not read text/selection, walk the document, focus, or retain it.
            var hasTextPattern = focused.TryGetCurrentPattern(TextPattern.Pattern, out _);
            var controlType = focused.Current.ControlType.ProgrammaticName;
            var observation = (window, controlType, hasTextPattern);
            if (_last != observation)
            {
                _last = observation;
                DiagnosticTrace.Write("accessibility.browser_warmed", new { controlType,
                    hasTextPattern, processId = focused.Current.ProcessId });
            }
            return hasTextPattern;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximum);
}
