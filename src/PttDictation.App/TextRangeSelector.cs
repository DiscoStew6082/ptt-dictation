using System.Windows.Automation.Text;
using PttDictation.Core;

namespace PttDictation.App;

internal interface IAutomationTextRange
{
    string GetText();
    IAutomationTextRange Clone();
    IAutomationTextRange? FindText(string text, bool backward);
    int CompareEndpoints(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint);
    void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint);
    int MoveStartByCharacters(int count);
    void Select();
}

internal sealed class AutomationTextRange(TextPatternRange range) : IAutomationTextRange
{
    private readonly TextPatternRange _range = range;
    public string GetText() => _range.GetText(-1);
    public IAutomationTextRange Clone() => new AutomationTextRange(_range.Clone());
    public IAutomationTextRange? FindText(string text, bool backward)
        => _range.FindText(text, backward, ignoreCase: false) is { } found ? new AutomationTextRange(found) : null;
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint)
        => _range.CompareEndpoints(endpoint, ((AutomationTextRange)other)._range, otherEndpoint);
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint)
        => _range.MoveEndpointByRange(endpoint, ((AutomationTextRange)other)._range, otherEndpoint);
    public int MoveStartByCharacters(int count)
        => _range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, count);
    public void Select() => _range.Select();
}

internal static class TextRangeSelector
{
    public static bool Select(IAutomationTextRange document, string prefix, string ownedText, string suffix, Func<bool> isFocused)
    {
        if (document.GetText() != prefix + ownedText + suffix) return Reject("document_changed");
        var owned = document.Clone();
        if (suffix.Length == 0)
        {
            // FindText(prefix) can end one character beyond a pasted link's
            // boundary. Anchor trailing dictation at the document end instead.
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.End);
            if (!ExtendStartToText(owned, ownedText)) return Reject("trailing_range_not_found");
        }
        else if (prefix.Length > 0)
        {
            var before = document.FindText(prefix, backward: false);
            if (before is null || before.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) != 0)
                return Reject("prefix_range_not_found");
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.Start, before, TextPatternRangeEndpoint.End);
        }
        if (suffix.Length > 0)
        {
            var after = document.FindText(suffix, backward: true);
            if (after is null || after.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) != 0)
                return Reject("suffix_range_not_found");
            owned.MoveEndpointByRange(TextPatternRangeEndpoint.End, after, TextPatternRangeEndpoint.Start);
        }
        if (owned.GetText() != ownedText || !isFocused()) return Reject("owned_text_or_focus_changed");
        // Verify the provider's resulting partition, not just the search string
        // or a character count, before selecting anything in the editor.
        var beforeOwned = document.Clone();
        beforeOwned.MoveEndpointByRange(TextPatternRangeEndpoint.End, owned, TextPatternRangeEndpoint.Start);
        var afterOwned = document.Clone();
        afterOwned.MoveEndpointByRange(TextPatternRangeEndpoint.Start, owned, TextPatternRangeEndpoint.End);
        if (beforeOwned.GetText() != prefix || afterOwned.GetText() != suffix
            || document.GetText() != prefix + ownedText + suffix || !isFocused())
            return Reject("range_partition_changed");
        owned.Select();
        return true;
    }

    private static bool ExtendStartToText(IAutomationTextRange range, string expected)
    {
        if (expected.Length > 0) range.MoveStartByCharacters(-expected.Length);
        // UIA character units need not equal UTF-16 units (emoji, combining
        // marks, CRLF). The initial count is only a hint; exact text decides.
        for (var attempts = 0; attempts <= expected.Length; attempts++)
        {
            var actual = range.GetText();
            if (actual == expected) return true;
            if (actual.Length == expected.Length) return false;
            var direction = actual.Length > expected.Length ? 1 : -1;
            if (range.MoveStartByCharacters(direction) == 0) return false;
        }
        return false;
    }

    private static bool Reject(string reason)
    {
        DiagnosticTrace.Write("target.select_rejected", new { reason });
        return false;
    }
}
