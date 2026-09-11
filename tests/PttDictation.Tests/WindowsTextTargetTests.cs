using System.Windows.Automation;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class WindowsTextTargetTests
{
    [TestMethod]
    public void UnavailableAutomationReferenceDoesNotClaimTheVisibleTextboxDisappeared()
    {
        var surface = new FakeSurface("before ", "", " after");
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first words", surface.Paste));

        // The editor and its content remain visible and focused. Only the
        // previously captured UI Automation reference has become inaccessible.
        surface.AutomationAvailable = false;
        var error = Assert.Throws<InvalidOperationException>(
            () => target.TryReplace("first words", "revised words", surface.Paste));

        StringAssert.Contains(error.Message, "automation reference");
        StringAssert.Contains(error.Message, "may still be visible");
        Assert.IsInstanceOfType<ElementNotAvailableException>(error.InnerException);
        Assert.AreEqual("before first words after", surface.Document);
        Assert.AreEqual(1, surface.Pastes, "An inaccessible identity must never authorize another paste.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnavailableTextPatternReportsReferenceLossBeforeAnotherPaste(bool failWhileSelecting)
    {
        var surface = new FakeSurface("before ", "", " after");
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first words", surface.Paste));
        surface.ReadAvailable = failWhileSelecting;
        surface.SelectionAvailable = !failWhileSelecting;

        var error = Assert.Throws<InvalidOperationException>(
            () => target.TryReplace("first words", "revised words", surface.Paste));

        StringAssert.Contains(error.Message, "automation reference");
        StringAssert.Contains(error.Message, "may still be visible");
        Assert.IsInstanceOfType<ElementNotAvailableException>(error.InnerException);
        Assert.AreEqual("before first words after", surface.Document);
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferenceLossDuringFinalSelectionCheckPreservesErrorAndDoesNotPaste(bool failFocusRead)
    {
        var surface = new FakeSurface("before ", "selected", " after");
        var target = new WindowsTextTarget(surface);

        var error = Assert.Throws<InvalidOperationException>(() => target.TryReplace("", "speech", text =>
        {
            // Clipboard preparation can outlive the provider reference, even
            // after selecting the right range. The final guard must still fail.
            surface.AutomationAvailable = !failFocusRead;
            surface.ReadAvailable = failFocusRead;
            if (target.IsPreparedSelectionCurrent) surface.Paste(text);
        }));

        StringAssert.Contains(error.Message, "automation reference");
        Assert.IsInstanceOfType<ElementNotAvailableException>(error.InnerException);
        Assert.AreEqual("before selected after", surface.Document);
        Assert.AreEqual(0, surface.Pastes);
    }

    [TestMethod]
    [DataRow("", "Do anything\n")]
    [DataRow("Do anything", "\n")]
    public void FirstInsertionAcknowledgesDisappearingPlaceholderWithoutLosingLaterRevisions(string prefix, string suffix)
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface(prefix, "", suffix) { DiscardSurroundingsOnFirstPaste = true };
        var target = new WindowsTextTarget(surface, () => now);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual("revised words", surface.Document);
        Assert.AreEqual(2, surface.Pastes);
    }

    [TestMethod]
    [DataRow("user text ", "", "")]
    [DataRow("", "", " user text")]
    [DataRow("", "first words", "")]
    public void UnexpectedFirstPasteContentsOrSelectionAreNotAdopted(string prefix, string selection, string suffix)
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface("", "", "Do anything\n") { DiscardSurroundingsOnFirstPaste = true };
        var target = new WindowsTextTarget(surface, () => now);
        target.TryReplace("", "first words", surface.Paste);
        surface.Prefix = prefix + (selection.Length == 0 ? "first words" : "");
        surface.Selection = selection;
        surface.Suffix = suffix;
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }
    [TestMethod]
    public void DisappearingSurroundingsWithOriginalSelectionAreNotAdopted()
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface("before ", "selected", " after") { DiscardSurroundingsOnFirstPaste = true };
        var target = new WindowsTextTarget(surface, () => now);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual("first words", surface.Document);
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void DisappearingSurroundingsAfterAcknowledgedWriteAreNotAdopted()
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface("before ", "", " after");
        var target = new WindowsTextTarget(surface, () => now);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        surface.Prefix = "revised words";
        surface.Suffix = "";
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(2, surface.Pastes);
    }

    [TestMethod]
    public void RevisionsReplaceOnlyDictatedRangeAndPreserveSurroundingUnicodeText()
    {
        var surface = new FakeSurface("Before 👋\r\n", "replace me", "\r\nAfter");
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first guess", surface.Paste));
        Assert.AreEqual("Before 👋\r\nfirst guess\r\nAfter", surface.Document);
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first guess", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first guess", "corrected words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("first guess", "corrected words", surface.Paste));
        Assert.AreEqual("Before 👋\r\ncorrected words\r\nAfter", surface.Document);
        Assert.AreEqual(2, surface.Pastes);
    }

    [TestMethod]
    public void ReturningToOriginalFieldResumesWithoutRepeatingPaste()
    {
        var surface = new FakeSurface("before ", "", " after");
        var target = new WindowsTextTarget(surface);
        surface.IsFocused = false;
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, target.TryReplace("", "held words", surface.Paste));
        Assert.AreEqual(0, surface.Pastes);
        surface.IsFocused = true;
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "held words", surface.Paste));
        surface.IsFocused = false;
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, target.TryReplace("", "held words", surface.Paste));
        surface.IsFocused = true;
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "held words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void ChangedSurroundingTextPreventsAnyWrite()
    {
        var surface = new FakeSurface("before ", "selected", " after");
        var target = new WindowsTextTarget(surface);
        surface.Prefix = "edited before ";
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual("edited before selected after", surface.Document);
        Assert.AreEqual(0, surface.Pastes);
    }

    [TestMethod]
    public void UserEditsToOwnedTextPreventOverwritingTheEdit()
    {
        var surface = new FakeSurface("before ", "", " after");
        var target = new WindowsTextTarget(surface);
        target.TryReplace("", "speech", surface.Paste);
        target.TryReplace("", "speech", surface.Paste);
        surface.Prefix = "before user edit";
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("speech", "new speech", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void FocusLostDuringSelectionNeverPastesIntoAnotherField()
    {
        var surface = new FakeSurface("before", "", "after") { LoseFocusDuringSelection = true };
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(0, surface.Pastes);
        Assert.IsFalse(target.HasAttemptedWrite);
    }

    [TestMethod]
    public void ProviderSelectingWrongRangeCannotEraseOutsideContent()
    {
        var surface = new FakeSurface("before ", "selected", " after") { SelectWrongRange = true };
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(0, surface.Pastes);
        Assert.AreEqual("before selected after", surface.Document);
    }

    [TestMethod]
    public void DelayedSelectionMustBeAcknowledgedBeforePasting()
    {
        var surface = new FakeSurface("before ", "", " after") { DelaySelection = true };
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "first words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
        Assert.AreEqual(2, surface.SelectCalls, "Polling must not issue another selection request.");
        surface.ApplyDelayedSelection();
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual("before revised words after", surface.Document);
        Assert.AreEqual(2, surface.Pastes);
        Assert.AreEqual(2, surface.SelectCalls);
    }

    [TestMethod]
    public void UnacknowledgedSelectionTimesOutWithoutPasting()
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface("before ", "", " after") { DelaySelection = true };
        var target = new WindowsTextTarget(surface, () => now);
        target.TryReplace("", "first words", surface.Paste);
        target.TryReplace("", "first words", surface.Paste);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void ChangedCaretWhileAwaitingSelectionCannotReceivePaste()
    {
        var surface = new FakeSurface("before ", "", " after") { DelaySelection = true };
        var target = new WindowsTextTarget(surface);
        target.TryReplace("", "first words", surface.Paste);
        target.TryReplace("", "first words", surface.Paste);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("first words", "revised words", surface.Paste));
        surface.Suffix = surface.Document;
        surface.Prefix = "";
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("first words", "revised words", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void DelayedEditorAcknowledgementDoesNotDuplicateInput()
    {
        var surface = new FakeSurface("", "", "") { DelayPaste = true };
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
        surface.ApplyDelayedPaste();
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(1, surface.Pastes);
        Assert.AreEqual("speech", surface.Document);
    }

    [TestMethod]
    public void UnacknowledgedPasteBecomesConflictWithoutRetrying()
    {
        var now = DateTimeOffset.UtcNow;
        var surface = new FakeSurface("", "", "") { DelayPaste = true };
        var target = new WindowsTextTarget(surface, () => now);
        Assert.AreEqual(TextTargetUpdateResult.Pending, target.TryReplace("", "speech", surface.Paste));
        now += TimeSpan.FromSeconds(3);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, target.TryReplace("", "speech", surface.Paste));
        Assert.IsTrue(target.HasAttemptedWrite);
        Assert.AreEqual(1, surface.Pastes);
    }

    [TestMethod]
    public void UnsupportedEditorStillTracksExactFieldFocus()
    {
        var surface = new FakeSurface("", "", "") { SupportsReplacement = false };
        var target = new WindowsTextTarget(surface);
        Assert.IsFalse(target.SupportsReplacement);
        Assert.AreEqual(TextTargetUpdateResult.Unsupported, target.TryReplace("", "speech", surface.Paste));
        surface.IsFocused = false;
        Assert.IsFalse(target.IsFocused);
        Assert.AreEqual(TextTargetUpdateResult.Unfocused, target.TryReplace("", "speech", surface.Paste));
        Assert.AreEqual(0, surface.Pastes);
    }

    [TestMethod]
    public void ReadOnlyPasswordDisabledAndNonEditableFieldsCannotReceiveFallbackPaste()
    {
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, true, true, false, false));
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(false, false, true, false, false));
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, false, true, true, false));
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, false, true, false, true));
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, false, false, null, null));
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, false, true, null, new object()));
        Assert.IsTrue(AutomationTextSurface.AllowsFallback(true, false, true, null, null));
        Assert.IsTrue(AutomationTextSurface.AllowsFallback(true, false, false, false, null));
        Assert.IsTrue(AutomationTextSurface.AllowsFallback(true, false, false, null, false));
        var target = new WindowsTextTarget(new FakeSurface("", "", "") { SupportsReplacement = false, CanPasteFallback = false });
        Assert.IsFalse(target.CanPasteFallback);
    }

    [TestMethod]
    public void EmptyInitialPreviewLeavesOriginalSelectionIntact()
    {
        var surface = new FakeSurface("before ", "selected", " after");
        var target = new WindowsTextTarget(surface);
        Assert.AreEqual(TextTargetUpdateResult.Success, target.TryReplace("", "", surface.Paste));
        Assert.AreEqual("before selected after", surface.Document);
        Assert.AreEqual(0, surface.Pastes);
    }

    [TestMethod]
    public void SelectionChangedDuringClipboardPreparationCannotReceivePaste()
    {
        var surface = new FakeSurface("before ", "selected", " after");
        var target = new WindowsTextTarget(surface);
        target.TryReplace("", "speech", text =>
        {
            Assert.IsTrue(target.IsPreparedSelectionCurrent);
            // Same document and same focused field, but the user moves the caret.
            surface.Prefix = surface.Document;
            surface.Selection = "";
            surface.Suffix = "";
            Assert.IsFalse(target.IsPreparedSelectionCurrent);
            if (target.IsPreparedSelectionCurrent) surface.Paste(text);
        });
        Assert.AreEqual("before selected after", surface.Document);
        Assert.AreEqual(0, surface.Pastes);
    }

    [TestMethod]
    public void ReadableFallbackSelectionMustStillMatchOriginalSelection()
    {
        var surface = new FakeSurface("before ", "selected", " after") { SupportsReplacement = false };
        var target = new WindowsTextTarget(surface);
        Assert.IsTrue(target.CanPasteFallback);
        Assert.IsTrue(target.IsPreparedSelectionCurrent);
        surface.Selection += surface.Suffix;
        surface.Suffix = "";
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
        Assert.AreEqual("before selected after", surface.Document);
    }

    private sealed class FakeSurface(string prefix, string selection, string suffix) : IWindowsTextSurface
    {
        public string Prefix { get; set; } = prefix;
        public string Selection { get; set; } = selection;
        public string Suffix { get; set; } = suffix;
        public string Document => Prefix + Selection + Suffix;
        public bool AutomationAvailable { get; set; } = true;
        public bool ReadAvailable { get; set; } = true;
        public bool SelectionAvailable { get; set; } = true;
        private bool _focused = true;
        public bool IsFocused
        {
            get => AutomationAvailable ? _focused : throw new ElementNotAvailableException();
            set => _focused = value;
        }
        public bool SupportsReplacement { get; set; } = true;
        public bool CanPasteFallback { get; set; } = true;
        public bool LoseFocusDuringSelection { get; init; }
        public bool SelectWrongRange { get; init; }
        public bool DelayPaste { get; init; }
        public bool DelaySelection { get; init; }
        public bool DiscardSurroundingsOnFirstPaste { get; init; }
        public int Pastes { get; private set; }
        public int SelectCalls { get; private set; }
        private string? _delayed;
        private (string Before, string Owned, string After)? _delayedSelection;
        public TextTargetSnapshot Read() => ReadAvailable
            ? new(Document, Prefix, Selection, Suffix)
            : throw new ElementNotAvailableException();
        public bool Select(string before, string owned, string after)
        {
            if (!SelectionAvailable) throw new ElementNotAvailableException();
            SelectCalls++;
            if (Document != before + owned + after) return false;
            if (DelaySelection)
            {
                _delayedSelection = (before, owned, after);
                return true;
            }
            Prefix = before;
            Selection = owned;
            Suffix = after;
            if (SelectWrongRange) { Selection = Document; Prefix = ""; Suffix = ""; }
            if (LoseFocusDuringSelection) IsFocused = false;
            return true;
        }
        public void Paste(string text)
        {
            Assert.IsTrue(IsFocused, "Pasting into the wrong field is forbidden.");
            Pastes++;
            if (DelayPaste) _delayed = text;
            else Apply(text);
        }
        public void ApplyDelayedPaste() => Apply(_delayed!);
        public void ApplyDelayedSelection()
        {
            var selection = _delayedSelection!.Value;
            Prefix = selection.Before;
            Selection = selection.Owned;
            Suffix = selection.After;
            _delayedSelection = null;
        }
        private void Apply(string text)
        {
            if (DiscardSurroundingsOnFirstPaste && Pastes == 1) { Prefix = ""; Suffix = ""; }
            Prefix += text;
            Selection = "";
        }
        public void RevealCaret() { }
    }
}
