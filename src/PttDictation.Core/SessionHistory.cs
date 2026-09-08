namespace PttDictation.Core;

public sealed class SessionHistory
{
    private readonly List<string> _items = [];
    private readonly List<SessionHistoryEntry> _entries = [];

    public IReadOnlyList<string> Items => _items;
    public IReadOnlyList<SessionHistoryEntry> Entries => _entries;

    public void Add(string transcript, TranscriptComparison? comparison = null)
    {
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            _items.Add(transcript);
            _entries.Add(new SessionHistoryEntry(transcript, comparison));
        }
    }
}

public sealed record SessionHistoryEntry(string Transcript, TranscriptComparison? Comparison);

public sealed record TranscriptComparison(
    string RawPreview,
    string LastPreview,
    string RawFinal,
    string CorrectedFinal,
    string FinalText);
