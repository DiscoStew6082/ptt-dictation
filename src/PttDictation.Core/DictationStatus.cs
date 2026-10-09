namespace PttDictation.Core;

public sealed record DictationStatus(
    DictationStatusKind Kind,
    string Title,
    string Message,
    bool AutoHide);

public enum DictationStatusKind
{
    Listening,
    Cancelled,
    EmptyTranscript,
    Error,
    Preparing
}

public static class DictationStatusCatalog
{
    public static DictationStatus Preparing { get; } = new(
        DictationStatusKind.Preparing,
        "Preparing textbox",
        "Not recording yet. Wait until this message clears before speaking.",
        AutoHide: false);

    public static DictationStatus Listening { get; } = new(
        DictationStatusKind.Listening,
        "Listening",
        "Release the hold-to-talk key to transcribe.",
        AutoHide: false);

    public static DictationStatus DictationCancelled { get; } = new(
        DictationStatusKind.Cancelled,
        "Dictation cancelled",
        "Recording and transcription were discarded. Nothing was pasted.",
        AutoHide: true);

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
