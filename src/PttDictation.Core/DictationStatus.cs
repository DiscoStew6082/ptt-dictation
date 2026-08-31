namespace PttDictation.Core;

public sealed record DictationStatus(
    DictationStatusKind Kind,
    string Title,
    string Message,
    bool AutoHide);

public enum DictationStatusKind
{
    Listening,
    Transcribing,
    TranscriptPreview,
    Pasted,
    Cancelled,
    EmptyTranscript,
    Error
}

public static class DictationStatusCatalog
{
    public static DictationStatus Listening { get; } = new(
        DictationStatusKind.Listening,
        "Listening",
        "Release Right Ctrl to transcribe.",
        AutoHide: false);

    public static DictationStatus Transcribing { get; } = new(
        DictationStatusKind.Transcribing,
        "Finalizing transcript",
        "Processing the complete recording before paste. Click this panel to cancel.",
        AutoHide: false);

    public static DictationStatus PreparingTranscription(string message)
    {
        return new DictationStatus(
            DictationStatusKind.Transcribing,
            "Preparing transcription engine",
            $"{message} Click this panel to cancel.",
            AutoHide: false);
    }

    public static DictationStatus Pasted { get; } = new(
        DictationStatusKind.Pasted,
        "Pasted",
        "Transcript pasted into the active app.",
        AutoHide: true);

    public static DictationStatus PasteCancelled { get; } = new(
        DictationStatusKind.Cancelled,
        "Paste cancelled",
        "The transcript was not pasted.",
        AutoHide: true);

    public static DictationStatus PastedTranscript(string text)
    {
        return new DictationStatus(
            DictationStatusKind.Pasted,
            "Pasted",
            text,
            AutoHide: true);
    }

    public static DictationStatus TranscriptPreview(string text)
    {
        return new DictationStatus(
            DictationStatusKind.TranscriptPreview,
            "Transcript",
            text,
            AutoHide: false);
    }

    public static DictationStatus EmptyTranscript { get; } = new(
        DictationStatusKind.EmptyTranscript,
        "No speech detected",
        "Nothing was pasted.",
        AutoHide: true);

    public static DictationStatus Error(string message)
    {
        return new DictationStatus(
            DictationStatusKind.Error,
            "Dictation failed",
            message,
            AutoHide: true);
    }
}
