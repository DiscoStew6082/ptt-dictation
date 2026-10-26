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

// A descriptor names an immutable prefix, without copying PCM or publishing a WAV.
public sealed record CumulativeAudioSnapshot(Guid RecordingId, long EndSample);

public interface ICumulativeAudioRecorder : IAudioRecorder
{
    event Action<CumulativeAudioSnapshot>? SnapshotReady;
    Task StartAsync(Guid recordingId, CancellationToken cancellationToken);
    // Called only by the session's selected preview worker. The caller owns the
    // returned WAV until recognition settles, including ignored cancellation.
    RecordedAudio MaterializeSnapshot(CumulativeAudioSnapshot snapshot, CancellationToken cancellationToken);
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
    Task PrepareCaptureAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void CaptureTarget();

    Task PasteAsync(string text, CancellationToken cancellationToken);
}

public interface ILiveClipboardPaster : IClipboardPaster
{
    /// <summary>A terminal output failure for the currently captured dictation.</summary>
    event Action<Exception>? InsertionFailed;

    /// <summary>Immutable delivery evidence captured when the current output failed.</summary>
    LiveInsertionFailureDelivery? FailureDelivery => null;

    void UpdatePreview(string text);

    void EndSession();
}

public sealed record LiveInsertionFailureDelivery(
    string AcknowledgedText,
    bool CanCompleteFromAcknowledgedText,
    bool InitialCaptureRejected = false);

public sealed record RecordedAudio(
    string Path,
    TimeSpan Duration,
    bool DeleteAfterUse = false,
    TimeSpan? OverlapDuration = null,
    bool IsCumulative = false);

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

}

public sealed record DictationSessionResult(TranscriptResult Transcript, string? CleanupWarningPath = null);

public sealed record TranscriptUpdate(TranscriptUpdateKind Kind, string StableText, string UnstableText = "");

public enum TranscriptUpdateKind
{
    Partial,
    Final
}
