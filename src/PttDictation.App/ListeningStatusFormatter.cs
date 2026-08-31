namespace PttDictation.App;

internal static class ListeningStatusFormatter
{
    public static string Format(TimeSpan elapsed, ListeningTriggerMode mode = ListeningTriggerMode.PushToTalk)
    {
        return $"{FormatElapsed(elapsed)}{Environment.NewLine}{FormatHint(mode)}";
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        var clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return $"Recording {(int)clamped.TotalMinutes:00}:{clamped.Seconds:00}";
    }

    public static string FormatHint(ListeningTriggerMode mode)
    {
        return mode == ListeningTriggerMode.Toggle
            ? "Press Right Shift to transcribe"
            : "Release to transcribe";
    }
}

internal enum ListeningTriggerMode
{
    PushToTalk,
    Toggle
}
