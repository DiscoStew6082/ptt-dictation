using PttDictation.Core;

namespace PttDictation.App;

// A dedicated message pump keeps native hook delivery independent of UIA and the tray UI.
// Subscribers must post their work elsewhere rather than block this thread.
internal sealed class KeyboardHookThread : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<SynchronizationContext> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal KeyboardHookThread(Action install, Action uninstall)
    {
        _thread = new Thread(() => Run(install, uninstall))
        {
            IsBackground = true,
            Name = "Dictation keyboard hook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    internal void Post(Action action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _ready.Task.GetAwaiter().GetResult().Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_thread.IsAlive) return;
        _ready.Task.GetAwaiter().GetResult().Post(_ => Application.ExitThread(), null);
        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(2)))
            DiagnosticTrace.Write("hotkey.shutdown_timeout");
    }

    private void Run(Action install, Action uninstall)
    {
        try
        {
            using var context = new ApplicationContext();
            using var synchronization = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            install();
            try
            {
                _ready.TrySetResult(synchronization);
                Application.Run(context);
            }
            finally { uninstall(); }
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            DiagnosticTrace.Write("hotkey.thread_failed", error: error);
        }
    }
}
