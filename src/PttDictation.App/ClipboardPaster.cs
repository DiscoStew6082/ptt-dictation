using PttDictation.Core;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal sealed class ClipboardPaster : IClipboardPaster
{
    private static readonly IClipboardRestoreQueue SharedRestoreQueue =
        new ClipboardRestoreQueue(TimeSpan.FromMilliseconds(750));

    private readonly IClipboardPasteBackend _clipboard;
    private readonly IClipboardRestoreQueue _restoreQueue;
    private readonly IForegroundWindowBackend _foregroundWindow;
    private readonly object _clipboardOwnershipSync = new();
    private IDataObject? _originalClipboard;
    private uint? _ownedClipboardSequence;
    private IntPtr _capturedTarget;

    public ClipboardPaster()
        : this(new WindowsClipboardPasteBackend(), SharedRestoreQueue, new WindowsForegroundWindowBackend())
    {
    }

    internal ClipboardPaster(
        IClipboardPasteBackend clipboard,
        IClipboardRestoreQueue restoreQueue,
        IForegroundWindowBackend foregroundWindow)
    {
        _clipboard = clipboard;
        _restoreQueue = restoreQueue;
        _foregroundWindow = foregroundWindow;
    }

    public void CaptureTarget()
    {
        _capturedTarget = _foregroundWindow.GetForegroundWindow();
    }

    public async Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        if (_capturedTarget == IntPtr.Zero || !_foregroundWindow.IsWindow(_capturedTarget))
        {
            throw new InvalidOperationException("The original window is no longer available. Nothing was pasted.");
        }

        if (_foregroundWindow.GetForegroundWindow() != _capturedTarget)
        {
            if (!_foregroundWindow.SetForegroundWindow(_capturedTarget))
            {
                throw new InvalidOperationException("Windows could not return focus to the original window. Nothing was pasted.");
            }

            await Task.Delay(75, cancellationToken);
        }

        IDataObject? previous = null;
        var clipboardChanged = false;
        uint clipboardSequence = 0;
        try
        {
            lock (_clipboardOwnershipSync)
            {
                previous = GetOriginalClipboardSnapshot();
                clipboardSequence = _clipboard.SetText(text);
                clipboardChanged = true;
                TrackClipboardOwnership(clipboardSequence, previous);
                if (_foregroundWindow.GetForegroundWindow() != _capturedTarget
                    || !_clipboard.IsCurrent(clipboardSequence, text))
                {
                    throw new InvalidOperationException("The paste target or clipboard changed. Nothing was pasted.");
                }

                _clipboard.SendPaste();
            }

            _restoreQueue.Enqueue(() => RestoreOwnedClipboard(clipboardSequence, previous));
            clipboardChanged = false;
        }
        finally
        {
            if (clipboardChanged)
            {
                _restoreQueue.EnqueueImmediate(() => RestoreOwnedClipboard(clipboardSequence, previous));
            }
        }
    }

    private IDataObject? GetOriginalClipboardSnapshot()
    {
        lock (_clipboardOwnershipSync)
        {
            if (_ownedClipboardSequence is { } sequence && _clipboard.IsSequenceCurrent(sequence))
            {
                return _originalClipboard;
            }

            _ownedClipboardSequence = null;
            _originalClipboard = null;
        }

        return _clipboard.GetDataObject();
    }

    private void TrackClipboardOwnership(uint sequence, IDataObject? originalClipboard)
    {
        lock (_clipboardOwnershipSync)
        {
            _ownedClipboardSequence = sequence;
            _originalClipboard = originalClipboard;
        }
    }

    private void RestoreOwnedClipboard(uint sequence, IDataObject? originalClipboard)
    {
        lock (_clipboardOwnershipSync)
        {
            try
            {
                _clipboard.RestoreIfCurrent(sequence, originalClipboard);
            }
            finally
            {
                if (_ownedClipboardSequence == sequence)
                {
                    _ownedClipboardSequence = null;
                    _originalClipboard = null;
                }
            }
        }
    }
}

internal interface IForegroundWindowBackend
{
    IntPtr GetForegroundWindow();

    bool IsWindow(IntPtr window);

    bool SetForegroundWindow(IntPtr window);
}

internal sealed class WindowsForegroundWindowBackend : IForegroundWindowBackend
{
    public IntPtr GetForegroundWindow() => NativeGetForegroundWindow();

    public bool IsWindow(IntPtr window) => NativeIsWindow(window);

    public bool SetForegroundWindow(IntPtr window) => NativeSetForegroundWindow(window);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetForegroundWindow(IntPtr hWnd);
}

internal interface IClipboardPasteBackend
{
    IDataObject? GetDataObject();

    uint SetText(string text);

    bool IsSequenceCurrent(uint expectedSequence);

    bool IsCurrent(uint expectedSequence, string pastedText);

    void SendPaste();

    void RestoreIfCurrent(uint expectedSequence, IDataObject? previous);
}

internal sealed class WindowsClipboardPasteBackend : IClipboardPasteBackend
{
    private readonly IWindowsClipboardApi _clipboard;

    public WindowsClipboardPasteBackend()
        : this(new WindowsClipboardApi())
    {
    }

    internal WindowsClipboardPasteBackend(IWindowsClipboardApi clipboard)
    {
        _clipboard = clipboard;
    }

    public IDataObject? GetDataObject()
    {
        var source = _clipboard.GetDataObject();
        if (source is null)
        {
            return null;
        }

        var formats = source.GetFormats(autoConvert: false);
        if (formats.Length == 0)
        {
            return null;
        }

        var snapshot = new DataObject();
        var copiedFormats = 0;
        foreach (var format in formats)
        {
            try
            {
                var value = source.GetData(format, autoConvert: false);
                if (value is null)
                {
                    continue;
                }

                snapshot.SetData(format, autoConvert: false, DetachClipboardValue(value));
                copiedFormats++;
            }
            catch (ExternalException)
            {
            }
        }

        if (copiedFormats == 0)
        {
            throw new ExternalException("Windows could not snapshot the current clipboard. Nothing was pasted.");
        }

        return snapshot;
    }

    public uint SetText(string text)
    {
        _clipboard.SetText(text);
        return _clipboard.GetSequenceNumber();
    }

    public bool IsCurrent(uint expectedSequence, string pastedText)
    {
        return _clipboard.GetSequenceNumber() == expectedSequence
            && _clipboard.ContainsText()
            && _clipboard.GetText() == pastedText;
    }

    public bool IsSequenceCurrent(uint expectedSequence)
    {
        return _clipboard.GetSequenceNumber() == expectedSequence;
    }

    public void SendPaste() => SendKeys.SendWait("^v");

    public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
    {
        if (_clipboard.GetSequenceNumber() != expectedSequence)
        {
            return;
        }

        if (previous is null)
        {
            _clipboard.Clear();
        }
        else
        {
            _clipboard.SetDataObject(previous);
        }
    }

    private static object DetachClipboardValue(object value)
    {
        return value switch
        {
            byte[] bytes => bytes.ToArray(),
            MemoryStream stream => new MemoryStream(stream.ToArray(), writable: false),
            Stream stream => CopyStream(stream),
            ICloneable cloneable => cloneable.Clone() ?? value,
            _ => value
        };
    }

    private static MemoryStream CopyStream(Stream source)
    {
        var originalPosition = source.CanSeek ? source.Position : (long?)null;
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        if (originalPosition is { } position)
        {
            source.Position = position;
        }

        return copy;
    }
}

internal interface IWindowsClipboardApi
{
    IDataObject? GetDataObject();

    void SetText(string text);

    bool ContainsText();

    string GetText();

    void SetDataObject(IDataObject data);

    void Clear();

    uint GetSequenceNumber();
}

internal sealed class WindowsClipboardApi : IWindowsClipboardApi
{
    public IDataObject? GetDataObject() => Clipboard.GetDataObject();

    public void SetText(string text) => Clipboard.SetText(text);

    public bool ContainsText() => Clipboard.ContainsText();

    public string GetText() => Clipboard.GetText();

    public void SetDataObject(IDataObject data) => Clipboard.SetDataObject(data, copy: true);

    public void Clear() => Clipboard.Clear();

    public uint GetSequenceNumber() => NativeGetClipboardSequenceNumber();

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint NativeGetClipboardSequenceNumber();
}

internal interface IClipboardRestoreQueue
{
    void Enqueue(Action restore);

    void EnqueueImmediate(Action restore);
}

internal sealed class ClipboardRestoreQueue : IClipboardRestoreQueue, IDisposable
{
    private readonly AutoResetEvent _workAvailable = new(initialState: false);
    private readonly object _sync = new();
    private readonly TimeSpan _delay;
    private readonly Thread _thread;
    private (Action Restore, DateTimeOffset DueAt)? _pendingRestore;
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
        Enqueue(restore, applyDelay: true);
    }

    public void EnqueueImmediate(Action restore)
    {
        Enqueue(restore, applyDelay: false);
    }

    private void Enqueue(Action restore, bool applyDelay)
    {
        ArgumentNullException.ThrowIfNull(restore);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Coalesce pending work so a stalled clipboard call retains only the latest snapshot.
            var dueAt = applyDelay ? DateTimeOffset.UtcNow + _delay : DateTimeOffset.UtcNow;
            _pendingRestore = (restore, dueAt);
            _workAvailable.Set();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pendingRestore is { } pending)
            {
                _pendingRestore = (pending.Restore, DateTimeOffset.UtcNow);
            }

            _workAvailable.Set();
        }

        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _workAvailable.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            (Action Restore, DateTimeOffset DueAt)? work = null;
            TimeSpan? waitDuration = null;
            lock (_sync)
            {
                if (_pendingRestore is { } pending)
                {
                    var remaining = pending.DueAt - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        work = pending;
                        _pendingRestore = null;
                    }
                    else
                    {
                        waitDuration = remaining;
                    }
                }
                else if (_disposed)
                {
                    return;
                }
            }

            if (work is { } ready)
            {
                try
                {
                    ready.Restore();
                }
                catch (Exception)
                {
                    // Clipboard restoration is best effort and must never interfere with later dictation.
                }

                continue;
            }

            _workAvailable.WaitOne(waitDuration ?? Timeout.InfiniteTimeSpan);
        }
    }
}
