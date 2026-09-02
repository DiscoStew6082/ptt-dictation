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
            _activeTriggerMode = mode;
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operation = operation;
            session = _sessionFactory.CreateSession();
            _session = session;
            session.TranscriptUpdated += OnTranscriptUpdated;
        }

        try
        {
            _clipboardPaster.CaptureTarget();
            await session.StartAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Recording, mode));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            await TryCancelSessionAsync(session);
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Cancelled));
            CompleteSession(session, operation);
        }
        catch (Exception ex)
        {
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Failed, ErrorMessage: ex.Message));
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
        DictationSessionResult? sessionResult = null;
        try
        {
            sessionResult = await session.StopAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var corrected = TranscriptCorrectionDictionary.Apply(
                sessionResult.Transcript.Text,
                _getTranscriptCorrections());
            var cleaned = TranscriptNormalizer.Normalize(corrected);
            if (cleaned.Length == 0)
            {
                Publish(new DictationWorkflowState(DictationWorkflowPhase.Empty));
                return;
            }

            Publish(new DictationWorkflowState(
                DictationWorkflowPhase.Processing,
                mode,
                cleaned,
                CurrentState.ProcessingDetail));
            await _clipboardPaster.PasteAsync(cleaned, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _history.Add(cleaned);
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Pasted, Transcript: cleaned));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (CurrentState.Phase != DictationWorkflowPhase.Cancelled)
            {
                Publish(new DictationWorkflowState(DictationWorkflowPhase.Cancelled));
            }
        }
        catch (Exception) when (operation.IsCancellationRequested)
        {
            if (CurrentState.Phase != DictationWorkflowPhase.Cancelled)
            {
                Publish(new DictationWorkflowState(DictationWorkflowPhase.Cancelled));
            }
        }
        catch (Exception ex)
        {
            Publish(new DictationWorkflowState(DictationWorkflowPhase.Failed, ErrorMessage: ex.Message));
        }
        finally
        {
            CompleteSession(session, operation);
            if (sessionResult?.FinalAudio is { DeleteAfterUse: true } audio && !TryDelete(audio.Path))
            {
                Publish(CurrentState with { CleanupWarningPath = audio.Path });
            }
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
        Publish(new DictationWorkflowState(DictationWorkflowPhase.Cancelled));

        if (phase == DictationWorkflowPhase.Recording && session is not null && operation is not null)
        {
            try
            {
                await session.CancelAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Publish(new DictationWorkflowState(DictationWorkflowPhase.Failed, ErrorMessage: ex.Message));
            }
            finally
            {
                CompleteSession(session, operation);
            }
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
        catch (Exception)
        {
        }
    }

    private void CompleteSession(IDictationSession session, CancellationTokenSource operation)
    {
        session.TranscriptUpdated -= OnTranscriptUpdated;
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
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

    private static async Task TryCancelSessionAsync(IDictationSession session)
    {
        try
        {
            await session.CancelAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
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
            return new DictationSessionResult(result, audio);
        }
        catch
        {
            if (audio is { DeleteAfterUse: true })
            {
                TryDelete(audio.Path);
            }

            throw;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        var audio = await recorder.StopAsync(cancellationToken);
        if (audio.DeleteAfterUse)
        {
            TryDelete(audio.Path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
