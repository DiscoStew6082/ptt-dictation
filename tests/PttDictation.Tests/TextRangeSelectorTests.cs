using System.Globalization;
using System.Windows.Automation.Text;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class TextRangeSelectorTests
{
    [TestMethod]
    [DataRow("here is your", "Here is your reproduction.")]
    [DataRow("words 👋", "Revised 👋 words.")]
    [DataRow("line one\r\nline two", "Final line.")]
    [DataRow("e\u0301 and 👩‍💻", "Final Unicode.")]
    public void RevisionsAfterLinkPreservePrefixWhenProviderFindTextOvershoots(string preview, string final)
    {
        const string prefix = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base\n\n ";
        var document = new Document(prefix + preview) { OvershootingPrefix = prefix };
        Assert.IsTrue(TextRangeSelector.Select(document.WholeRange(), prefix, preview, "", () => true));
        Assert.AreEqual(preview, document.SelectedText);
        document.ReplaceSelected(final);
        Assert.AreEqual(prefix + final, document.Text);
        Assert.IsTrue(TextRangeSelector.Select(document.WholeRange(), prefix, final, "", () => true));
        Assert.AreEqual(final, document.SelectedText);
    }

    [TestMethod]
    public void RepeatedWordsInPrefixDoNotSelectEarlierUserText()
    {
        const string prefix = "words before words ";
        var document = new Document(prefix + "words");
        Assert.IsTrue(TextRangeSelector.Select(document.WholeRange(), prefix, "words", "", () => true));
        document.ReplaceSelected("final");
        Assert.AreEqual(prefix + "final", document.Text);
    }

    [TestMethod]
    public void InitialCollapsedCaretAndInteriorSelectionRemainSupported()
    {
        var document = new Document("link ");
        Assert.IsTrue(TextRangeSelector.Select(document.WholeRange(), "link ", "", "", () => true));
        document.ReplaceSelected("preview");
        Assert.AreEqual("link preview", document.Text);
        document = new Document("before selected after");
        Assert.IsTrue(TextRangeSelector.Select(document.WholeRange(), "before ", "selected", " after", () => true));
        document.ReplaceSelected("replacement");
        Assert.AreEqual("before replacement after", document.Text);
    }

    [TestMethod]
    public void ChangedDocumentOrFocusNeverSelectsText()
    {
        var document = new Document("edited prefix words");
        Assert.IsFalse(TextRangeSelector.Select(document.WholeRange(), "prefix ", "words", "", () => true));
        Assert.IsNull(document.SelectedText);
        Assert.IsFalse(TextRangeSelector.Select(document.WholeRange(), "edited prefix ", "words", "", () => false));
        Assert.IsNull(document.SelectedText);
    }

    [TestMethod]
    public void ProviderThatCannotMoveRangeDoesNotSelectOrLoopIndefinitely()
    {
        var document = new Document("link words") { CanMoveStart = false };
        Assert.IsFalse(TextRangeSelector.Select(document.WholeRange(), "link ", "words", "", () => true));
        Assert.IsNull(document.SelectedText);
    }

    [TestMethod]
    public void OwnedTextThatCannotBeSeparatedFromUserGraphemeIsNotSelected()
    {
        var document = new Document("e\u0301");
        Assert.IsFalse(TextRangeSelector.Select(document.WholeRange(), "e", "\u0301", "", () => true));
        Assert.IsNull(document.SelectedText);
    }

    private sealed class Document(string text)
    {
        public string Text = text;
        public string? OvershootingPrefix;
        public bool CanMoveStart = true;
        private Range? _selected;
        public string? SelectedText => _selected?.GetText();
        public Range WholeRange() => new(this, 0, Text.Length);
        public void ReplaceSelected(string replacement)
        {
            Assert.IsNotNull(_selected);
            Text = Text[.._selected.Start] + replacement + Text[_selected.End..];
            _selected = null;
        }

        public sealed class Range(Document document, int start, int end) : IAutomationTextRange
        {
            public int Start = start, End = end;
            public string GetText() => document.Text[Start..End];
            public IAutomationTextRange Clone() => new Range(document, Start, End);
            public IAutomationTextRange? FindText(string text, bool backward)
            {
                var offset = backward ? GetText().LastIndexOf(text, StringComparison.Ordinal)
                    : GetText().IndexOf(text, StringComparison.Ordinal);
                if (offset < 0) return null;
                // Recorded live failure: the full prefix is found, but its end
                // position consumes one character of the following dictation.
                var extra = text == document.OvershootingPrefix ? 1 : 0;
                return new Range(document, Start + offset, Math.Min(End, Start + offset + text.Length + extra));
            }
            private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? Start : End;
            public int CompareEndpoints(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint)
                => Endpoint(endpoint).CompareTo(((Range)other).Endpoint(otherEndpoint));
            public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, IAutomationTextRange other, TextPatternRangeEndpoint otherEndpoint)
            {
                var offset = ((Range)other).Endpoint(otherEndpoint);
                if (endpoint == TextPatternRangeEndpoint.Start) { Start = offset; End = Math.Max(End, Start); }
                else { End = offset; Start = Math.Min(Start, End); }
            }
            public int MoveStartByCharacters(int count)
            {
                if (!document.CanMoveStart) return 0;
                // Provider character units are graphemes, not UTF-16 offsets.
                var positions = StringInfo.ParseCombiningCharacters(document.Text).Append(document.Text.Length).ToArray();
                var index = Array.IndexOf(positions, Start);
                Assert.IsTrue(index >= 0);
                var next = Math.Clamp(index + count, 0, positions.Length - 1);
                Start = positions[next];
                End = Math.Max(Start, End);
                return next - index;
            }
            public void Select() => document._selected = this;
        }
    }
}
