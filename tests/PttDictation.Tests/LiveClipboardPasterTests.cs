using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveClipboardPasterTests
{
    [TestMethod]
    public async Task RevisedPreviewAndFinalReplaceOneOwnedRangeWithoutDuplicatePaste()
    {
        var target = new Target();
        var writes = new List<string>();
        using var output = Create(target, writes);
        output.CaptureTarget();
        output.UpdatePreview("wrong words");
        output.Pump();
        output.UpdatePreview("correct words");
        output.Pump(); // acknowledge earlier write, not newest preview
        output.Pump();
        output.Pump();
        var finish = output.PasteAsync("Correct final words.", CancellationToken.None);
        output.Pump();
        await finish;
        CollectionAssert.AreEqual(new[] { "wrong words", "correct words", "Correct final words." }, writes);
        Assert.AreEqual("Correct final words.", target.Text);
    }

    [TestMethod]
    public async Task FocusLossKeepsFinalTextUntilReturningWithoutStealingFocus()
    {
        var target = new Target();
        var writes = new List<string>();
        using var output = Create(target, writes);
        output.CaptureTarget();
        output.UpdatePreview("first words");
        target.Focused = false;
        output.Pump();
        var finish = output.PasteAsync("A whole minute of completed speech.", CancellationToken.None);
        output.Pump();
        Assert.IsFalse(finish.IsCompleted);
        Assert.AreEqual(0, writes.Count);
        target.Focused = true;
        output.Pump();
        output.Pump();
        await finish;
        CollectionAssert.AreEqual(new[] { "A whole minute of completed speech." }, writes);
    }

    [TestMethod]
    public async Task UnsupportedFieldUsesOneFinalPasteOnlyAfterFocusReturns()
    {
        var target = new Target { Supported = false };
        var writes = new List<string>();
        using var output = Create(target, writes);
        output.CaptureTarget();
        output.UpdatePreview("preview");
        output.Pump();
        Assert.AreEqual(0, writes.Count);
        target.Focused = false;
        var finish = output.PasteAsync("Final.", CancellationToken.None);
        Assert.IsFalse(finish.IsCompleted);
        target.Focused = true;
        output.Pump();
        output.Pump();
        await finish;
        CollectionAssert.AreEqual(new[] { "Final." }, writes);
    }

    [TestMethod]
    public async Task ConflictAfterLiveWriteNeverAppendsFallbackTranscript()
    {
        var target = new Target();
        var writes = new List<string>();
        using var output = Create(target, writes);
        output.CaptureTarget();
        output.UpdatePreview("already inserted");
        output.Pump();
        output.Pump();
        target.Conflict = true;
        var finish = output.PasteAsync("final replacement", CancellationToken.None);
        output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await finish);
        CollectionAssert.AreEqual(new[] { "already inserted" }, writes);
    }

    [TestMethod]
    public void EndSessionPreventsStalePreviewWritingIntoNextTextbox()
    {
        var target = new Target();
        var writes = new List<string>();
        using var output = Create(target, writes);
        output.CaptureTarget();
        output.UpdatePreview("pending old words");
        output.EndSession();
        output.Pump();
        output.CaptureTarget();
        output.Pump();
        Assert.AreEqual(0, writes.Count);
    }

    private static LiveClipboardPaster Create(Target target, List<string> writes) =>
        new(() => target, (text, focused) =>
        {
            Assert.IsTrue(focused());
            writes.Add(text);
        });

    [TestMethod]
    public async Task ChangedFieldDuringCaptureRetainsResultWithoutWritingNewField()
    {
        var unchanged = true;
        var writes = 0;
        using var output = new LiveClipboardPaster(() => { unchanged = false; return new Target(); },
            (_, _) => writes++, beginCaptureGuard: () => () => unchanged);
        output.CaptureTarget();
        output.UpdatePreview("preserve me");
        output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.PasteAsync("Preserve me.", CancellationToken.None));
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public async Task BlockedModifiersDeferInsertionWithoutLosingFinalResult()
    {
        var ready = false;
        var writes = new List<string>();
        using var output = new LiveClipboardPaster(() => new Target(), (text, _) => writes.Add(text),
            canPaste: () => ready);
        output.CaptureTarget();
        var finish = output.PasteAsync("Words.", CancellationToken.None);
        output.Pump();
        Assert.IsFalse(finish.IsCompleted);
        Assert.AreEqual(0, writes.Count);
        ready = true;
        output.Pump();
        output.Pump();
        await finish;
        CollectionAssert.AreEqual(new[] { "Words." }, writes);
    }

    private sealed class Target : IWindowsTextTarget
    {
        private string? _pending;
        public bool Focused { get; set; } = true;
        public bool Supported { get; init; } = true;
        public bool Conflict { get; set; }
        public string Text { get; private set; } = "";
        public bool IsFocused => Focused;
        public bool IsPreparedSelectionCurrent => Focused && !Conflict;
        public bool SupportsReplacement => Supported;
        public bool HasAttemptedWrite { get; private set; }
        public bool CanPasteFallback => true;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
        {
            if (!Focused) return TextTargetUpdateResult.Unfocused;
            if (Conflict) return TextTargetUpdateResult.Conflict;
            if (_pending is not null)
            {
                Text = _pending;
                _pending = null;
                return TextTargetUpdateResult.Success;
            }
            Assert.AreEqual(Text, previousText);
            if (Text == replacementText) return TextTargetUpdateResult.Success;
            HasAttemptedWrite = true;
            _pending = replacementText;
            paste(replacementText);
            return TextTargetUpdateResult.Pending;
        }
    }
}
