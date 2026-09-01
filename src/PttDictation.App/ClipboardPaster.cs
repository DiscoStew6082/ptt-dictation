using PttDictation.Core;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal sealed class ClipboardPaster : IClipboardPaster
{
    private static readonly IClipboardRestoreQueue SharedRestoreQueue =
        new ClipboardRestoreQueue(TimeSpan.FromMilliseconds(750));

    private readonly IClipboardPasteBackend _clipboard;
    private readonly IClipboardRestoreQueue _restoreQueue;
    private IntPtr _capturedTarget;

    public ClipboardPaster()
        : this(new WindowsClipboardPasteBackend(), SharedRestoreQueue)
    {
    }

    internal ClipboardPaster(
        IClipboardPasteBackend clipboard,
        IClipboardRestoreQueue restoreQueue)
    {
        _clipboard = clipboard;
        _restoreQueue = restoreQueue;
    }

    public void CaptureTarget()
    {
        _capturedTarget = GetForegroundWindow();
    }

    public async Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        var previous = _clipboard.GetDataObject();
        var clipboardChanged = false;
        try
        {
            _clipboard.SetText(text);
            clipboardChanged = true;
            if (_capturedTarget != IntPtr.Zero && IsWindow(_capturedTarget))
            {
                SetForegroundWindow(_capturedTarget);
                await Task.Delay(75, cancellationToken);
            }

            _clipboard.SendPaste();
        }
        finally
        {
            if (clipboardChanged)
            {
                _restoreQueue.Enqueue(() => _clipboard.RestoreIfUnchanged(text, previous));
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

internal interface IClipboardPasteBackend
{
    IDataObject? GetDataObject();

    void SetText(string text);

    void SendPaste();

    void RestoreIfUnchanged(string pastedText, IDataObject? previous);
}

internal sealed class WindowsClipboardPasteBackend : IClipboardPasteBackend
{
    public IDataObject? GetDataObject() => Clipboard.GetDataObject();

    public void SetText(string text) => Clipboard.SetText(text);

    public void SendPaste() => SendKeys.SendWait("^v");

    public void RestoreIfUnchanged(string pastedText, IDataObject? previous)
    {
        if (previous is null)
        {
            return;
        }

        if (Clipboard.ContainsText() && Clipboard.GetText() == pastedText)
        {
            Clipboard.SetDataObject(previous, copy: true);
        }
    }
}

internal interface IClipboardRestoreQueue
{
    void Enqueue(Action restore);
}

internal sealed class ClipboardRestoreQueue : IClipboardRestoreQueue, IDisposable
{
    private readonly BlockingCollection<Action> _work = [];
    private readonly TimeSpan _delay;
    private readonly Thread _thread;
    private bool _disposed;

    public ClipboardRestoreQueue(TimeSpan delay)
    {
        _delay = delay;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PTT Dictation clipboard restore"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Enqueue(Action restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _work.Add(restore);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.CompleteAdding();
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _work.Dispose();
        }
    }

    private void Run()
    {
        foreach (var restore in _work.GetConsumingEnumerable())
        {
            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    Thread.Sleep(_delay);
                }

                restore();
            }
            catch (Exception)
            {
                // Clipboard restoration is best effort and must never block later dictation.
            }
        }
    }
}
