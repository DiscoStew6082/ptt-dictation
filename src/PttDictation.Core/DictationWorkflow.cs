namespace PttDictation.Core;

public sealed class DictationWorkflow
{
    private readonly IDictationSessionFactory _sessionFactory;
    private readonly IClipboardPaster _clipboardPaster;
    private readonly SessionHistory _history;
    private readonly Func<IReadOnlyList<TranscriptCorrection>> _getTranscriptCorrections;
    private readonly object _gate = new();
    private IDictationSession? _session;
    private CancellationTokenSource? _operation;
    private DictationTriggerMode? _activeTriggerMode;
    private DictationWorkflowState _state = DictationWorkflowState.Idle;
    private bool _starting;
    private string _rawPreview = string.Empty;
    private string _lastPreview = string.Empty;
    private string? _recordingId;

    public DictationWorkflow(
        IAudioRecorder recorder,
        ITranscriber transcriber,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
        : this(
            new BatchDictationSessionFactory(recorder, transcriber),
            clipboardPaster,
            history,
            getTranscriptCorrections)
    {
    }

    public DictationWorkflow(
        IDictationSessionFactory sessionFactory,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
    {
        _sessionFactory = sessionFactory;
        _clipboardPaster = clipboardPaster;
        _history = history;
        _getTranscriptCorrections = getTranscriptCorrections ?? (() => []);
    }

    public event Action<DictationWorkflowState>? StateChanged;

    public DictationWorkflowState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Task HandleAsync(DictationIntent intent, CancellationToken cancellationToken)
    {
        return intent switch
        {
            DictationIntent.BeginHold => BeginAsync(DictationTriggerMode.Hold, cancellationToken),
            DictationIntent.EndHold => FinishAsync(DictationTriggerMode.Hold),
            DictationIntent.Toggle => ToggleAsync(cancellationToken),
            DictationIntent.Cancel => CancelAsync(),
            _ => Task.CompletedTask
        };
    }

    public void ReportProcessingDetail(string detail)
    {
        DictationWorkflowState? next = null;
        lock (_gate)
        {
            if (_state.Phase == DictationWorkflowPhase.Processing)
            {
                next = _state with { ProcessingDetail = detail };
            }
        }

        if (next is not null)
        {
            Publish(next);
        }
    }

    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        DictationWorkflowPhase phase;
        DictationTriggerMode? mode;
        lock (_gate)
        {
            phase = _state.Phase;
            mode = _activeTriggerMode;
        }

        if (phase == DictationWorkflowPhase.Recording && mode == DictationTriggerMode.Toggle)
        {
            await FinishAsync(DictationTriggerMode.Toggle);
            return;
        }

        if (!IsActive(phase))
        {
            await BeginAsync(DictationTriggerMode.Toggle, cancellationToken);
        }
    }

    private async Task BeginAsync(DictationTriggerMode mode, CancellationToken cancellationToken)
    {
        IDictationSession session;
        CancellationTokenSource operation;
        lock (_gate)
        {
            if (_starting || _session is not null || IsActive(_state.Phase))
            {
                return;
            }

            _starting = true;
            _recordingId = DiagnosticTrace.BeginRecording(mode.ToString());
            _rawPreview = string.Empty;
            _lastPreview = string.Empty;
            _activeTriggerMode = mode;
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operation = operation;
            session = _sessionFactory.CreateSession();
            _session = session;
            session.TranscriptUpdated += OnTranscriptUpdated;
        }

        try
        {
            using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
            Trace("workflow.capture_target");
            _clipboardPaster.CaptureTarget();
            await session.StartAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Recording, mode));
        }
        catch (OperationCanceledException ex) when (operation.IsCancellationRequested)
        {
            Trace("workflow.start_cancelled", error: ex);
            await TryCancelSessionAsync(session);
            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Cancelled,
                CleanupWarningPath: session.CleanupWarningPath));
            CompleteSession(session, operation);
        }
        catch (Exception ex)
        {
            Trace("workflow.start_failed", error: ex);
            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Failed,
                ErrorMessage: ex.Message,
                CleanupWarningPath: session.CleanupWarningPath));
            CompleteSession(session, operation);
        }
        finally
        {
            lock (_gate)
            {
                _starting = false;
            }
        }
    }

    private async Task FinishAsync(DictationTriggerMode mode)
    {
        IDictationSession? session;
        CancellationTokenSource? operation;
        DictationWorkflowState processing;
        lock (_gate)
        {
            if (_state.Phase != DictationWorkflowPhase.Recording || _activeTriggerMode != mode)
            {
                return;
            }

            session = _session;
            operation = _operation;
            processing = new DictationWorkflowState(
                DictationWorkflowPhase.Processing,
                mode,
                _state.Transcript);
        }

        if (session is null || operation is null)
        {
            return;
        }

        Publish(processing);
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        Trace("workflow.finish_started");
        string? sessionResultCleanupWarningPath = null;
        TranscriptComparison? comparison = null;
        try
        {
            var sessionResult = await session.StopAsync(operation.Token);
            sessionResultCleanupWarningPath = sessionResult.CleanupWarningPath;
            operation.Token.ThrowIfCancellationRequested();
            var cleanupWarningPath = sessionResultCleanupWarningPath ?? session.CleanupWarningPath;
            var corrected = TranscriptCorrectionDictionary.Apply(
                sessionResult.Transcript.Text,
                _getTranscriptCorrections());
            var cleaned = TranscriptNormalizer.Normalize(corrected);
            Trace("workflow.final_text_stages", new { raw = sessionResult.Transcript.Text, dictionaryCorrected = corrected, normalized = cleaned });
            comparison = new TranscriptComparison(
                _rawPreview, _lastPreview, sessionResult.Transcript.Text, corrected, cleaned);
            if (cleaned.Length == 0)
            {
                if (_clipboardPaster is ILiveClipboardPaster && !string.IsNullOrWhiteSpace(_lastPreview))
                {
                    _history.Add(_lastPreview, comparison);
                    Publish(new DictationWorkflowState(
                        DictationWorkflowPhase.Failed,
                        Transcript: _lastPreview,
                        ErrorMessage: "Final recognition returned no text. Your preview is preserved in Session History.",
                        CleanupWarningPath: cleanupWarningPath));
                    return;
                }
                Publish(new DictationWorkflowState(
                    DictationWorkflowPhase.Empty,
                    CleanupWarningPath: cleanupWarningPath));
                return;
            }

            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Processing,
                mode,
                cleaned,
                CurrentState.ProcessingDetail,
                CleanupWarningPath: cleanupWarningPath));
            Trace("workflow.paste_started", new { text = cleaned });
            await _clipboardPaster.PasteAsync(cleaned, operation.Token);
            Trace("workflow.paste_completed");
            operation.Token.ThrowIfCancellationRequested();
            _history.Add(cleaned, comparison);
            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Pasted,
                Transcript: cleaned,
                CleanupWarningPath: cleanupWarningPath));
        }
        catch (OperationCanceledException ex) when (operation.IsCancellationRequested)
        {
            Trace("workflow.finish_cancelled", error: ex);
            PublishCancellationIfNeeded(session, sessionResultCleanupWarningPath);
        }
        catch (Exception ex) when (operation.IsCancellationRequested)
        {
            Trace("workflow.finish_failure_during_cancellation", error: ex);
            PublishCancellationIfNeeded(session, sessionResultCleanupWarningPath);
        }
        catch (Exception ex)
        {
            Trace("workflow.finish_failed", error: ex);
            var retained = comparison?.FinalText;
            if (_clipboardPaster is ILiveClipboardPaster)
            {
                retained = string.IsNullOrWhiteSpace(retained) ? _lastPreview : retained;
                _history.Add(retained, comparison);
            }
            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Failed,
                Transcript: retained ?? string.Empty,
                ErrorMessage: ex.Message + (_clipboardPaster is ILiveClipboardPaster && !string.IsNullOrWhiteSpace(retained)
                    ? " Your transcript is available in Session History." : string.Empty),
                CleanupWarningPath: sessionResultCleanupWarningPath ?? session.CleanupWarningPath));
        }
        finally
        {
            CompleteSession(session, operation);
        }
    }

    private async Task CancelAsync()
    {
        IDictationSession? session;
        CancellationTokenSource? operation;
        DictationWorkflowPhase phase;
        lock (_gate)
        {
            phase = _state.Phase;
            if (!_starting && phase is not (DictationWorkflowPhase.Recording or DictationWorkflowPhase.Processing))
            {
                return;
            }

            session = _session;
            operation = _operation;
        }

        operation?.Cancel();
        (_clipboardPaster as ILiveClipboardPaster)?.EndSession();
        Publish(new DictationWorkflowState(
            DictationWorkflowPhase.Cancelled,
            CleanupWarningPath: CurrentState.CleanupWarningPath));

        if (phase == DictationWorkflowPhase.Recording && session is not null && operation is not null)
        {
            try
            {
                await session.CancelAsync(CancellationToken.None);
                Publish(new DictationWorkflowState(
                    DictationWorkflowPhase.Cancelled,
                    CleanupWarningPath: session.CleanupWarningPath));
            }
            catch (Exception ex)
            {
                Trace("workflow.cancel_failed", error: ex);
                Publish(new DictationWorkflowState(
                    DictationWorkflowPhase.Failed,
                    ErrorMessage: ex.Message,
                    CleanupWarningPath: session.CleanupWarningPath));
            }
            finally
            {
                CompleteSession(session, operation);
            }
        }
    }

    private void PublishCancellationIfNeeded(
        IDictationSession session,
        string? resultCleanupWarningPath = null)
    {
        var current = CurrentState;
        var cleanupWarningPath = resultCleanupWarningPath
            ?? session.CleanupWarningPath
            ?? current.CleanupWarningPath;
        var cleanupWarningChanged = !string.Equals(
            current.CleanupWarningPath,
            cleanupWarningPath,
            StringComparison.OrdinalIgnoreCase);
        if (current.Phase != DictationWorkflowPhase.Cancelled || cleanupWarningChanged)
        {
            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Cancelled,
                CleanupWarningPath: cleanupWarningPath));
        }
    }

    private void OnTranscriptUpdated(TranscriptUpdate update)
    {
        var corrected = ApplyTranscriptCorrections(update);
        var transcript = string.IsNullOrWhiteSpace(corrected.UnstableText)
            ? corrected.StableText
            : $"{corrected.StableText} {corrected.UnstableText}".Trim();

        DictationWorkflowState? next = null;
        lock (_gate)
        {
            if (_state.Phase == DictationWorkflowPhase.Recording)
            {
                _rawPreview = string.IsNullOrWhiteSpace(update.UnstableText)
                    ? update.StableText
                    : $"{update.StableText} {update.UnstableText}".Trim();
                _lastPreview = transcript;
                Trace("workflow.preview_text_stages", new { raw = _rawPreview, dictionaryCorrected = transcript });
                (_clipboardPaster as ILiveClipboardPaster)?.UpdatePreview(transcript);
                next = _state with { Transcript = transcript };
            }
        }

        if (next is not null)
        {
            Publish(next);
        }
    }

    private TranscriptUpdate ApplyTranscriptCorrections(TranscriptUpdate update)
    {
        var corrections = _getTranscriptCorrections();
        if (corrections.Count == 0)
        {
            return update;
        }

        if (string.IsNullOrWhiteSpace(update.UnstableText))
        {
            return update with
            {
                StableText = TranscriptCorrectionDictionary.Apply(update.StableText, corrections)
            };
        }

        var combined = $"{update.StableText} {update.UnstableText}".Trim();
        return update with
        {
            StableText = TranscriptCorrectionDictionary.Apply(combined, corrections),
            UnstableText = string.Empty
        };
    }

    private static bool IsActive(DictationWorkflowPhase phase)
    {
        return phase is DictationWorkflowPhase.Recording or DictationWorkflowPhase.Processing;
    }

    private void Publish(DictationWorkflowState state)
    {
        Trace("workflow.state", new { phase = state.Phase.ToString(), state.ErrorMessage });
        Action<DictationWorkflowState>? stateChanged;
        lock (_gate)
        {
            _state = state;
            stateChanged = StateChanged;
        }

        try
        {
            stateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Trace("workflow.state_subscriber_failed", error: ex);
        }
    }

    private void CompleteSession(IDictationSession session, CancellationTokenSource operation)
    {
        session.TranscriptUpdated -= OnTranscriptUpdated;
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                (_clipboardPaster as ILiveClipboardPaster)?.EndSession();
                _session = null;
                _activeTriggerMode = null;
            }

            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                operation.Dispose();
            }
        }
    }

    private async Task TryCancelSessionAsync(IDictationSession session)
    {
        try
        {
            await session.CancelAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace("workflow.cancel_failed", error: ex);
        }
    }

    private void Trace(string stage, object? data = null, Exception? error = null)
        => DiagnosticTrace.Write(stage, data, error, _recordingId);

}

public enum DictationIntent
{
    BeginHold,
    EndHold,
    Toggle,
    Cancel
}

public enum DictationTriggerMode
{
    Hold,
    Toggle
}

public enum DictationWorkflowPhase
{
    Idle,
    Recording,
    Processing,
    Pasted,
    Empty,
    Cancelled,
    Failed
}

public sealed record DictationWorkflowState(
    DictationWorkflowPhase Phase,
    DictationTriggerMode? TriggerMode = null,
    string Transcript = "",
    string? ProcessingDetail = null,
    string? ErrorMessage = null,
    string? CleanupWarningPath = null)
{
    public static DictationWorkflowState Idle { get; } = new(DictationWorkflowPhase.Idle);

    public bool CanCancel => Phase is DictationWorkflowPhase.Recording or DictationWorkflowPhase.Processing;
}

internal sealed class BatchDictationSessionFactory(IAudioRecorder recorder, ITranscriber transcriber) : IDictationSessionFactory
{
    public IDictationSession CreateSession()
    {
        return new BatchDictationSession(recorder, transcriber);
    }
}

internal sealed class BatchDictationSession(IAudioRecorder recorder, ITranscriber transcriber) : IDictationSession
{
    public string? CleanupWarningPath { get; private set; }

    public event Action<TranscriptUpdate>? TranscriptUpdated
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return recorder.StartAsync(cancellationToken);
    }

    public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        RecordedAudio? audio = null;
        try
        {
            audio = await recorder.StopAsync(cancellationToken);
            var result = await transcriber.TranscribeAsync(audio.Path, cancellationToken);
            Release(audio);
            audio = null;
            return new DictationSessionResult(result, CleanupWarningPath);
        }
        catch
        {
            Release(audio);

            throw;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        var audio = await recorder.StopAsync(cancellationToken);
        Release(audio);
    }

    private void Release(RecordedAudio? audio)
    {
        CleanupWarningPath ??= DictationSessionAudioOwnership.Release(audio);
    }
}
