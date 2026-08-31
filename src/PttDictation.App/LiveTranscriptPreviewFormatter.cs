namespace PttDictation.App;

internal static class LiveTranscriptPreviewFormatter
{
    private const int DefaultMaximumCharacters = 52;

    public static string LatestWords(string? transcript, int maximumCharacters = DefaultMaximumCharacters)
    {
        var text = transcript?.Trim() ?? string.Empty;
        if (text.Length <= maximumCharacters || maximumCharacters < 2)
        {
            return text;
        }

        var start = text.Length - (maximumCharacters - 1);
        var firstSpace = text.IndexOf(' ', start);
        if (firstSpace >= 0 && firstSpace < text.Length - 1)
        {
            start = firstSpace + 1;
        }

        return $"…{text[start..]}";
    }
}
