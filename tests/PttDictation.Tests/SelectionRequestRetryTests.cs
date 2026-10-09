using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class SelectionRequestRetryTests
{
    [TestMethod]
    public void LateFirstAcknowledgementOnPreRetryReadNeedsNoSecondSelect()
    {
        var run = new Run();
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        var reads = 0;
        run.Surface.OnRead = () =>
        {
            if (++reads == 2) { run.Surface.Prefix = "prior text "; run.Surface.Selection = "preview"; }
        };
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(2, run.Surface.Pastes);
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
    }
    [TestMethod]
    public void AsynchronousResendAcknowledgementAfterDeadlineCannotPaste()
    {
        var run = new Run();
        run.Surface.IgnoreEverySelection = true;
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        run.Replace();
        run.Now += TimeSpan.FromSeconds(2);
        run.Surface.Prefix = "prior text ";
        run.Surface.Selection = "preview";
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void LateFirstAcknowledgementAfterDeadlineCannotPaste()
    {
        var run = new Run();
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        var reads = 0;
        run.Surface.OnRead = () =>
        {
            if (++reads != 2) return;
            run.Now += TimeSpan.FromSeconds(2);
            run.Surface.Prefix = "prior text ";
            run.Surface.Selection = "preview";
        };
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void IgnoredSelectionIsResentOnceBeforeOneVerifiedPaste()
    {
        var run = new Run();
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(1, run.Surface.Selects);
        run.Now += TimeSpan.FromMilliseconds(250);
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(2, run.Surface.Pastes, "One initial preview and one final paste, never a duplicate.");
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
        Assert.AreEqual("prior text final sentence.", run.Surface.Document);
    }

    [TestMethod]
    [DataRow("input")]
    [DataRow("focus-generation")]
    [DataRow("missing-input")]
    [DataRow("missing-focus")]
    [DataRow("unfocused")]
    [DataRow("document")]
    [DataRow("caret")]
    [DataRow("deadline")]
    public void ChangedOrUnverifiableIntentCannotResendOrPaste(string change)
    {
        var run = new Run(change != "missing-input", change != "missing-focus");
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromMilliseconds(250);
        switch (change)
        {
            case "input": run.Input++; break;
            case "focus-generation": run.Focus++; break;
            case "unfocused": run.Surface.IsFocused = false; break;
            case "document": run.Surface.Prefix += "edit"; break;
            case "caret": run.Surface.Selection = run.Surface.Prefix; run.Surface.Prefix = ""; break;
            case "deadline": run.Now += TimeSpan.FromSeconds(2); break;
        }
        run.Replace();
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void SecondIgnoredSelectionFailsAtOriginalDeadlineWithoutPasting()
    {
        var run = new Run();
        run.Surface.IgnoreEverySelection = true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromMilliseconds(250);
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromMilliseconds(1750);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void InputDuringFinalSnapshotCheckPreventsResend()
    {
        var run = new Run();
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        var reads = 0;
        run.Surface.OnRead = () => { if (++reads == 2) run.Input++; };
        run.Replace();
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void UnverifiedProviderCannotResend(int refresh)
    {
        var run = new Run();
        run.Surface.Refresh = (TextPatternRequeryResult)refresh;
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        run.Replace();
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void SlowResendCannotPasteAfterOriginalDeadline()
    {
        var run = new Run();
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        run.Surface.OnResend = () => run.Now += TimeSpan.FromSeconds(2);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void UnacknowledgedPasteIsNeverRepeated()
    {
        var run = new Run();
        run.Surface.IgnorePaste = true;
        run.Replace();
        run.Now += TimeSpan.FromMilliseconds(250);
        run.Replace();
        run.Now += TimeSpan.FromSeconds(2);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(2, run.Surface.Pastes);
        Assert.AreEqual(2, run.Surface.Selects);
    }

    private sealed class Run
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public long Input, Focus;
        public Surface Surface = new();
        public WindowsTextTarget Target;
        public Run(bool inputAvailable = true, bool focusAvailable = true)
        {
            Target = new WindowsTextTarget(Surface, () => Now,
                readFocusGeneration: focusAvailable ? () => Focus : null,
                readInputGeneration: inputAvailable ? () => Input : null);
            Assert.AreEqual(TextTargetUpdateResult.Pending, Target.TryReplace("", "preview", Surface.Paste));
            Assert.AreEqual(TextTargetUpdateResult.Success, Target.TryReplace("", "preview", Surface.Paste));
        }
        public TextTargetUpdateResult Replace() => Target.TryReplace("preview", "final sentence.", Surface.Paste);
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public string Prefix = "prior text ", Selection = "";
        public string Document => Prefix + Selection;
        public int Selects, Pastes;
        public bool IgnoreEverySelection;
        public bool IgnorePaste;
        public TextPatternRequeryResult Refresh = TextPatternRequeryResult.Refreshed;
        public Action? OnResend;
        public Action? OnRead;
        public bool IsFocused { get; set; } = true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read() { OnRead?.Invoke(); return new(Document, Prefix, Selection, ""); }
        public bool Select(string prefix, string owned, string suffix)
        {
            Selects++;
            if (Selects > 1)
            {
                OnResend?.Invoke();
                if (!IgnoreEverySelection) { Prefix = prefix; Selection = owned; }
            }
            return true;
        }
        public TextPatternRequeryResult RequerySelectionPattern() => Refresh;
        public void Paste(string text) { Pastes++; if (!IgnorePaste) { Prefix += text; Selection = ""; } }
        public void RevealCaret() { }
    }
}
