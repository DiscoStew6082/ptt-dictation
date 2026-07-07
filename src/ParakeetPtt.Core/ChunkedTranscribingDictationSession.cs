namespace ParakeetPtt.Core;

public sealed class ChunkedTranscribingDictationSessionFactory : IDictationSessionFactory
{
    private readonly IChunkedAudioRecorder _recorder;
    private readonly ITranscriber _previewTranscriber;
    private readonly ITranscriber _finalTranscriber;

    public ChunkedTranscribingDictationSessionFactory(IChunkedAudioRecorder recorder, ITranscriber transcriber)
        : this(recorder, transcriber, transcriber)
    {
    }

    public ChunkedTranscribingDictationSessionFactory(
        IChunkedAudioRecorder recorder,
        ITranscriber previewTranscriber,
        ITranscriber finalTranscriber)
    {
        _recorder = recorder;
        _previewTranscriber = previewTranscriber;
        _finalTranscriber = finalTranscriber;
    }

    public IDictationSession CreateSession()
    {
        return new ChunkedTranscribingDictationSession(_recorder, _previewTranscriber, _finalTranscriber);
    }
}

public sealed class ChunkedTranscribingDictationSession(
    IChunkedAudioRecorder recorder,
    ITranscriber previewTranscriber,
    ITranscriber finalTranscriber) : IDictationSession
{
    private readonly object _gate = new();
    private readonly IncrementalTranscriptAssembler _assembler = new();
    private Task _chunkProcessing = Task.CompletedTask;
    private CancellationTokenSource? _chunkCancellation;
    private bool _started;
    private bool _stopping;

    public event Action<TranscriptUpdate>? TranscriptUpdated;

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
            _stopping = false;
            _chunkProcessing = Task.CompletedTask;
            _chunkCancellation = new CancellationTokenSource();
        }

        recorder.AudioChunkReady += OnAudioChunkReady;
        try
        {
            await recorder.StartAsync(cancellationToken);
        }
        catch
        {
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

    public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        RecordedAudio? finalAudio = null;
        try
        {
            lock (_gate)
            {
                _stopping = true;
            }

            finalAudio = await recorder.StopAsync(cancellationToken);
            recorder.AudioChunkReady -= OnAudioChunkReady;
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            var finalTranscript = await finalTranscriber.TranscribeAsync(finalAudio.Path, cancellationToken);
            return new DictationSessionResult(finalTranscript, finalAudio);
        }
        catch
        {
            if (finalAudio is { DeleteAfterUse: true })
            {
                TryDelete(finalAudio.Path);
            }

            throw;
        }
        finally
        {
            recorder.AudioChunkReady -= OnAudioChunkReady;
            CancelChunkProcessing();
            await WaitForChunkProcessingToSettleAsync();
            lock (_gate)
            {
                _started = false;
                _stopping = false;
                _chunkCancellation?.Dispose();
                _chunkCancellation = null;
            }
        }
    }

    private void CancelChunkProcessing()
    {
        try
        {
            _chunkCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnAudioChunkReady(RecordedAudio chunk)
    {
        lock (_gate)
        {
            if (!_started || _stopping)
            {
                TryDeleteIfNeeded(chunk);
                return;
            }

            var cancellationToken = _chunkCancellation?.Token ?? CancellationToken.None;
            _chunkProcessing = _chunkProcessing
                .ContinueWith(
                    _ => ProcessChunkAsync(chunk, cancellationToken),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task ProcessChunkAsync(RecordedAudio chunk, CancellationToken cancellationToken)
    {
        try
        {
            var transcript = await previewTranscriber.TranscribeAsync(chunk.Path, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var stableText = _assembler.Add(transcript, chunk.OverlapDuration.GetValueOrDefault());
            if (stableText.Length > 0)
            {
                TryPublish(new TranscriptUpdate(TranscriptUpdateKind.Partial, stableText));
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            TryDeleteIfNeeded(chunk);
        }
    }

    private async Task WaitForChunkProcessingToSettleAsync()
    {
        var chunkProcessing = GetChunkProcessingTask();
        try
        {
            await chunkProcessing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
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
        catch (Exception)
        {
        }
    }

    private static void TryDeleteIfNeeded(RecordedAudio audio)
    {
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
