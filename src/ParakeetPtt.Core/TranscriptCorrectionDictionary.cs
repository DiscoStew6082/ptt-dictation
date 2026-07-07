using System.Text.RegularExpressions;

namespace ParakeetPtt.Core;

public sealed record TranscriptCorrection(string HeardAs, string ReplaceWith);

public sealed class TranscriptCorrectionDictionary(IReadOnlyList<TranscriptCorrection> corrections)
{
    public static TranscriptCorrectionDictionary Empty { get; } = new([]);

    public IReadOnlyList<TranscriptCorrection> Corrections { get; } = corrections;

    public string Apply(string? transcript)
    {
        var corrected = transcript ?? string.Empty;
        foreach (var correction in Corrections
            .Where(correction => !string.IsNullOrWhiteSpace(correction.HeardAs))
            .OrderByDescending(correction => correction.HeardAs.Length))
        {
            corrected = Regex.Replace(
                corrected,
                PatternFor(correction.HeardAs),
                _ => correction.ReplaceWith,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return corrected;
    }

    public static string Apply(string? transcript, IReadOnlyList<TranscriptCorrection>? corrections)
    {
        return corrections is { Count: > 0 }
            ? new TranscriptCorrectionDictionary(corrections).Apply(transcript)
            : transcript ?? string.Empty;
    }

    private static string PatternFor(string heardAs)
    {
        var tokens = Regex.Split(heardAs.Trim(), @"\s+")
            .Where(token => token.Length > 0)
            .Select(Regex.Escape);
        return $@"(?<![\p{{L}}\p{{N}}_]){string.Join(@"\s+", tokens)}(?![\p{{L}}\p{{N}}_])";
    }
}
