namespace PttDictation.Core;

public interface IAudioRecorder
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken);
}

public interface IChunkedAudioRecorder : IAudioRecorder
{
    event Action<RecordedAudio>? AudioChunkReady;
}

public interface ITranscriber
{
    Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken);
}

public interface IWarmableTranscriber
{
    Task WarmUpAsync(CancellationToken cancellationToken);
}

public interface IDictationSessionFactory
{
    IDictationSession CreateSession();
}

public interface IDictationSession
{
    event Action<TranscriptUpdate>? TranscriptUpdated;

    string? CleanupWarningPath => null;

    Task StartAsync(CancellationToken cancellationToken);

    Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public interface IClipboardPaster
{
    void CaptureTarget();

    Task PasteAsync(string text, CancellationToken cancellationToken);
}

public sealed record RecordedAudio(
    string Path,
    TimeSpan Duration,
    bool DeleteAfterUse = false,
    TimeSpan? OverlapDuration = null)
{
    public void Deconstruct(out string path, out TimeSpan duration, out bool deleteAfterUse)
    {
        path = Path;
        duration = Duration;
        deleteAfterUse = DeleteAfterUse;
    }
}

public sealed record TranscriptWord(string Text, TimeSpan Start, TimeSpan End, double? Confidence);

public sealed record TranscriptResult
{
    public TranscriptResult(
        string text,
        TimeSpan? inferenceTime,
        double? confidence,
        IReadOnlyList<TranscriptWord>? words = null)
    {
        Text = text;
        InferenceTime = inferenceTime;
        Confidence = confidence;
        Words = words ?? [];
    }

    public string Text { get; init; }

    public TimeSpan? InferenceTime { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyList<TranscriptWord> Words { get; init; }

    public void Deconstruct(out string text, out TimeSpan? inferenceTime, out double? confidence)
    {
        text = Text;
        inferenceTime = InferenceTime;
        confidence = Confidence;
    }
}

public sealed record DictationSessionResult(TranscriptResult Transcript, string? CleanupWarningPath = null);

public sealed record TranscriptUpdate(TranscriptUpdateKind Kind, string StableText, string UnstableText = "");

public enum TranscriptUpdateKind
{
    Partial,
    Final
}
