using System.Windows.Automation;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class SelectionPatternRequeryTests
{
    [TestMethod]
    public void RequeriedPatternAcknowledgesExistingSelectionWithoutSelectingAgain()
    {
        var run = new Harness();
        run.Surface.Requery = () => { run.Surface.AcknowledgeSelection(); return true; };

        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(2, run.Surface.Pastes);
        Assert.AreEqual(2, run.Surface.Selects, "Requery must only read the already-requested selection.");
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
        Assert.AreEqual("before revised words after", run.Surface.Document);
    }

    [TestMethod]
    public void UnchangedRequeryKeepsOriginalDeadlineAndRunsOnlyOncePerRequest()
    {
        var run = new Harness();
        run.Surface.Requery = () => true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromMilliseconds(1900);
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromMilliseconds(100);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void AlreadyExpiredSelectionDoesNotRequeryOrExtendDeadline()
    {
        var run = new Harness();
        // Selection time starts after Select returns. Advance the clock during
        // its following Read to model a slow provider observation instead.
        run.Surface.AfterSelect = () => run.Surface.AfterRead = () => run.Now += TimeSpan.FromSeconds(2);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(0, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void SlowRequeryCannotExtendOriginalSelectionDeadline()
    {
        var run = new Harness();
        run.Surface.Requery = () =>
        {
            run.Now += TimeSpan.FromSeconds(2);
            run.Surface.AcknowledgeSelection();
            return true;
        };
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
        Assert.AreEqual(2, run.Surface.Selects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RequeriedDocumentOrCaretMismatchCannotAuthorizePaste(bool editDocument)
    {
        var run = new Harness();
        run.Surface.Requery = () =>
        {
            if (editDocument) run.Surface.Prefix += " user edit";
            else
            {
                run.Surface.Suffix = run.Surface.Document;
                run.Surface.Prefix = run.Surface.Selection = "";
            }
            return true;
        };
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
        Assert.AreEqual(2, run.Surface.Selects);
    }

    [TestMethod]
    public void ChangedDocumentBeforeSelectionDoesNotTriggerRequery()
    {
        var run = new Harness();
        run.Surface.Prefix += " user edit";
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(0, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Selects);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void FocusLostAfterSuccessfulRequeryCannotReceivePaste()
    {
        var run = new Harness();
        run.Surface.Requery = () =>
        {
            run.Surface.AcknowledgeSelection();
            run.Surface.IsFocused = false;
            return true;
        };
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FocusLostDuringRejectedRequeryRemainsResumableWithoutReselectingOrExtendingDeadline(bool acknowledgeAfterReturn)
    {
        var run = new Harness();
        run.Surface.Requery = () =>
        {
            run.Surface.IsFocused = false;
            return false;
        };
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, run.Replace(),
            "A failed pattern query caused by focus loss must hold the original target rather than retire it.");
        Assert.AreEqual(1, run.Surface.Pastes);
        run.Now += TimeSpan.FromMilliseconds(1900);
        run.Surface.IsFocused = true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(1, run.Surface.Pastes, "Focus return alone cannot acknowledge the requested selection.");
        if (acknowledgeAfterReturn)
        {
            run.Surface.AcknowledgeSelection();
            Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
            Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
            Assert.AreEqual("before revised words after", run.Surface.Document);
            Assert.AreEqual(2, run.Surface.Pastes);
        }
        else
        {
            run.Now += TimeSpan.FromMilliseconds(100);
            Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
            Assert.AreEqual(1, run.Surface.Pastes);
        }
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(2, run.Surface.Selects);
    }

    [TestMethod]
    public void UnsupportedRequeryRemainsPendingOnlyUntilOriginalDeadline()
    {
        var run = new Harness();
        run.Surface.RequeryUnsupported = true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Now += TimeSpan.FromSeconds(2);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void UnsupportedRequeryStillAllowsOrdinarySelectionAcknowledgement()
    {
        var run = new Harness();
        run.Surface.RequeryUnsupported = true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        run.Surface.AcknowledgeSelection();
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(2, run.Surface.Selects);
        Assert.AreEqual(2, run.Surface.Pastes);
    }

    [TestMethod]
    public void RejectedRequeryCannotContinueUsingOldPattern()
    {
        var run = new Harness();
        run.Surface.Requery = () => false;
        Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RequeryFailureDoesNotCauseAnotherRefreshOrPaste(bool unavailable)
    {
        var run = new Harness();
        run.Surface.Requery = () =>
        {
            if (unavailable) throw new ElementNotAvailableException();
            throw new InvalidOperationException("Provider failed.");
        };
        if (unavailable) Assert.Throws<TextTargetUnavailableException>(() => run.Replace());
        else Assert.AreEqual(TextTargetUpdateResult.Conflict, run.Replace());
        Assert.AreEqual(1, run.Surface.Requeries);
        Assert.AreEqual(1, run.Surface.Pastes);
    }

    [TestMethod]
    public void LaterSelectionHasItsOwnRequeryBudget()
    {
        var run = new Harness();
        run.Surface.Requery = () => { run.Surface.AcknowledgeSelection(); return true; };
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
        Assert.AreEqual(TextTargetUpdateResult.Pending,
            run.Target.TryReplace("revised words", "final words", run.Surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success,
            run.Target.TryReplace("revised words", "final words", run.Surface.Paste));
        Assert.AreEqual(2, run.Surface.Requeries);
        Assert.AreEqual(3, run.Surface.Selects);
        Assert.AreEqual(3, run.Surface.Pastes);
        Assert.AreEqual("before final words after", run.Surface.Document);
    }

    [TestMethod]
    public void SelectionRequeryDoesNotConsumeUnavailableReferenceRecoveryBudget()
    {
        var run = new Harness();
        run.Surface.Requery = () => { run.Surface.AcknowledgeSelection(); return true; };
        Assert.AreEqual(TextTargetUpdateResult.Pending, run.Replace());
        Assert.AreEqual(TextTargetUpdateResult.Success, run.Replace());
        run.Surface.ThrowOnNextRead = true;
        run.Surface.Requery = () => true;
        Assert.AreEqual(TextTargetUpdateResult.Success,
            run.Target.TryReplace("revised words", "revised words", run.Surface.Paste));
        Assert.AreEqual(2, run.Surface.Requeries);
        Assert.AreEqual(2, run.Surface.Pastes);
    }

    private sealed class Harness
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public Surface Surface { get; } = new();
        public WindowsTextTarget Target { get; }
        public Harness()
        {
            Target = new WindowsTextTarget(Surface, () => Now);
            Assert.AreEqual(TextTargetUpdateResult.Pending, Target.TryReplace("", "first words", Surface.Paste));
            Assert.AreEqual(TextTargetUpdateResult.Success, Target.TryReplace("", "first words", Surface.Paste));
            Surface.StaleSelection = true;
        }
        public TextTargetUpdateResult Replace() => Target.TryReplace("first words", "revised words", Surface.Paste);
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public string Prefix = "before ", Selection = "", Suffix = " after";
        public string Document => Prefix + Selection + Suffix;
        public bool IsFocused { get; set; } = true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public bool StaleSelection, ThrowOnNextRead, RequeryUnsupported;
        public int Selects, Pastes, Requeries;
        public Func<bool> Requery = () => true;
        public Action? AfterSelect, AfterRead;
        private TextTargetSnapshot? _requested;
        public TextTargetSnapshot Read()
        {
            if (ThrowOnNextRead) { ThrowOnNextRead = false; throw new ElementNotAvailableException(); }
            var snapshot = new TextTargetSnapshot(Document, Prefix, Selection, Suffix);
            var afterRead = AfterRead;
            AfterRead = null;
            afterRead?.Invoke();
            return snapshot;
        }
        public bool Select(string prefix, string ownedText, string suffix)
        {
            Selects++;
            _requested = new(Document, prefix, ownedText, suffix);
            if (!StaleSelection) AcknowledgeSelection();
            AfterSelect?.Invoke();
            return true;
        }
        public void AcknowledgeSelection()
        {
            Prefix = _requested!.Prefix;
            Selection = _requested.Selection;
            Suffix = _requested.Suffix;
        }
        public bool TryRefreshOriginalReference() { Requeries++; return Requery(); }
        public TextPatternRequeryResult RequerySelectionPattern()
        {
            if (RequeryUnsupported) { Requeries++; return TextPatternRequeryResult.NotSupported; }
            return TryRefreshOriginalReference()
                ? TextPatternRequeryResult.Refreshed : TextPatternRequeryResult.Rejected;
        }
        public void Paste(string text)
        {
            Assert.IsTrue(IsFocused);
            Pastes++;
            Prefix += text;
            Selection = "";
        }
        public void RevealCaret() { }
    }
}
