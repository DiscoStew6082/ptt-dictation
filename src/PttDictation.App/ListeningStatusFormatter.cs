namespace PttDictation.App;

internal static class ListeningStatusFormatter
{
    public static string Format(
        TimeSpan elapsed,
        ListeningTriggerMode mode = ListeningTriggerMode.PushToTalk,
        string? hotkeyName = null)
    {
        return $"{FormatElapsed(elapsed)}{Environment.NewLine}{FormatHint(mode, hotkeyName)}";
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        var clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return $"Recording {(int)clamped.TotalMinutes:00}:{clamped.Seconds:00}";
    }

    public static string FormatHint(ListeningTriggerMode mode, string? hotkeyName = null)
    {
        if (mode == ListeningTriggerMode.Toggle)
        {
            return $"Press {hotkeyName ?? "Right Shift"} to transcribe";
        }

        return string.IsNullOrWhiteSpace(hotkeyName)
            ? "Release to transcribe"
            : $"Release {hotkeyName} to transcribe";
    }
}

internal enum ListeningTriggerMode
{
    PushToTalk,
    Toggle
}
