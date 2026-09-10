using System.Collections.Concurrent;
using PttDictation.Core;

namespace PttDictation.App;

// Each recording has its own output state. UIA runs on a serial MTA worker;
// clipboard operations run on an STA worker. Neither blocks the hotkey/UI loop.
internal sealed class LiveClipboardPaster : ILiveClipboardPaster, IDisposable
{
    private const long FinalFocusWaitMilliseconds = 5000;
    private readonly Func<Func<IWindowsTextTarget>> _prepareCapture;
    private readonly Func<Func<bool>> _beginCaptureGuard;
    private readonly Action<Action> _confirmCapture;
    private readonly WindowsFocusCaptureGuard? _focusGuard;
    private readonly Action<string, Func<bool>> _paste;
    private readonly Func<bool> _canPaste;
    private readonly System.Windows.Forms.Timer? _timer;
    private readonly ApartmentWorker? _automation;
    private readonly ApartmentWorker? _clipboard;
    private Session? _session;
    private int _queued;

    public LiveClipboardPaster()
        : this(WindowsTextTarget.Capture, new ClipboardPaster().PasteToCurrentTarget,
            startTimer: true, canPaste: () => WindowsPasteInput.CanPasteNow)
    { }

    internal LiveClipboardPaster(Func<IWindowsTextTarget> capture, Action<string, Func<bool>> paste,
        bool startTimer = false, Func<bool>? canPaste = null, Func<Func<bool>>? beginCaptureGuard = null)
    {
        _prepareCapture = () => capture;
        _beginCaptureGuard = beginCaptureGuard ?? (() => () => true);
        _confirmCapture = action => action();
        _paste = paste;
        _canPaste = canPaste ?? (() => true);
        if (startTimer)
        {
            _focusGuard = new WindowsFocusCaptureGuard();
            _beginCaptureGuard = _focusGuard.BeginCapture;
            // Resolve only the original focused identity before returning from
            // the hotkey. All document/provider reads then happen on the worker.
            _prepareCapture = WindowsTextTarget.CaptureFocusedIdentity;
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _confirmCapture = action => ui.Post(_ => action(), null);
            _automation = new ApartmentWorker(ApartmentState.MTA, "Dictation text target");
            _clipboard = new ApartmentWorker(ApartmentState.STA, "Dictation live paste");
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (_, _) => Pump();
        }
    }

    public bool InlinePreview { get; private set; }
    public string? HoldingReason { get; private set; }
    public event Action? PresentationChanged;

    public void CaptureTarget()
    {
        EndSession();
        var session = new Session { BusySince = Environment.TickCount64, RecordingId = DiagnosticTrace.CurrentRecordingId };
        Trace(session, "inline.capture_started");
        Volatile.Write(ref _session, session);
        SetPresentation(false, null);
        var unchanged = _beginCaptureGuard();
        Func<IWindowsTextTarget> capture;
        try { capture = _prepareCapture(); }
        catch (Exception ex)
        {
            Trace(session, "inline.identity_capture_failed", error: ex);
            session.Conflicted = session.Ready = true;
            session.Failure = ex.Message;
            Interlocked.Exchange(ref session.BusySince, 0);
            _timer?.Start();
            return;
        }
        Dispatch(() =>
        {
            using var traceScope = DiagnosticTrace.EnterRecording(session.RecordingId ?? string.Empty);
            if (!IsCurrent(session)) return;
            try
            {
                session.Target = capture();
                session.Fallback = !session.Target.SupportsReplacement;
                session.Conflicted = session.Fallback && !session.Target.CanPasteFallback;
                if (session.Conflicted) session.Failure = "This field cannot safely receive dictation.";
                Trace(session, "inline.capture_result", new { session.Fallback, session.Conflicted });
            }
            catch (Exception ex)
            {
                Trace(session, "inline.capture_failed", error: ex);
                session.Conflicted = true; session.Failure = ex.Message;
            }
            finally
            {
                // Process queued native focus/selection events before accepting
                // the snapshot. Retire the guard here so later focus returns work.
                _confirmCapture(() =>
                {
                    if (!IsCurrent(session)) return;
                    if (!unchanged())
                    {
                        Trace(session, "inline.capture_rejected", new { reason = "focus_or_selection_changed" });
                        session.Conflicted = true;
                        session.Failure = "The textbox or selection changed while dictation was starting.";
                    }
                    session.Ready = true;
                    Trace(session, "inline.capture_ready", new { session.Conflicted, session.Fallback });
                    Interlocked.Exchange(ref session.BusySince, 0);
                    SetPresentation(!session.Fallback && !session.Conflicted, null);
                });
            }
        });
        _timer?.Start();
    }

    public void UpdatePreview(string text)
    {
        var session = Volatile.Read(ref _session);
        if (session is null) return;
        lock (session)
        {
            if (session.Active && !session.Finishing && !string.IsNullOrWhiteSpace(text)) session.Desired = text;
        }
    }

    public async Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session)
            ?? throw new InvalidOperationException("No dictation textbox was captured.");
        lock (session)
        {
            session.Desired = text;
            session.FinishingSince = Environment.TickCount64;
            session.Finishing = true;
        }
        Trace(session, "inline.final_requested", new { text });
        Pump();
        await session.Completion.Task.WaitAsync(cancellationToken);
    }

    internal void Pump()
    {
        var session = Volatile.Read(ref _session);
        if (session is null || !session.Active) return;
        // A timed-out external operation loses permission to write even if it
        // returns later. Recognition continues independently of this worker.
        if (Interlocked.Read(ref session.BusySince) is var started && started != 0
            && Environment.TickCount64 - started > 5000)
        {
            Trace(session, "inline.watchdog_timeout", new { elapsedMilliseconds = Environment.TickCount64 - started });
            session.Active = false;
            session.Completion.TrySetException(new InvalidOperationException("The original editor stopped responding."));
            SetPresentation(false, "Text updates stopped. Your transcript will be kept in Session History.");
            return;
        }
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Interlocked.Exchange(ref session.BusySince, Environment.TickCount64);
        Dispatch(() =>
        {
            try { if (IsCurrent(session)) PumpSession(session); }
            finally { Interlocked.Exchange(ref session.BusySince, 0); Interlocked.Exchange(ref _queued, 0); }
        });
    }

    private void PumpSession(Session session)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(session.RecordingId ?? string.Empty);
        try
        {
            if (!session.Ready) return;
            if (session.Conflicted)
            {
                SetPresentation(false, "Text updates paused. Your completed transcript will be kept in Session History.");
                if (session.Finishing) session.Completion.TrySetException(new InvalidOperationException(session.Failure
                    ?? "The original textbox changed, so dictation stopped replacing text."));
                return;
            }
            var target = session.Target!;
            if (!target.IsFocused)
            {
                if (session.Finishing && Environment.TickCount64 - session.FinishingSince >= FinalFocusWaitMilliseconds)
                    throw new InvalidOperationException("The original textbox did not regain focus after dictation stopped.");
                if (!session.FocusPaused) Trace(session, "inline.focus_paused");
                session.FocusPaused = true;
                SetPresentation(false, "Dictation is held. Return to the original textbox to insert it.");
                return;
            }
            if (session.FocusPaused) Trace(session, "inline.focus_returned");
            session.FocusPaused = false;
            string desired;
            bool finishing;
            lock (session) { desired = session.Desired; finishing = session.Finishing; }
            SetPresentation(!session.Fallback, null);
            if (!_canPaste()) return;
            if (session.Fallback)
            {
                if (!finishing) return;
                Paste(session, desired);
                Trace(session, "inline.fallback_completed");
                session.Completion.TrySetResult();
                session.Active = false;
                return;
            }
            if (desired.Length == 0) return;
            session.InFlight ??= desired;
            var result = target.TryReplace(session.Acknowledged, session.InFlight, text => Paste(session, text));
            Trace(session, "inline.replace_result", new { result = result.ToString(), acknowledgedLength = session.Acknowledged.Length, requestedLength = session.InFlight.Length, finishing });
            switch (result)
            {
                case TextTargetUpdateResult.Success:
                    session.Acknowledged = session.InFlight;
                    session.InFlight = null;
                    if (finishing && session.Acknowledged == desired)
                    {
                        Trace(session, "inline.final_completed");
                        session.Completion.TrySetResult();
                        session.Active = false;
                    }
                    break;
                case TextTargetUpdateResult.Pending:
                    break;
                case TextTargetUpdateResult.Unfocused:
                    SetPresentation(false, "Dictation is held. Return to the original textbox to insert it.");
                    break;
                case TextTargetUpdateResult.Unsupported when !target.HasAttemptedWrite && target.CanPasteFallback:
                    session.Fallback = true;
                    session.InFlight = null;
                    SetPresentation(false, null);
                    break;
                default:
                    Trace(session, "inline.conflict", new { reason = result.ToString() });
                    session.Conflicted = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace(session, "inline.update_failed", error: ex);
            // An ambiguous write can never become a whole-transcript fallback.
            session.Conflicted = true;
            session.Failure = ex.Message;
            if (session.Finishing) session.Completion.TrySetException(ex);
        }
    }

    private void Paste(Session session, string text)
    {
        void Write()
        {
            using var traceScope = DiagnosticTrace.EnterRecording(session.RecordingId ?? string.Empty);
            Trace(session, "inline.clipboard_dispatch", new { text });
            _paste(text, () =>
            {
                var prepared = session.Target!.IsPreparedSelectionCurrent;
                var canPaste = prepared && _canPaste();
                var current = canPaste && IsCurrent(session);
                if (!current) Trace(session, "inline.prepared_selection_rejected", new { prepared, canPaste, current });
                return current;
            });
            Trace(session, "inline.clipboard_returned");
        }
        if (_clipboard is null) Write();
        else _clipboard.InvokeAsync(Write).GetAwaiter().GetResult();
    }

    private bool IsCurrent(Session session) => session.Active && ReferenceEquals(Volatile.Read(ref _session), session);

    private void Dispatch(Action action)
    {
        if (_automation is null) action();
        else _ = _automation.InvokeAsync(action);
    }

    public void EndSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            Trace(session, "inline.session_ended", new { session.Finishing, session.Conflicted, acknowledgedLength = session.Acknowledged.Length });
            session.Active = false;
            session.Completion.TrySetCanceled();
        }
        _timer?.Stop();
    }

    private void SetPresentation(bool inline, string? reason)
    {
        if (InlinePreview == inline && HoldingReason == reason) return;
        InlinePreview = inline;
        HoldingReason = reason;
        PresentationChanged?.Invoke();
    }

    public void Dispose()
    {
        EndSession();
        _timer?.Dispose();
        _automation?.Dispose();
        _clipboard?.Dispose();
        _focusGuard?.Dispose();
    }

    private sealed class Session
    {
        public string? RecordingId;
        public bool FocusPaused;
        public IWindowsTextTarget? Target;
        public string Desired = "";
        public string Acknowledged = "";
        public string? InFlight;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Active = true;
        public volatile bool Ready;
        public volatile bool Finishing;
        public long FinishingSince;
        public bool Conflicted;
        public bool Fallback;
        public string? Failure;
        public long BusySince;
    }

    private static void Trace(Session session, string stage, object? data = null, Exception? error = null)
        => DiagnosticTrace.Write(stage, data, error, session.RecordingId);
}

internal sealed class ApartmentWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public ApartmentWorker(ApartmentState apartment, string name)
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(apartment);
        thread.Start();
    }

    public Task InvokeAsync(Action action)
    {
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { action(); completion.TrySetResult(); }
                catch (Exception ex)
                {
                    DiagnosticTrace.Write("inline.worker_failed", error: ex, recordingId: recordingId);
                    completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticTrace.Write("inline.worker_queue_closed", error: ex, recordingId: recordingId);
            completion.TrySetCanceled();
        }
        return completion.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
