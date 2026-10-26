namespace PttDictation.Core;

public sealed class ChunkedTranscribingDictationSessionFactory : IDictationSessionFactory
{
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriber _previewTranscriber;
    private readonly Func<ITranscriber> _finalTranscriberFactory;

    public ChunkedTranscribingDictationSessionFactory(IAudioRecorder recorder, ITranscriber transcriber)
        : this(recorder, transcriber, transcriber)
    {
    }

    public ChunkedTranscribingDictationSessionFactory(
        IAudioRecorder recorder,
        ITranscriber previewTranscriber,
        ITranscriber finalTranscriber)
        : this(recorder, previewTranscriber, () => finalTranscriber)
    {
    }

    public ChunkedTranscribingDictationSessionFactory(
        IAudioRecorder recorder,
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
    IAudioRecorder recorder,
    ITranscriber previewTranscriber,
    ITranscriber finalTranscriber) : IDictationSession
{
    private readonly object _gate = new();
    private IncrementalTranscriptAssembler _assembler = new();
    private readonly Dictionary<string, RecordedAudio> _ownedChunks = new(StringComparer.OrdinalIgnoreCase);
    private Task _chunkProcessing = Task.CompletedTask;
    private readonly LinkedList<QueuedPreview> _pendingChunks = new();
    private Guid _recordingGeneration;
    private long _latestEndSample;
    private RecordedAudio? _activeChunk;
    private bool _chunkWorkerRunning;
    private TimeSpan? _latestCumulativeDuration;
    private CancellationTokenSource? _chunkCancellation;
    private string? _cleanupWarningPath;
    private bool _started;
    private bool _stopping;
    private string? _recordingId;
    private int _nextChunkId;
    private PreviewObservations _previewObservations = new();

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

    public ChunkedTranscribingDictationSession(IAudioRecorder recorder, ITranscriber transcriber)
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

            if (!_chunkProcessing.IsCompleted)
                throw new InvalidOperationException("The previous recording's preview is still settling.");
            _recordingGeneration = Guid.NewGuid();
            _latestEndSample = 0;
            _pendingChunks.Clear();
            _chunkWorkerRunning = false;
            _assembler = new();
            _started = true;
            _recordingId = DiagnosticTrace.CurrentRecordingId;
            _nextChunkId = 0;
            _previewObservations = new();
            _latestCumulativeDuration = null;
            _stopping = false;
            _chunkProcessing = Task.CompletedTask;
            _chunkCancellation = new CancellationTokenSource();
            _cleanupWarningPath = null;
            _ownedChunks.Clear();
        }

        Trace("chunk_session.start");
        try
        {
            if (recorder is ICumulativeAudioRecorder snapshots) snapshots.SnapshotReady += OnSnapshotReady;
            else if (recorder is IChunkedAudioRecorder chunks) chunks.AudioChunkReady += OnAudioChunkReady;
            else throw new InvalidOperationException("Preview recording requires snapshots or legacy chunks.");
            if (recorder is ICumulativeAudioRecorder cumulative)
                await cumulative.StartAsync(_recordingGeneration, cancellationToken);
            else await recorder.StartAsync(cancellationToken);
            Trace("chunk_session.started");
            BeginTranscriberWarmUp(cancellationToken);
        }
        catch (Exception ex)
        {
            Trace("chunk_session.start_failed", error: ex);
            UnsubscribeRecorder();
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            lock (_gate)
            {
                _started = false;
                _stopping = false;
                DisposeCancellationAfterProcessing();
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
            PreviewObservations previewObservations;
            lock (_gate)
            {
                _stopping = true;
                previewObservations = _previewObservations;
            }

            CancelChunkProcessing();
            finalAudio = await recorder.StopAsync(cancellationToken);
            Trace("chunk_session.audio_stopped", new { finalAudio.Duration });
            UnsubscribeRecorder();
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            var previewsSettled = GetChunkProcessingTask().IsCompletedSuccessfully;
            ReleaseOutstandingChunks();
            Trace("recognition.final_started");
            var finalTranscript = await finalTranscriber.TranscribeAsync(finalAudio.Path, cancellationToken);
            Trace("recognition.final_raw", finalTranscript);
            if (previewsSettled && !cancellationToken.IsCancellationRequested)
                TraceEmptyPreviewDiscrepancy(previewObservations, finalAudio.Duration, finalTranscript.Text);
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
            UnsubscribeRecorder();
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            lock (_gate)
            {
                _started = false;
                _stopping = false;
                DisposeCancellationAfterProcessing();
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

            CancelChunkProcessing();
            finalAudio = await recorder.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Trace(ex is OperationCanceledException ? "chunk_session.cancel_cancelled" : "chunk_session.cancel_failed", error: ex);
            throw;
        }
        finally
        {
            UnsubscribeRecorder();
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            ReleaseOutstandingChunks();
            Release(finalAudio);

            lock (_gate)
            {
                _started = false;
                _stopping = false;
                DisposeCancellationAfterProcessing();
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

    private void UnsubscribeRecorder()
    {
        if (recorder is ICumulativeAudioRecorder snapshots) snapshots.SnapshotReady -= OnSnapshotReady;
        else if (recorder is IChunkedAudioRecorder chunks) chunks.AudioChunkReady -= OnAudioChunkReady;
    }

    private void DisposeCancellationAfterProcessing()
    {
        var cancellation = _chunkCancellation;
        _chunkCancellation = null;
        // A recognizer may ignore cancellation. Its worker still owns the token
        // and WAV until it returns; a timeout cannot dispose either underneath it.
        if (cancellation is not null)
            _ = _chunkProcessing.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnSnapshotReady(CumulativeAudioSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_started || _stopping || snapshot.RecordingId != _recordingGeneration
                || snapshot.EndSample <= _latestEndSample) return;
            _latestEndSample = snapshot.EndSample;
            QueuePreview(new SnapshotPreview(snapshot), cumulative: true);
        }
    }

    // Compatibility adapter for materialized overlapping-window recorders. The
    // production recorder uses only descriptors; legacy windows keep their order.
    private void OnAudioChunkReady(RecordedAudio chunk)
    {
        lock (_gate)
        {
            if (!_started || _stopping)
            {
                Release(chunk);
                return;
            }
            if (_ownedChunks.ContainsKey(chunk.Path)) return;
            if (chunk.IsCumulative)
            {
                if (_latestCumulativeDuration is { } latest && chunk.Duration <= latest)
                {
                    Trace("chunk.stale_snapshot", new { chunk.Duration, latestDuration = latest });
                    Release(chunk);
                    return;
                }
                _latestCumulativeDuration = chunk.Duration;
            }
            _ownedChunks[chunk.Path] = chunk;
            QueuePreview(new LegacyPreview(chunk), chunk.IsCumulative);
        }
    }

    // Called under _gate. This is the only preview scheduler, before any
    // production prefix copy or WAV creation, with one active and one latest.
    private void QueuePreview(PreviewSource source, bool cumulative)
    {
        var chunkId = ++_nextChunkId;
        if (cumulative && _pendingChunks.Last is { Value.Cumulative: true } superseded)
        {
            _pendingChunks.RemoveLast();
            if (superseded.Value.Source is LegacyPreview old) ReleaseOwnedChunk(old.Audio);
            Trace("chunk.superseded", new { chunkId = superseded.Value.Id, replacementChunkId = chunkId });
        }
        _pendingChunks.AddLast(new QueuedPreview(source, cumulative, _chunkCancellation!.Token,
            Environment.TickCount64, chunkId, _previewObservations));
        Trace("chunk.queued", new { chunkId, cumulative, pendingChunks = _pendingChunks.Count });
        if (!_chunkWorkerRunning)
        {
            _chunkWorkerRunning = true;
            _chunkProcessing = Task.Run(ProcessQueuedChunksAsync);
        }
    }

    private abstract record PreviewSource;
    private sealed record SnapshotPreview(CumulativeAudioSnapshot Snapshot) : PreviewSource;
    private sealed record LegacyPreview(RecordedAudio Audio) : PreviewSource;
    private sealed record QueuedPreview(PreviewSource Source, bool Cumulative, CancellationToken Cancellation,
        long QueuedAt, int Id, PreviewObservations Observations);

    private sealed class PreviewObservations
    {
        public int Successful, Empty, Failed, Cancelled;
        public bool HasNonemptyPreview;
        public double MinimumAudioMilliseconds = double.PositiveInfinity;
        public double MaximumAudioMilliseconds;
        public long ProcessingMilliseconds;
    }

    private async Task ProcessQueuedChunksAsync()
    {
        while (true)
        {
            QueuedPreview queued;
            lock (_gate)
            {
                if (_pendingChunks.First is not { } next)
                {
                    _chunkWorkerRunning = false;
                    return;
                }
                queued = next.Value;
                _pendingChunks.RemoveFirst();
                // Claim legacy ownership before Stop can release pending work.
                _activeChunk = (queued.Source as LegacyPreview)?.Audio;
            }
            await ProcessChunkAsync(queued);
        }
    }

    private async Task ProcessChunkAsync(QueuedPreview queued)
    {
        using var traceScope = DiagnosticTrace.EnterRecording(_recordingId ?? string.Empty);
        var startedAt = Environment.TickCount64;
        var chunkId = queued.Id;
        var observations = queued.Observations;
        var cancellationToken = queued.Cancellation;
        RecordedAudio? chunk = (queued.Source as LegacyPreview)?.Audio;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (queued.Source is SnapshotPreview snapshot)
            {
                chunk = ((ICumulativeAudioRecorder)recorder).MaterializeSnapshot(snapshot.Snapshot, cancellationToken);
                lock (_gate)
                {
                    _activeChunk = chunk;
                    _ownedChunks[chunk.Path] = chunk;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk is null) throw new InvalidOperationException("Preview source did not supply audio.");
            Trace("chunk.started", new { chunkId, queueMilliseconds = startedAt - queued.QueuedAt, chunk.Duration, chunk.OverlapDuration });
            var transcript = await previewTranscriber.TranscribeAsync(chunk.Path, cancellationToken);
            Trace("recognition.chunk_raw", new { chunkId, transcript.Text, transcript.Words, transcript.InferenceTime, transcript.Confidence });
            cancellationToken.ThrowIfCancellationRequested();

            var stableText = queued.Cumulative ? transcript.Text
                : _assembler.Add(transcript, chunk.OverlapDuration.GetValueOrDefault());
            lock (_gate)
            {
                observations.Successful++;
                if (string.IsNullOrWhiteSpace(transcript.Text)) observations.Empty++;
                observations.HasNonemptyPreview |= !string.IsNullOrWhiteSpace(stableText);
                observations.MinimumAudioMilliseconds = Math.Min(observations.MinimumAudioMilliseconds, chunk.Duration.TotalMilliseconds);
                observations.MaximumAudioMilliseconds = Math.Max(observations.MaximumAudioMilliseconds, chunk.Duration.TotalMilliseconds);
            }
            Trace("preview.assembled", new { chunkId, IsCumulative = queued.Cumulative, text = stableText });
            if (!cancellationToken.IsCancellationRequested && stableText.Length > 0)
                TryPublish(new TranscriptUpdate(TranscriptUpdateKind.Partial, stableText));
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ex is OperationCanceledException) observations.Cancelled++;
                else observations.Failed++;
            }
            Trace(ex is OperationCanceledException ? "chunk.cancelled" : "chunk.failed", new { chunkId }, ex);
        }
        finally
        {
            var elapsedMilliseconds = Environment.TickCount64 - startedAt;
            lock (_gate)
            {
                observations.ProcessingMilliseconds += elapsedMilliseconds;
                _activeChunk = null;
            }
            if (chunk is not null) ReleaseOwnedChunk(chunk);
            Trace("chunk.finished", new { chunkId, elapsedMilliseconds });
        }
    }

    private void TraceEmptyPreviewDiscrepancy(PreviewObservations observations, TimeSpan finalDuration, string finalText)
    {
        // Only settled, successful recognition is compared. Obsolete cancelled
        // work is counted separately; provider failures already have their own
        // diagnostics. Keep this warning metadata-only and behavior-neutral.
        if (string.IsNullOrWhiteSpace(finalText)) return;
        lock (_gate)
        {
            if (observations.Successful < 2 || observations.Empty != observations.Successful
                || observations.Failed != 0 || observations.HasNonemptyPreview) return;
            Trace("recognition.preview_empty_warning", new
            {
                successfulPreviewCount = observations.Successful,
                emptyPreviewCount = observations.Empty,
                failedPreviewCount = observations.Failed,
                cancelledPreviewCount = observations.Cancelled,
                minimumPreviewAudioMilliseconds = observations.MinimumAudioMilliseconds,
                maximumPreviewAudioMilliseconds = observations.MaximumAudioMilliseconds,
                previewProcessingMilliseconds = observations.ProcessingMilliseconds,
                finalAudioMilliseconds = finalDuration.TotalMilliseconds,
                finalCharacterCount = finalText.Length
            });
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

    private void ReleaseOwnedChunk(RecordedAudio chunk)
    {
        lock (_gate)
        {
            if (!_ownedChunks.Remove(chunk.Path))
            {
                return;
            }
        }
        // Capture's descriptor callback shares _gate; never hold it over disk I/O.
        Release(chunk);
    }

    private void ReleaseOutstandingChunks()
    {
        List<RecordedAudio> pending = [];
        lock (_gate)
        {
            foreach (var queued in _pendingChunks)
            {
                queued.Observations.Cancelled++;
                if (queued.Source is LegacyPreview legacy) pending.Add(legacy.Audio);
            }
            _pendingChunks.Clear();
            // Retained active audio is reported honestly, then deleted by its
            // sole worker owner when recognition actually returns.
            if (_activeChunk is { DeleteAfterUse: true } active)
                _cleanupWarningPath ??= active.Path;
        }
        foreach (var chunk in pending) ReleaseOwnedChunk(chunk);
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
