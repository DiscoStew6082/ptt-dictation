namespace PttDictation.Core;

public sealed class ChunkedTranscribingDictationSessionFactory : IDictationSessionFactory
{
    private readonly IChunkedAudioRecorder _recorder;
    private readonly ITranscriber _previewTranscriber;
    private readonly Func<ITranscriber> _finalTranscriberFactory;

    public ChunkedTranscribingDictationSessionFactory(IChunkedAudioRecorder recorder, ITranscriber transcriber)
        : this(recorder, transcriber, transcriber)
    {
    }

    public ChunkedTranscribingDictationSessionFactory(
        IChunkedAudioRecorder recorder,
        ITranscriber previewTranscriber,
        ITranscriber finalTranscriber)
        : this(recorder, previewTranscriber, () => finalTranscriber)
    {
    }

    public ChunkedTranscribingDictationSessionFactory(
        IChunkedAudioRecorder recorder,
        ITranscriber previewTranscriber,
        Func<ITranscriber> finalTranscriberFactory)
    {
        _recorder = recorder;
        _previewTranscriber = previewTranscriber;
        _finalTranscriberFactory = finalTranscriberFactory;
    }

    public IDictationSession CreateSession()
    {
        return new ChunkedTranscribingDictationSession(_recorder, _previewTranscriber, _finalTranscriberFactory());
    }
}

public sealed class ChunkedTranscribingDictationSession(
    IChunkedAudioRecorder recorder,
    ITranscriber previewTranscriber,
    ITranscriber finalTranscriber) : IDictationSession
{
    private readonly object _gate = new();
    private readonly IncrementalTranscriptAssembler _assembler = new();
    private readonly Dictionary<string, RecordedAudio> _ownedChunks = new(StringComparer.OrdinalIgnoreCase);
    private Task _chunkProcessing = Task.CompletedTask;
    private readonly LinkedList<QueuedAudioChunk> _pendingChunks = new();
    private bool _chunkWorkerRunning;
    private TimeSpan? _latestCumulativeDuration;
    private CancellationTokenSource? _chunkCancellation;
    private string? _cleanupWarningPath;
    private bool _started;
    private bool _stopping;
    private string? _recordingId;
    private int _nextChunkId;

    public event Action<TranscriptUpdate>? TranscriptUpdated;

    public string? CleanupWarningPath
    {
        get
        {
            lock (_gate)
            {
                return _cleanupWarningPath;
            }
        }
    }

    public ChunkedTranscribingDictationSession(IChunkedAudioRecorder recorder, ITranscriber transcriber)
        : this(recorder, transcriber, transcriber)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _recordingId = DiagnosticTrace.CurrentRecordingId;
            _nextChunkId = 0;
            _latestCumulativeDuration = null;
            _stopping = false;
            _chunkProcessing = Task.CompletedTask;
            _chunkCancellation = new CancellationTokenSource();
            _cleanupWarningPath = null;
            _ownedChunks.Clear();
        }

        recorder.AudioChunkReady += OnAudioChunkReady;
        Trace("chunk_session.start");
        try
        {
            await recorder.StartAsync(cancellationToken);
            Trace("chunk_session.started");
            BeginTranscriberWarmUp(cancellationToken);
        }
        catch (Exception ex)
        {
            Trace("chunk_session.start_failed", error: ex);
            recorder.AudioChunkReady -= OnAudioChunkReady;
            lock (_gate)
            {
                _started = false;
                _stopping = false;
                _chunkCancellation?.Dispose();
                _chunkCancellation = null;
            }

            throw;
        }
    }

    private void BeginTranscriberWarmUp(CancellationToken operationToken)
    {
        if (previewTranscriber is IWarmableTranscriber preview)
        {
            _ = Task.Run(() => WarmUpWithoutBlockingRecordingAsync(preview, "preview", operationToken));
        }

        if (!ReferenceEquals(previewTranscriber, finalTranscriber)
            && finalTranscriber is IWarmableTranscriber final)
        {
            // The operation survives StopAsync cancelling obsolete preview chunks.
            _ = Task.Run(() => WarmUpWithoutBlockingRecordingAsync(final, "final", operationToken));
        }
    }

    private async Task WarmUpWithoutBlockingRecordingAsync(
        IWarmableTranscriber warmable, string role, CancellationToken operationToken)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        try
        {
            operationToken.ThrowIfCancellationRequested();
            Trace($"{role}.warmup_started");
            await warmable.WarmUpAsync(operationToken);
            Trace($"{role}.warmup_completed");
        }
        catch (Exception ex)
        {
            Trace(ex is OperationCanceledException ? $"{role}.warmup_cancelled" : $"{role}.warmup_failed", error: ex);
        }
    }

    public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        Trace("chunk_session.stop");
        RecordedAudio? finalAudio = null;
        try
        {
            lock (_gate)
            {
                _stopping = true;
            }

            finalAudio = await recorder.StopAsync(cancellationToken);
            Trace("chunk_session.audio_stopped", new { finalAudio.Duration });
            recorder.AudioChunkReady -= OnAudioChunkReady;
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            Trace("recognition.final_started");
            var finalTranscript = await finalTranscriber.TranscribeAsync(finalAudio.Path, cancellationToken);
            Trace("recognition.final_raw", finalTranscript);
            Release(finalAudio);
            finalAudio = null;
            return new DictationSessionResult(finalTranscript, CleanupWarningPath);
        }
        catch (Exception ex)
        {
            Trace(ex is OperationCanceledException ? "chunk_session.stop_cancelled" : "chunk_session.stop_failed", error: ex);
            Release(finalAudio);
            ReleaseOutstandingChunks();

            throw;
        }
        finally
        {
            recorder.AudioChunkReady -= OnAudioChunkReady;
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            lock (_gate)
            {
                _started = false;
                _stopping = false;
                _chunkCancellation?.Dispose();
                _chunkCancellation = null;
            }
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        Trace("chunk_session.cancel_started");
        RecordedAudio? finalAudio = null;
        try
        {
            lock (_gate)
            {
                if (!_started)
                {
                    return;
                }

                _stopping = true;
            }

            finalAudio = await recorder.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Trace(ex is OperationCanceledException ? "chunk_session.cancel_cancelled" : "chunk_session.cancel_failed", error: ex);
            throw;
        }
        finally
        {
            recorder.AudioChunkReady -= OnAudioChunkReady;
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            Release(finalAudio);

            lock (_gate)
            {
                _started = false;
                _stopping = false;
                _chunkCancellation?.Dispose();
                _chunkCancellation = null;
            }
            Trace("chunk_session.cancel_cleanup_completed");
        }
    }

    private void CancelChunkProcessing()
    {
        try
        {
            _chunkCancellation?.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            Trace("chunk_session.cancel_disposed", error: ex);
        }
    }

    private void OnAudioChunkReady(RecordedAudio chunk)
    {
        lock (_gate)
        {
            if (!_started || _stopping)
            {
                Trace("chunk.rejected", new { reason = "session_not_accepting", chunk.Duration, chunk.OverlapDuration });
                TryDeleteIfNeeded(chunk);
                return;
            }

            if (chunk.IsCumulative)
            {
                // WAV publication tasks can finish out of capture order. Duration
                // identifies a complete prefix; arrival order does not.
                if (_latestCumulativeDuration is { } latest && chunk.Duration <= latest)
                {
                    Trace("chunk.stale_snapshot", new { chunk.Duration, latestDuration = latest });
                    if (!_ownedChunks.ContainsKey(chunk.Path)) Release(chunk);
                    return;
                }
                _latestCumulativeDuration = chunk.Duration;
            }
            var cancellationToken = _chunkCancellation?.Token ?? CancellationToken.None;
            _ownedChunks[chunk.Path] = chunk;
            var queuedAt = Environment.TickCount64;
            var chunkId = ++_nextChunkId;
            // A newer complete snapshot supersedes an unstarted complete snapshot.
            // Keep one active inference and only the newest pending snapshot, so
            // longer dictations cannot build a queue of obsolete preview work.
            if (chunk.IsCumulative && _pendingChunks.Last is { Value.Audio.IsCumulative: true } superseded)
            {
                _pendingChunks.RemoveLast();
                ReleaseOwnedChunk(superseded.Value.Audio);
                Trace("chunk.superseded", new { chunkId = superseded.Value.Id, replacementChunkId = chunkId });
            }
            _pendingChunks.AddLast(new QueuedAudioChunk(chunk, cancellationToken, queuedAt, chunkId));
            Trace("chunk.queued", new { chunkId, chunk.Duration, chunk.OverlapDuration, chunk.IsCumulative, ownedChunks = _ownedChunks.Count });
            if (!_chunkWorkerRunning)
            {
                _chunkWorkerRunning = true;
                _chunkProcessing = Task.Run(ProcessQueuedChunksAsync);
            }
        }
    }

    private sealed record QueuedAudioChunk(RecordedAudio Audio, CancellationToken Cancellation, long QueuedAt, int Id);

    private async Task ProcessQueuedChunksAsync()
    {
        while (true)
        {
            QueuedAudioChunk queued;
            lock (_gate)
            {
                if (_pendingChunks.First is not { } next)
                {
                    _chunkWorkerRunning = false;
                    return;
                }
                queued = next.Value;
                _pendingChunks.RemoveFirst();
            }
            if (queued.Cancellation.IsCancellationRequested) ReleaseOwnedChunk(queued.Audio);
            else await ProcessChunkAsync(queued.Audio, queued.Cancellation, queued.QueuedAt, queued.Id);
        }
    }

    private async Task ProcessChunkAsync(RecordedAudio chunk, CancellationToken cancellationToken, long queuedAt, int chunkId)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        var startedAt = Environment.TickCount64;
        Trace("chunk.started", new { chunkId, queueMilliseconds = startedAt - queuedAt, chunk.Duration, chunk.OverlapDuration });
        try
        {
            var transcript = await previewTranscriber.TranscribeAsync(chunk.Path, cancellationToken);
            Trace("recognition.chunk_raw", new { chunkId, transcript.Text, transcript.Words, transcript.InferenceTime, transcript.Confidence });
            if (cancellationToken.IsCancellationRequested)
            {
                Trace("chunk.result_cancelled", new { chunkId });
                return;
            }

            var stableText = chunk.IsCumulative ? transcript.Text
                : _assembler.Add(transcript, chunk.OverlapDuration.GetValueOrDefault());
            Trace("preview.assembled", new { chunkId, chunk.IsCumulative, text = stableText });
            if (stableText.Length > 0)
            {
                TryPublish(new TranscriptUpdate(TranscriptUpdateKind.Partial, stableText));
            }
        }
        catch (Exception ex)
        {
            Trace(ex is OperationCanceledException ? "chunk.cancelled" : "chunk.failed", new { chunkId }, ex);
        }
        finally
        {
            Trace("chunk.finished", new { chunkId, elapsedMilliseconds = Environment.TickCount64 - startedAt });
            ReleaseOwnedChunk(chunk);
        }
    }

    private async Task WaitForChunkProcessingToSettleAsync()
    {
        var chunkProcessing = GetChunkProcessingTask();
        try
        {
            await chunkProcessing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException ex)
        {
            Trace("chunk.settle_cancelled", error: ex);
        }
        catch (TimeoutException ex)
        {
            Trace("chunk.settle_timeout", error: ex);
        }
    }

    private Task GetChunkProcessingTask()
    {
        lock (_gate)
        {
            return _chunkProcessing;
        }
    }

    private void TryPublish(TranscriptUpdate update)
    {
        try
        {
            TranscriptUpdated?.Invoke(update);
        }
        catch (Exception ex)
        {
            Trace("preview.subscriber_failed", error: ex);
        }
    }

    private void TryDeleteIfNeeded(RecordedAudio audio)
    {
        Release(audio);
    }

    private void ReleaseOwnedChunk(RecordedAudio chunk)
    {
        lock (_gate)
        {
            if (!_ownedChunks.Remove(chunk.Path))
            {
                return;
            }

            Release(chunk);
        }
    }

    private void ReleaseOutstandingChunks()
    {
        RecordedAudio[] chunks;
        lock (_gate)
        {
            chunks = [.. _ownedChunks.Values];
            _ownedChunks.Clear();
        }

        foreach (var chunk in chunks)
        {
            Release(chunk);
        }
    }

    private void Release(RecordedAudio? audio)
    {
        var warningPath = DictationSessionAudioOwnership.Release(audio);
        if (warningPath is null)
        {
            return;
        }

        lock (_gate)
        {
            _cleanupWarningPath ??= warningPath;
        }
    }

    private void Trace(string stage, object? data = null, Exception? error = null)
        => DiagnosticTrace.Write(stage, data, error, _recordingId);
}

internal sealed class IncrementalTranscriptAssembler
{
    private readonly List<string> _words = [];

    public string Add(TranscriptResult transcript, TimeSpan overlapDuration)
    {
        if (transcript.Words.Count == 0)
        {
            return Add(transcript.Text);
        }

        var words = transcript.Words
            .Where(word => word.End > overlapDuration)
            .Select(word => word.Text)
            .ToList();
        if (words.Count == 0)
        {
            return Text;
        }

        AddWords(words);
        return Text;
    }

    private string Add(string transcript)
    {
        var incoming = SplitWords(transcript);
        if (incoming.Count == 0)
        {
            return Text;
        }

        AddWords(incoming);
        return Text;
    }

    private void AddWords(IReadOnlyList<string> incoming)
    {
        var overlap = FindOverlap(_words, incoming);
        _words.AddRange(incoming.Skip(overlap));
    }

    private string Text => string.Join(" ", _words);

    private static List<string> SplitWords(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int FindOverlap(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        var max = Math.Min(existing.Count, incoming.Count);
        for (var length = max; length > 0; length--)
        {
            var matches = true;
            for (var i = 0; i < length; i++)
            {
                if (!string.Equals(
                    existing[existing.Count - length + i],
                    incoming[i],
                    StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return length;
            }
        }

        return 0;
    }
}
