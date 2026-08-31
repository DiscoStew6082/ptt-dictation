namespace PttDictation.Core;

public sealed class SessionHistory
{
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

    public void Add(string transcript)
    {
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            _items.Add(transcript);
        }
    }
}
