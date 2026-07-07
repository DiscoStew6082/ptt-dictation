namespace ParakeetPtt.Core;

public sealed class DictationController
{
    private readonly IDictationSessionFactory _sessionFactory;
    private readonly IClipboardPaster _clipboardPaster;
    private readonly SessionHistory _history;
    private readonly Action<string>? _transcriptPreviewReady;
    private readonly Action<string>? _cleanupWarningReady;
    private readonly Action<TranscriptUpdate>? _transcriptUpdateReady;
    private readonly Func<IReadOnlyList<TranscriptCorrection>> _getTranscriptCorrections;
    private IDictationSession? _session;
    private bool _isRecording;
    private bool _isProcessing;

    public DictationController(
        IAudioRecorder recorder,
        ITranscriber transcriber,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Action<string>? transcriptPreviewReady = null,
        Action<string>? cleanupWarningReady = null,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
        : this(
            new BatchDictationSessionFactory(recorder, transcriber),
            clipboardPaster,
            history,
            transcriptPreviewReady,
            cleanupWarningReady,
            getTranscriptCorrections: getTranscriptCorrections)
    {
    }

    public DictationController(
        IDictationSessionFactory sessionFactory,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Action<string>? transcriptPreviewReady = null,
        Action<string>? cleanupWarningReady = null,
        Action<TranscriptUpdate>? transcriptUpdateReady = null,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
    {
        _sessionFactory = sessionFactory;
        _clipboardPaster = clipboardPaster;
        _history = history;
        _transcriptPreviewReady = transcriptPreviewReady;
        _cleanupWarningReady = cleanupWarningReady;
        _transcriptUpdateReady = transcriptUpdateReady;
        _getTranscriptCorrections = getTranscriptCorrections ?? (() => []);
    }

    public async Task<bool> HandleHotkeyDownAsync(CancellationToken cancellationToken)
    {
        if (_isRecording || _isProcessing)
        {
            return false;
        }

        _isRecording = true;
        _session = _sessionFactory.CreateSession();
        _session.TranscriptUpdated += OnTranscriptUpdated;
        try
        {
            await _session.StartAsync(cancellationToken);
        }
        catch
        {
            _session.TranscriptUpdated -= OnTranscriptUpdated;
            _session = null;
            _isRecording = false;
            throw;
        }

        return true;
    }

    public async Task<DictationOutcome> HandleHotkeyUpAsync(CancellationToken cancellationToken)
    {
        if (!_isRecording || _isProcessing)
        {
            return DictationOutcome.NotRecording;
        }

        _isRecording = false;
        _isProcessing = true;

        DictationSessionResult? sessionResult = null;
        try
        {
            if (_session is null)
            {
                return DictationOutcome.NotRecording;
            }

            sessionResult = await _session.StopAsync(cancellationToken);
            var corrected = TranscriptCorrectionDictionary.Apply(
                sessionResult.Transcript.Text,
                _getTranscriptCorrections());
            var cleaned = TranscriptNormalizer.Normalize(corrected);
            if (cleaned.Length == 0)
            {
                return DictationOutcome.EmptyTranscript;
            }

            TryPublishTranscriptPreview(cleaned);
            TryPublishTranscriptUpdate(new TranscriptUpdate(TranscriptUpdateKind.Final, cleaned));
            _history.Add(cleaned);
            await _clipboardPaster.PasteAsync(cleaned, cancellationToken);
            return DictationOutcome.Pasted;
        }
        finally
        {
            if (_session is not null)
            {
                _session.TranscriptUpdated -= OnTranscriptUpdated;
                _session = null;
            }

            if (sessionResult?.FinalAudio is { DeleteAfterUse: true } audio)
            {
                TryDeleteOrWarn(audio.Path);
            }

            _isProcessing = false;
        }
    }

    private void TryDeleteOrWarn(string path)
    {
        if (!TryDelete(path))
        {
            TryPublishCleanupWarning(path);
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

    private void TryPublishTranscriptPreview(string text)
    {
        try
        {
            _transcriptPreviewReady?.Invoke(text);
        }
        catch (Exception)
        {
        }
    }

    private void OnTranscriptUpdated(TranscriptUpdate update)
    {
        TryPublishTranscriptUpdate(ApplyTranscriptCorrections(update));
    }

    private TranscriptUpdate ApplyTranscriptCorrections(TranscriptUpdate update)
    {
        var corrections = _getTranscriptCorrections();
        if (corrections.Count == 0)
        {
            return update;
        }

        return update with
        {
            StableText = TranscriptCorrectionDictionary.Apply(update.StableText, corrections),
            UnstableText = TranscriptCorrectionDictionary.Apply(update.UnstableText, corrections)
        };
    }

    private void TryPublishTranscriptUpdate(TranscriptUpdate update)
    {
        try
        {
            _transcriptUpdateReady?.Invoke(update);
        }
        catch (Exception)
        {
        }
    }

    private void TryPublishCleanupWarning(string path)
    {
        try
        {
            _cleanupWarningReady?.Invoke(path);
        }
        catch (Exception)
        {
        }
    }
}

public enum DictationOutcome
{
    NotRecording,
    EmptyTranscript,
    Pasted
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
