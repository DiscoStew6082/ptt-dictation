using System.Text.RegularExpressions;

namespace PttDictation.Core;

public static partial class TranscriptNormalizer
{
    public static string Normalize(string? transcript)
    {
        var text = Whitespace().Replace(transcript ?? string.Empty, " ").Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = char.ToUpperInvariant(text[0]) + text[1..];

        if (!IsTerminalPunctuation(text[^1]))
        {
            text += ".";
        }

        return text;
    }

    private static bool IsTerminalPunctuation(char value)
    {
        return value is '.' or '!' or '?';
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
