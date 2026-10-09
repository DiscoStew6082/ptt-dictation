using System.Collections.Concurrent;
using PttDictation.Core;

namespace PttDictation.App;

// Each recording has its own output state. UIA runs on a serial MTA worker;
// clipboard operations run on an STA worker. Neither blocks the hotkey/UI loop.
internal sealed class LiveClipboardPaster : ILiveClipboardPaster, IDisposable
{
    private const long FinalFocusWaitMilliseconds = 5000;
    internal const long InitialCaptureTimeoutMilliseconds = 5000;
    internal const long SlowOperationMilliseconds = 5000;
    internal const long ReadyOperationTimeoutMilliseconds = WindowsTextTarget.ResponseTimeoutMilliseconds;
    internal const string SlowProviderReason = "Waiting for textbox to respond.";
    private readonly Func<Func<IWindowsTextTarget>> _prepareCapture;
    private readonly Func<Func<bool>> _beginCaptureGuard;
    private readonly Action<Action> _confirmCapture;
    private readonly Func<long> _tickCount;
    private readonly WindowsFocusCaptureGuard? _focusGuard;
    private readonly WindowsAccessibilityWarmup? _accessibilityWarmup;
    private readonly Func<CancellationToken, Task<IDisposable>>? _acquireCapture;
    private readonly Func<Func<bool>> _beginPreparation;
    private readonly Func<TextTargetReadiness> _createReadiness;
    private CapturePreparation? _preparation;
    private readonly Action<string, Func<bool>> _paste;
    private readonly Func<string, Func<bool>, IPendingClipboardPaste>? _pasteAwaitingAcknowledgement;
    private readonly Func<bool> _canPaste;
    private readonly System.Windows.Forms.Timer? _timer;
    private readonly ApartmentWorker? _automation;
    private readonly ApartmentWorker? _clipboard;
    private Session? _session;
    private readonly object _presentationSync = new();
    private int _queued;

    public LiveClipboardPaster(Func<IntPtr>? statusWindow = null)
        : this(CreateNativeClipboard(), statusWindow)
    { }

    private LiveClipboardPaster(NativeClipboard clipboard, Func<IntPtr>? statusWindow)
        : this(WindowsTextTarget.Capture, clipboard.Paster.PasteToCurrentTarget,
            startTimer: true, canPaste: () => WindowsPasteInput.CanPasteNow, statusWindow: statusWindow,
            pasteAwaitingAcknowledgement: clipboard.Paster.PasteToCurrentTargetAwaitingAcknowledgement,
            clipboardWorker: clipboard.Worker)
    { }

    private sealed record NativeClipboard(ClipboardPaster Paster, ApartmentWorker Worker);

    private static NativeClipboard CreateNativeClipboard()
    {
        var worker = new ApartmentWorker(ApartmentState.STA, "Dictation clipboard");
        return new(new ClipboardPaster(worker), worker);
    }

    internal LiveClipboardPaster(Func<IWindowsTextTarget> capture, Action<string, Func<bool>> paste,
        bool startTimer = false, Func<bool>? canPaste = null, Func<Func<bool>>? beginCaptureGuard = null,
        Action<Action>? confirmCapture = null, Func<long>? tickCount = null,
        Func<CancellationToken, Task<IDisposable>>? acquireCapture = null,
        Func<Func<bool>>? beginPreparation = null, Func<Func<bool>>? createReadiness = null,
        ApartmentWorker? automationWorker = null, Func<IntPtr>? statusWindow = null,
        Func<string, Func<bool>, IPendingClipboardPaste>? pasteAwaitingAcknowledgement = null,
        ApartmentWorker? clipboardWorker = null, Func<Func<IWindowsTextTarget>>? prepareCapture = null,
        Func<TextTargetReadiness>? createPreparedReadiness = null)
    {
        _prepareCapture = prepareCapture ?? (() => capture);
        _beginCaptureGuard = beginCaptureGuard ?? (() => () => true);
        _confirmCapture = confirmCapture ?? (action => action());
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _paste = paste;
        _pasteAwaitingAcknowledgement = pasteAwaitingAcknowledgement;
        _canPaste = canPaste ?? (() => true);
        _acquireCapture = acquireCapture;
        _beginPreparation = beginPreparation ?? (() => () => true);
        _createReadiness = createPreparedReadiness ?? (() => new((createReadiness ?? (() => () => true))()));
        _automation = automationWorker;
        _clipboard = clipboardWorker;
        if (startTimer)
        {
            var focusGuard = new WindowsFocusCaptureGuard(statusWindow);
            _focusGuard = focusGuard;
            _accessibilityWarmup = new WindowsAccessibilityWarmup(() => Volatile.Read(ref _session) is null);
            _acquireCapture = _accessibilityWarmup.AcquireCaptureAsync;
            _beginPreparation = () => focusGuard.BeginPreparation(() => GlobalHotkeySource.InputGeneration);
            _createReadiness = () => BrowserCaptureReadiness.Create(() => focusGuard.Generation);
            focusGuard.FocusChanged += _accessibilityWarmup.Request;
            _beginCaptureGuard = focusGuard.BeginCapture;
            // Native intent is captured on the UI thread. Identity and every
            // provider read stay on the same serial automation MTA.
            _prepareCapture = () => WindowsTextTarget.CaptureFocusedIdentity(() => focusGuard.Generation);
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _confirmCapture = confirmCapture ?? (action => ui.Post(_ => action(), null));
            _automation = new ApartmentWorker(ApartmentState.MTA, "Dictation text target");
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (_, _) => Pump();
        }
    }

    public bool InlinePreview { get; private set; }
    public string? HoldingReason { get; private set; }
    public bool WaitingForTargetResponse => HoldingReason == SlowProviderReason;
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
            TextTargetReadiness? ready = null;
            await OnWorker(() => { Check(); ready = _createReadiness(); });
            while (true)
            {
                var available = false;
                await OnWorker(() => { Check(); available = ready!.Probe(); Check(); });
                Check();
                if (available) break;
                await Task.Delay(50, preparation.Token);
            }
            preparation.ValidateIdentity = ready!.Probe;
            if (ready.Target is { } target)
            {
                // Readiness may legitimately refine a cold browser container.
                // After that, a strict native boundary surrounds final validation
                // of the SAME editor and snapshot, all before microphone startup.
                var unchanged = _beginCaptureGuard();
                await OnWorker(() =>
                {
                    Check();
                    if (!ready.Probe() || !target.IsPreparedSelectionCurrent)
                        throw new InvalidOperationException("The prepared textbox or selection changed before dictation could start.");
                    Check();
                });
                var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _confirmCapture(() =>
                {
                    try
                    {
                        Check();
                        if (!unchanged()) throw new InvalidOperationException("The textbox or selection changed while dictation was preparing.");
                        preparation.PreparedTarget = target;
                        preparation.ConfirmedIntent = unchanged;
                        confirmed.TrySetResult();
                    }
                    catch (Exception error) { confirmed.TrySetException(error); }
                });
                await confirmed.Task.WaitAsync(preparation.Token);
            }
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
        if (preparation is not null && (preparation.Token.IsCancellationRequested || !preparation.Unchanged()
            || preparation.ConfirmedIntent is { } confirmedIntent && !confirmedIntent()))
        {
            preparation.Dispose();
            throw new InvalidOperationException("The textbox or input changed before dictation could start.");
        }
        var session = new Session { BusySince = _tickCount(), RecordingId = DiagnosticTrace.CurrentRecordingId,
            Preparation = preparation };
        Trace(session, "inline.capture_started");
        Volatile.Write(ref _session, session);
        SetPresentation(false, null);
        if (preparation?.PreparedTarget is { } preparedTarget)
        {
            session.Target = preparedTarget;
            session.Fallback = !preparedTarget.SupportsReplacement;
            session.Ready = true;
            Interlocked.Exchange(ref session.BusySince, 0);
            Trace(session, "inline.prepared_target_adopted", new { session.Fallback });
            preparation.Dispose();
            SetPresentation(!session.Fallback, null);
            _timer?.Start();
            return;
        }
        Func<bool> unchanged;
        try { unchanged = _beginCaptureGuard(); }
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
                var capture = _prepareCapture();
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
        lock (session)
        {
            // A verified editor may briefly stall inside native provider work.
            // Keep one operation in flight and surface the wait, but revoke all
            // write permission at the hard ceiling. Initial capture stays strict.
            if (!CheckOperationDeadline(session)) return;
            if (session.BusySince is var started && started != 0)
            {
                var elapsed = _tickCount() - started;
                if (session.Ready && elapsed >= SlowOperationMilliseconds && !session.SlowOperationNotified)
                {
                    session.SlowOperationNotified = true;
                    Trace(session, "inline.provider_waiting", new { elapsedMilliseconds = elapsed });
                    SetSessionPresentation(session, false, SlowProviderReason);
                }
            }
        }
        if (!IsCurrent(session)) return;
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Interlocked.Exchange(ref session.BusySince, _tickCount());
        Dispatch(() =>
        {
            try { if (IsCurrent(session)) PumpSession(session); }
            finally
            {
                lock (session)
                {
                    Interlocked.Exchange(ref session.BusySince, 0);
                    ClearProviderWait(session);
                }
                Interlocked.Exchange(ref _queued, 0);
            }
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
                if (!CheckOperationDeadline(session)) return;
                Trace(session, "inline.fallback_completed");
                ClearProviderWait(session);
                session.Completion.TrySetResult();
                session.Active = false;
                return;
            }
            if (desired.Length == 0) return;
            lock (session) session.InFlight ??= desired;
            var result = target.TryReplace(session.Acknowledged, session.InFlight, text => Paste(session, text));
            if (!CheckOperationDeadline(session)) return;
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
                        ClearProviderWait(session);
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
                var current = canPaste && CheckOperationDeadline(session);
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
        // ClipboardPaster dispatches only clipboard APIs to its STA backend.
        // Its target callback must execute here, on the automation MTA.
        Write();
    }

    private bool IsCurrent(Session session) => session.Active && ReferenceEquals(Volatile.Read(ref _session), session);

    private bool CheckOperationDeadline(Session session)
    {
        lock (session)
        {
            if (!IsCurrent(session)) return false;
            var started = Interlocked.Read(ref session.BusySince);
            var elapsed = _tickCount() - started;
            if (started == 0 || !(session.Ready ? elapsed >= ReadyOperationTimeoutMilliseconds
                : elapsed > InitialCaptureTimeoutMilliseconds)) return true;
            Trace(session, "inline.watchdog_timeout", new { elapsedMilliseconds = elapsed });
            TextProviderDiagnostics.WritePendingOperations(session.RecordingId);
            FailInsertion(session, new InvalidOperationException("The original editor stopped responding."),
                initialCaptureRejected: !session.Ready);
            return false;
        }
    }

    private void ClearProviderWait(Session session)
    {
        lock (session)
        {
            if (!session.SlowOperationNotified || !IsCurrent(session)) return;
            session.SlowOperationNotified = false;
            Trace(session, "inline.provider_resumed");
            if (WaitingForTargetResponse) SetSessionPresentation(session, !session.Fallback && !session.FocusPaused, null);
        }
    }

    private void Dispatch(Action action)
    {
        if (_automation is null) action();
        else _ = _automation.InvokeAsync(action);
    }

    public void EndSession()
    {
        Interlocked.Exchange(ref _preparation, null)?.Dispose();
        Session? session;
        lock (_presentationSync)
        {
            session = Interlocked.Exchange(ref _session, null);
            if (session is not null) session.Active = false;
            _timer?.Stop();
            SetPresentation(false, null);
        }
        if (session is not null)
        {
            Trace(session, "inline.session_ended", new { session.Finishing, session.Conflicted, acknowledgedLength = session.Acknowledged.Length });
            AbandonClipboard(session);
            session.Preparation?.Dispose();
            session.Completion.TrySetCanceled();
        }
    }

    private void SetSessionPresentation(Session session, bool inline, string? reason)
    {
        // Retirement and publication share this small lock. A late worker must
        // never clear a newer recording's status or restore its inline flag.
        lock (_presentationSync)
        {
            if (IsCurrent(session)) SetPresentation(inline, reason);
        }
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
        public bool SlowOperationNotified;
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
    private Action? _wakeSta;
    private Exception? _startupFailure;

    public ApartmentWorker(ApartmentState apartment, string name)
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (apartment == ApartmentState.STA)
                {
                    using var context = new ApplicationContext();
                    using var ui = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(ui);
                    var draining = false;
                    void Drain()
                    {
                        // OLE can pump messages during a queued action. Nested
                        // wake messages must not run the next action early.
                        if (draining) return;
                        draining = true;
                        try
                        {
                            while (_queue.TryTake(out var work)) work();
                            if (_queue.IsCompleted) context.ExitThread();
                        }
                        finally { draining = false; }
                    }
                    Volatile.Write(ref _wakeSta, () => ui.Post(_ => Drain(), null));
                    ui.Post(_ => Drain(), null);
                    Application.Run(context);
                }
                else foreach (var work in _queue.GetConsumingEnumerable()) work();
            }
            catch (Exception error)
            {
                _startupFailure = error;
                _queue.CompleteAdding();
                while (_queue.TryTake(out var work)) work();
            }
            finally { Volatile.Write(ref _wakeSta, null); }
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
                try
                {
                    if (_startupFailure is not null) throw new InvalidOperationException("Apartment worker failed to initialize.", _startupFailure);
                    action(); completion.TrySetResult();
                }
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
        // An accepted action keeps its completion even if its wake races STA
        // shutdown: the draining loop may already have executed it.
        WakeSta();
        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        WakeSta();
    }

    private void WakeSta()
    {
        try { Volatile.Read(ref _wakeSta)?.Invoke(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.InvalidAsynchronousStateException) { }
    }
}
