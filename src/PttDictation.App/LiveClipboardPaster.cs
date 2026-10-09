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
    private readonly Func<long> _tickCount;
    private readonly WindowsFocusCaptureGuard? _focusGuard;
    private readonly WindowsAccessibilityWarmup? _accessibilityWarmup;
    private readonly Func<CancellationToken, Task<IDisposable>>? _acquireCapture;
    private readonly Func<Func<bool>> _beginPreparation;
    private readonly Func<Func<bool>> _createReadiness;
    private CapturePreparation? _preparation;
    private readonly Action<string, Func<bool>> _paste;
    private readonly Func<string, Func<bool>, IPendingClipboardPaste>? _pasteAwaitingAcknowledgement;
    private readonly Func<bool> _canPaste;
    private readonly System.Windows.Forms.Timer? _timer;
    private readonly ApartmentWorker? _automation;
    private readonly ApartmentWorker? _clipboard;
    private Session? _session;
    private int _queued;

    public LiveClipboardPaster(Func<IntPtr>? statusWindow = null)
        : this(new ClipboardPaster(), statusWindow)
    { }

    private LiveClipboardPaster(ClipboardPaster clipboard, Func<IntPtr>? statusWindow)
        : this(WindowsTextTarget.Capture, clipboard.PasteToCurrentTarget,
            startTimer: true, canPaste: () => WindowsPasteInput.CanPasteNow, statusWindow: statusWindow,
            pasteAwaitingAcknowledgement: clipboard.PasteToCurrentTargetAwaitingAcknowledgement)
    { }

    internal LiveClipboardPaster(Func<IWindowsTextTarget> capture, Action<string, Func<bool>> paste,
        bool startTimer = false, Func<bool>? canPaste = null, Func<Func<bool>>? beginCaptureGuard = null,
        Action<Action>? confirmCapture = null, Func<long>? tickCount = null,
        Func<CancellationToken, Task<IDisposable>>? acquireCapture = null,
        Func<Func<bool>>? beginPreparation = null, Func<Func<bool>>? createReadiness = null,
        ApartmentWorker? automationWorker = null, Func<IntPtr>? statusWindow = null,
        Func<string, Func<bool>, IPendingClipboardPaste>? pasteAwaitingAcknowledgement = null)
    {
        _prepareCapture = () => capture;
        _beginCaptureGuard = beginCaptureGuard ?? (() => () => true);
        _confirmCapture = confirmCapture ?? (action => action());
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _paste = paste;
        _pasteAwaitingAcknowledgement = pasteAwaitingAcknowledgement;
        _canPaste = canPaste ?? (() => true);
        _acquireCapture = acquireCapture;
        _beginPreparation = beginPreparation ?? (() => () => true);
        _createReadiness = createReadiness ?? (() => () => true);
        _automation = automationWorker;
        if (startTimer)
        {
            var focusGuard = new WindowsFocusCaptureGuard(statusWindow);
            _focusGuard = focusGuard;
            _accessibilityWarmup = new WindowsAccessibilityWarmup(() => Volatile.Read(ref _session) is null);
            _acquireCapture = _accessibilityWarmup.AcquireCaptureAsync;
            _beginPreparation = () => focusGuard.BeginPreparation(() => GlobalHotkeySource.InputGeneration);
            _createReadiness = BrowserCaptureReadiness.Create;
            focusGuard.FocusChanged += _accessibilityWarmup.Request;
            _beginCaptureGuard = focusGuard.BeginCapture;
            // Resolve only the original focused identity before returning from
            // the hotkey. All document/provider reads then happen on the worker.
            _prepareCapture = () => WindowsTextTarget.CaptureFocusedIdentity(() => focusGuard.Generation);
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _confirmCapture = confirmCapture ?? (action => ui.Post(_ => action(), null));
            _automation = new ApartmentWorker(ApartmentState.MTA, "Dictation text target");
            _clipboard = new ApartmentWorker(ApartmentState.STA, "Dictation live paste");
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (_, _) => Pump();
        }
    }

    public bool InlinePreview { get; private set; }
    public string? HoldingReason { get; private set; }
    public event Action? PresentationChanged;
    public event Action<Exception>? InsertionFailed;
    public LiveInsertionFailureDelivery? FailureDelivery => Volatile.Read(ref _session)?.FailureDelivery;

    public async Task PrepareCaptureAsync(CancellationToken cancellationToken)
    {
        if (_acquireCapture is null) return;
        EndSession();
        var preparation = new CapturePreparation(cancellationToken, _beginPreparation(), lease =>
        {
            // A timed-out native call may still be running. Release only after
            // the serial automation worker has finished it, never from the UI.
            if (_automation is null) lease.Dispose();
            else _ = _automation.InvokeAsync(lease.Dispose);
        });
        _preparation = preparation;
        void Check()
        {
            preparation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Volatile.Read(ref _preparation), preparation) || !preparation.Unchanged())
                throw new InvalidOperationException("The textbox or input changed while dictation was preparing.");
        }
        async Task OnWorker(Action action)
        {
            if (_automation is null) action();
            else await _automation.InvokeAsync(action).WaitAsync(preparation.Token);
        }
        try
        {
            preparation.SetLease(await _acquireCapture(preparation.Token));
            Check();
            Func<bool>? ready = null;
            await OnWorker(() => { Check(); ready = _createReadiness(); });
            while (true)
            {
                var available = false;
                await OnWorker(() => { Check(); available = ready!(); Check(); });
                Check();
                if (available) break;
                await Task.Delay(50, preparation.Token);
            }
            preparation.ValidateIdentity = ready;
            DiagnosticTrace.Write("inline.preparation_ready");
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _preparation, null, preparation);
            preparation.Dispose();
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("The textbox did not become ready for dictation. No recording was started.", error);
            throw;
        }
    }

    public void CaptureTarget()
    {
        var preparation = Interlocked.Exchange(ref _preparation, null);
        EndSession();
        if (preparation is not null && (preparation.Token.IsCancellationRequested || !preparation.Unchanged()))
        {
            preparation.Dispose();
            throw new InvalidOperationException("The textbox or input changed before dictation could start.");
        }
        var session = new Session { BusySince = _tickCount(), RecordingId = DiagnosticTrace.CurrentRecordingId,
            Preparation = preparation };
        Trace(session, "inline.capture_started");
        Volatile.Write(ref _session, session);
        SetPresentation(false, null);
        Func<bool> unchanged;
        Func<IWindowsTextTarget> capture;
        try { unchanged = _beginCaptureGuard(); capture = _prepareCapture(); }
        catch (Exception ex)
        {
            preparation?.Dispose();
            Trace(session, "inline.identity_capture_failed", error: ex);
            session.Ready = true;
            Interlocked.Exchange(ref session.BusySince, 0);
            FailInsertion(session, ex, initialCaptureRejected: true);
            return;
        }
        Dispatch(() =>
        {
            using var traceScope = DiagnosticTrace.EnterRecording(session.RecordingId ?? string.Empty);
            if (!IsCurrent(session)) return;
            try
            {
                if (preparation?.ValidateIdentity is { } validate && !validate())
                    throw new InvalidOperationException("The prepared textbox is no longer available.");
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
                    if (!unchanged() || preparation is not null
                        && (preparation.Token.IsCancellationRequested || !preparation.Unchanged()))
                    {
                        Trace(session, "inline.capture_rejected", new { reason = "focus_or_selection_changed" });
                        session.Conflicted = true;
                        session.Failure = "The textbox or selection changed while dictation was starting.";
                    }
                    preparation?.Dispose();
                    session.Ready = true;
                    Trace(session, "inline.capture_ready", new { session.Conflicted, session.Fallback });
                    Interlocked.Exchange(ref session.BusySince, 0);
                    if (session.Conflicted)
                    {
                        FailInsertion(session, new InvalidOperationException(session.Failure
                            ?? "The textbox could not be verified for dictation."), initialCaptureRejected: true);
                        return;
                    }
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
            session.FinishingSince = _tickCount();
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
        // returns later. Notify the workflow so capture cannot remain running.
        if (Interlocked.Read(ref session.BusySince) is var started && started != 0
            && _tickCount() - started > 5000)
        {
            Trace(session, "inline.watchdog_timeout", new { elapsedMilliseconds = _tickCount() - started });
            FailInsertion(session, new InvalidOperationException("The original editor stopped responding."),
                initialCaptureRejected: !session.Ready);
            return;
        }
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Interlocked.Exchange(ref session.BusySince, _tickCount());
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
                FailInsertion(session, new InvalidOperationException(session.Failure
                    ?? "The original textbox could no longer be verified for text replacement."));
                return;
            }
            var target = session.Target!;
            var awaitingWrite = target.IsAwaitingWriteAcknowledgement;
            if (!awaitingWrite && !target.IsFocused)
            {
                if (session.Finishing && _tickCount() - session.FinishingSince >= FinalFocusWaitMilliseconds)
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
            if (!awaitingWrite && !_canPaste()) return;
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
            lock (session) session.InFlight ??= desired;
            var result = target.TryReplace(session.Acknowledged, session.InFlight, text => Paste(session, text));
            if (!IsCurrent(session)) return;
            Trace(session, "inline.replace_result", new { result = result.ToString(), acknowledgedLength = session.Acknowledged.Length, requestedLength = session.InFlight.Length, finishing });
            switch (result)
            {
                case TextTargetUpdateResult.Success:
                    Interlocked.Exchange(ref session.ClipboardRestore, null)?.Acknowledge();
                    lock (session)
                    {
                        session.Acknowledged = session.InFlight;
                        session.InFlight = null;
                    }
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
                    FailInsertion(session, new InvalidOperationException(
                        "The original textbox could no longer be verified for text replacement."));
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace(session, "inline.update_failed", error: ex);
            // An ambiguous write can never become a whole-transcript fallback.
            FailInsertion(session, ex);
        }
    }

    private void FailInsertion(Session session, Exception error, bool initialCaptureRejected = false)
    {
        var failureSubscribers = InsertionFailed;
        if (!IsCurrent(session) || Interlocked.Exchange(ref session.FailureNotified, 1) != 0) return;
        lock (session)
        {
            session.FailureDelivery = new(session.Acknowledged,
                error is TextTargetUnavailableException && !session.Conflicted && session.InFlight is null,
                initialCaptureRejected);
        }
        session.Conflicted = true;
        session.Failure = error.Message;
        session.Active = false;
        AbandonClipboard(session);
        session.Completion.TrySetException(error);
        // Preview failures may never have a PasteAsync waiter. Observe the task
        // while preserving the same exception for any existing/future waiter.
        _ = session.Completion.Task.Exception;
        Trace(session, "inline.insertion_failed", error: error);
        try { SetPresentation(false, "Text insertion stopped. Completing your transcript for Session History."); }
        catch (Exception presentationError) { Trace(session, "inline.failure_presentation_failed", error: presentationError); }
        if (failureSubscribers is null) return;
        foreach (Action<Exception> subscriber in failureSubscribers.GetInvocationList())
        {
            try { subscriber(error); }
            catch (Exception subscriberError) { Trace(session, "inline.failure_subscriber_failed", error: subscriberError); }
        }
    }

    private void Paste(Session session, string text)
    {
        void Write()
        {
            using var traceScope = DiagnosticTrace.EnterRecording(session.RecordingId ?? string.Empty);
            Trace(session, "inline.clipboard_dispatch", new { text });
            bool CanWrite()
            {
                var prepared = session.Target!.IsPreparedSelectionCurrent;
                var canPaste = prepared && _canPaste();
                var current = canPaste && IsCurrent(session);
                if (!current) Trace(session, "inline.prepared_selection_rejected", new { prepared, canPaste, current });
                return current;
            }
            if (!session.Fallback && _pasteAwaitingAcknowledgement is not null)
            {
                var lease = _pasteAwaitingAcknowledgement(text, CanWrite);
                Interlocked.Exchange(ref session.ClipboardRestore, lease)?.Dispose();
                // Cancellation can retire the session while native SendInput or
                // clipboard validation is returning. Do not strand a late lease.
                if (!IsCurrent(session)) AbandonClipboard(session);
            }
            else _paste(text, CanWrite);
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
        Interlocked.Exchange(ref _preparation, null)?.Dispose();
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            Trace(session, "inline.session_ended", new { session.Finishing, session.Conflicted, acknowledgedLength = session.Acknowledged.Length });
            session.Active = false;
            AbandonClipboard(session);
            session.Preparation?.Dispose();
            session.Completion.TrySetCanceled();
        }
        _timer?.Stop();
        SetPresentation(false, null);
    }

    private void SetPresentation(bool inline, string? reason)
    {
        if (InlinePreview == inline && HoldingReason == reason) return;
        InlinePreview = inline;
        HoldingReason = reason;
        if (PresentationChanged is not { } subscribers) return;
        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(); }
            catch (Exception error) { DiagnosticTrace.Write("inline.presentation_subscriber_failed", error: error); }
        }
    }

    public void Dispose()
    {
        if (_accessibilityWarmup is not null)
        {
            if (_focusGuard is not null) _focusGuard.FocusChanged -= _accessibilityWarmup.Request;
            _accessibilityWarmup.Dispose();
        }
        EndSession();
        _timer?.Dispose();
        _automation?.Dispose();
        _clipboard?.Dispose();
        _focusGuard?.Dispose();
    }

    private sealed class Session
    {
        public IPendingClipboardPaste? ClipboardRestore;
        public CapturePreparation? Preparation;
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
        public int FailureNotified;
        public volatile LiveInsertionFailureDelivery? FailureDelivery;
    }

    private static void Trace(Session session, string stage, object? data = null, Exception? error = null)
        => DiagnosticTrace.Write(stage, data, error, session.RecordingId);

    private static void AbandonClipboard(Session session)
        => Interlocked.Exchange(ref session.ClipboardRestore, null)?.Dispose();
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
