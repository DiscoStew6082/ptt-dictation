using PttDictation.Core;
using System.Runtime.InteropServices;

namespace PttDictation.App;

// Raised only before keyboard input, while the clipboard sequence is still ours.
// A temporary failed read must not be confused with an ambiguous submitted paste.
internal sealed class ClipboardReadUnavailableException : InvalidOperationException
{
    public ClipboardReadUnavailableException() : base("The dictation clipboard could not be read yet. No paste was sent.") { }
}

internal interface IPendingClipboardPaste : IDisposable
{
    // Exact target readback proves consumption. Dispose abandons an uncertain
    // paste without putting unrelated clipboard contents beneath queued input.
    void Acknowledge();
}

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

    internal ClipboardPaster(ApartmentWorker clipboardWorker)
        : this(new WindowsClipboardPasteBackend(), SharedRestoreQueue,
            new WindowsForegroundWindowBackend(), clipboardWorker) { }

    internal ClipboardPaster(
        IClipboardPasteBackend clipboard,
        IClipboardRestoreQueue restoreQueue,
        IForegroundWindowBackend foregroundWindow, ApartmentWorker? clipboardWorker = null)
    {
        _clipboard = clipboardWorker is null ? clipboard : new ApartmentClipboardBackend(clipboard, clipboardWorker);
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

        cancellationToken.ThrowIfCancellationRequested();
        PasteToCurrentTarget(text, () => _foregroundWindow.GetForegroundWindow() == _capturedTarget);
    }

    internal void PasteToCurrentTarget(string text, Func<bool> targetIsCurrent)
        => PasteToCurrentTargetCore(text, targetIsCurrent, awaitAcknowledgement: false);

    internal IPendingClipboardPaste PasteToCurrentTargetAwaitingAcknowledgement(string text, Func<bool> targetIsCurrent)
        => PasteToCurrentTargetCore(text, targetIsCurrent, awaitAcknowledgement: true)!;

    private IPendingClipboardPaste? PasteToCurrentTargetCore(string text, Func<bool> targetIsCurrent, bool awaitAcknowledgement)
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        DiagnosticTrace.Write("clipboard.paste_started", new { text }, recordingId: recordingId);
        if (!targetIsCurrent())
        {
            DiagnosticTrace.Write("clipboard.target_rejected", recordingId: recordingId);
            throw new InvalidOperationException("The original textbox is not focused. Nothing was pasted.");
        }

        IDataObject? previous = null;
        var clipboardChanged = false;
        var sendAttempted = false;
        uint clipboardSequence = 0;
        var stage = "snapshot";
        try
        {
            lock (_clipboardOwnershipSync)
            {
                previous = GetOriginalClipboardSnapshot();
                DiagnosticTrace.Write("clipboard.snapshot_completed", recordingId: recordingId);
                stage = "set_text";
                clipboardSequence = _clipboard.SetText(text);
                DiagnosticTrace.Write("clipboard.text_set", new { clipboardSequence }, recordingId: recordingId);
                clipboardChanged = true;
                TrackClipboardOwnership(clipboardSequence, previous);
                stage = "validate";
                var clipboardCurrent = _clipboard.IsCurrent(clipboardSequence, text);
                var targetCurrent = targetIsCurrent();
                DiagnosticTrace.Write("clipboard.validate", new { clipboardSequence, clipboardCurrent, targetCurrent }, recordingId: recordingId);
                if (!clipboardCurrent && targetCurrent && _clipboard.IsSequenceCurrent(clipboardSequence))
                {
                    throw new ClipboardReadUnavailableException();
                }
                // Target validation can enter a slow external provider. A user
                // copy during that call invalidates the earlier clipboard read.
                if (!clipboardCurrent || !targetCurrent || !_clipboard.IsSequenceCurrent(clipboardSequence))
                {
                    throw new InvalidOperationException("The paste target or clipboard changed. Nothing was pasted.");
                }

                stage = "send_paste";
                sendAttempted = true;
                _clipboard.SendPaste();
                DiagnosticTrace.Write("clipboard.paste_sent", new { clipboardSequence }, recordingId: recordingId);
            }

            Action restore = () => RestoreOwnedClipboard(clipboardSequence, previous, recordingId);
            if (awaitAcknowledgement)
            {
                // SendInput only queues a paste. A slow browser can read the
                // clipboard after the legacy timer would already restore it.
                // Only exact text readback can restore. Cancellation/failure
                // abandons restoration because queued input may still consume
                // this payload; the original snapshot remains chained for the
                // next acknowledged write. Never resend an uncertain paste.
                var lease = new PendingRestore(
                    () => _restoreQueue.EnqueueImmediate(restore),
                    () => DiagnosticTrace.Write("clipboard.restore_abandoned",
                        new { clipboardSequence, reason = "paste_unacknowledged" }, recordingId: recordingId));
                clipboardChanged = false;
                return lease;
            }
            stage = "queue_restore";
            _restoreQueue.Enqueue(restore);
            clipboardChanged = false;
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticTrace.Write("clipboard.paste_failed", new { stage, clipboardSequence, clipboardChanged }, ex, recordingId);
            throw;
        }
        finally
        {
            if (clipboardChanged && !sendAttempted)
            {
                _restoreQueue.EnqueueImmediate(() => RestoreOwnedClipboard(clipboardSequence, previous, recordingId));
            }
            else if (clipboardChanged)
            {
                // SendInput may have accepted a prefix containing Ctrl+V before
                // throwing. Restoring here could paste the user's old clipboard.
                DiagnosticTrace.Write("clipboard.restore_abandoned",
                    new { clipboardSequence, reason = "paste_submission_uncertain" }, recordingId: recordingId);
            }
        }
    }

    private sealed class PendingRestore(Action restore, Action abandoned) : IPendingClipboardPaste
    {
        private int _completed;
        public void Acknowledge()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) restore();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) abandoned();
        }
    }

    // Clipboard/OLE calls belong to the pumped STA. Target validation and
    // SendInput stay on the caller's automation MTA; no UIA reference crosses
    // into the clipboard apartment and no STA callback waits on that MTA.
    private sealed class ApartmentClipboardBackend(IClipboardPasteBackend backend, ApartmentWorker worker)
        : IClipboardPasteBackend
    {
        private T Read<T>(Func<T> read)
        {
            T result = default!;
            worker.InvokeAsync(() => result = read()).GetAwaiter().GetResult();
            return result;
        }

        public IDataObject? GetDataObject() => Read(backend.GetDataObject);
        public uint SetText(string text) => Read(() => backend.SetText(text));
        public bool IsCurrent(uint sequence, string text) => Read(() => backend.IsCurrent(sequence, text));
        public bool IsSequenceCurrent(uint sequence) => backend.IsSequenceCurrent(sequence);
        public void SendPaste() => backend.SendPaste();
        public void RestoreIfCurrent(uint sequence, IDataObject? previous)
        {
            // The restore queue owns an independent STA and can outlive this
            // clipboard worker when an acknowledged session shuts down.
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                backend.RestoreIfCurrent(sequence, previous);
            else
                worker.InvokeAsync(() => backend.RestoreIfCurrent(sequence, previous)).GetAwaiter().GetResult();
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

    private void RestoreOwnedClipboard(uint sequence, IDataObject? originalClipboard, string? recordingId)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(recordingId ?? string.Empty);
        lock (_clipboardOwnershipSync)
        {
            try
            {
                DiagnosticTrace.Write("clipboard.restore_started", new { sequence }, recordingId: recordingId);
                _clipboard.RestoreIfCurrent(sequence, originalClipboard);
                DiagnosticTrace.Write("clipboard.restore_returned", new { sequence }, recordingId: recordingId);
            }
            catch (Exception ex)
            {
                DiagnosticTrace.Write("clipboard.restore_failed", new { sequence }, ex, recordingId);
                throw;
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
            catch (ExternalException ex)
            {
                DiagnosticTrace.Write("clipboard.format_snapshot_failed", error: ex);
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
        var before = _clipboard.GetSequenceNumber();
        if (before != expectedSequence) return false;
        var containsText = _clipboard.ContainsText();
        var textMatches = containsText && _clipboard.GetText() == pastedText;
        var after = _clipboard.GetSequenceNumber();
        var current = textMatches && after == expectedSequence;
        if (!current)
            DiagnosticTrace.Write("clipboard.read_unconfirmed", new { expectedSequence, before, after, containsText, textMatches });
        return current;
    }

    public bool IsSequenceCurrent(uint expectedSequence)
    {
        return _clipboard.GetSequenceNumber() == expectedSequence;
    }

    public void SendPaste() => WindowsPasteInput.SendPaste();

    public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
    {
        if (_clipboard.GetSequenceNumber() != expectedSequence)
        {
            DiagnosticTrace.Write("clipboard.restore_skipped", new { expectedSequence, reason = "sequence_changed" });
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
        DiagnosticTrace.Write("clipboard.restore_applied", new { expectedSequence });
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
