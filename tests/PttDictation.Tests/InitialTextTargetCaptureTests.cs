using PttDictation.App;
using System.Windows.Automation;

namespace PttDictation.Tests;

[TestClass]
public sealed class InitialTextTargetCaptureTests
{
    [TestMethod]
    public void UnsupportedGroupUsesBoundedFocusRefinementWithoutMakingTheGroupWritable()
    {
        var fixture = new Fixture { OriginalControlType = ControlType.Group };
        Assert.AreSame(fixture.EditorTarget, fixture.Capture());
        Assert.AreEqual(1, fixture.Waits);
        Assert.IsFalse(AutomationTextSurface.AllowsFallback(true, false, false, null, null));
        fixture = new Fixture { OriginalControlType = ControlType.Group, PaneReadsRemaining = 20 };
        Assert.AreSame(fixture.OriginalTarget, fixture.Capture());
        Assert.AreEqual(3, fixture.Waits);
    }

    [TestMethod]
    public void GroupRefinementRejectsAnUnrelatedOrChangedFocus()
    {
        var unrelated = new Fixture { OriginalControlType = ControlType.Group, Related = false };
        Assert.AreSame(unrelated.OriginalTarget, unrelated.Capture());
        foreach (var changeAt in new[] { "before", "wait", "focus", "inspect" })
        {
            var changed = new Fixture { OriginalControlType = ControlType.Group, ChangeAt = changeAt };
            Assert.AreSame(changed.OriginalTarget, changed.Capture(), changeAt);
        }
    }

    [TestMethod]
    public void InitialRefinementDoesNotOverrideDisabledPasswordOrExistingProviderCapabilities()
    {
        foreach (var control in new[] { ControlType.Pane, ControlType.Group })
        {
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(false, false, false, control, false, false));
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(true, true, false, control, false, false));
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(true, false, true, control, false, false));
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(true, false, false, control, true, false));
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(true, false, false, control, false, true));
        }
        foreach (var control in new[] { ControlType.Edit, ControlType.Text, ControlType.Button, ControlType.ComboBox })
            Assert.IsFalse(AutomationTextSurface.AllowsInitialFocusRefinement(true, false, false, control, false, false));
    }

    [TestMethod]
    public void UnsupportedPaneRefinesToItsActuallyFocusedEditorBeforeCaptureCompletes()
    {
        var fixture = new Fixture();
        Assert.AreSame(fixture.EditorTarget, fixture.Capture());
        Assert.AreEqual(1, fixture.Waits);
    }

    [TestMethod]
    public void ProviderThatNeedsSeveralObservationsHasABoundedRecoveryWindow()
    {
        var fixture = new Fixture { PaneReadsRemaining = 2 };
        Assert.AreSame(fixture.EditorTarget, fixture.Capture());
        Assert.AreEqual(3, fixture.Waits);
        fixture = new Fixture { PaneReadsRemaining = 20 };
        Assert.AreSame(fixture.OriginalTarget, fixture.Capture());
        Assert.AreEqual(3, fixture.Waits);
    }

    [TestMethod]
    public void SameIdentityCanAcquireCapabilitiesWithoutAdoptingAnotherElement()
    {
        var fixture = new Fixture { SameIdentity = true };
        Assert.AreSame(fixture.EditorTarget, fixture.Capture());
    }

    [TestMethod]
    public void NormalAndUnsupportedEditableTargetsNeverRequeryFocus()
    {
        foreach (var supported in new[] { false, true })
        {
            var fixture = new Fixture { OriginalIsPane = false };
            fixture.OriginalTarget.Supported = supported;
            Assert.AreSame(fixture.OriginalTarget, fixture.Capture());
            Assert.AreEqual(0, fixture.Waits);
        }
    }

    [TestMethod]
    public void UnrelatedFocusedEditorCannotBeAdopted()
    {
        var fixture = new Fixture { Related = false };
        Assert.AreSame(fixture.OriginalTarget, fixture.Capture());
        Assert.AreEqual(1, fixture.Inspections);
    }

    [TestMethod]
    public void FocusOrSelectionChangesBeforeDuringOrAfterInspectionRejectRecovery()
    {
        foreach (var changeAt in new[] { "before", "wait", "focus", "inspect" })
        {
            var fixture = new Fixture { ChangeAt = changeAt };
            Assert.AreSame(fixture.OriginalTarget, fixture.Capture(), changeAt);
        }
    }

    [TestMethod]
    public void RecoveryRequiresFocusedWritableTargetAndConsistentCapturedSelection()
    {
        foreach (var rejection in new[] { "unsupported", "unfocused", "selection" })
        {
            var fixture = new Fixture();
            fixture.EditorTarget.Supported = rejection != "unsupported";
            fixture.EditorTarget.Focused = rejection != "unfocused";
            fixture.EditorTarget.SelectionCurrent = rejection != "selection";
            Assert.AreSame(fixture.OriginalTarget, fixture.Capture(), rejection);
        }
    }

    private sealed class Identity;

    private sealed class Fixture
    {
        private readonly Identity _original = new();
        private readonly Identity _editor = new();
        private bool _unchanged = true;
        public Target OriginalTarget { get; } = new() { Supported = false };
        public Target EditorTarget { get; } = new();
        public bool OriginalIsPane { get; init; } = true;
        public ControlType OriginalControlType { get; init; } = ControlType.Pane;
        public bool Related { get; init; } = true;
        public bool SameIdentity { get; init; }
        public string? ChangeAt { get; init; }
        public int PaneReadsRemaining { get; set; }
        public int Waits { get; private set; }
        public int Inspections { get; private set; }

        public IWindowsTextTarget Capture() => InitialTextTargetCapture.Capture(_original,
            identity =>
            {
                Inspections++;
                if (Inspections == 1) return new(OriginalTarget, OriginalIsPane
                    && AutomationTextSurface.AllowsInitialFocusRefinement(true, false, false,
                        OriginalControlType, false, false));
                if (ChangeAt == "inspect") _unchanged = false;
                if (PaneReadsRemaining-- > 0) return new(OriginalTarget, true);
                return new(EditorTarget, false);
            },
            () =>
            {
                if (ChangeAt == "focus") _unchanged = false;
                return SameIdentity ? _original : _editor;
            },
            (original, candidate) => original == candidate || Related,
            () => _unchanged && ChangeAt != "before",
            duration =>
            {
                Assert.AreEqual(TimeSpan.FromMilliseconds(50), duration);
                Waits++;
                if (ChangeAt == "wait") _unchanged = false;
            });
    }

    private sealed class Target : IWindowsTextTarget
    {
        public bool Supported { get; set; } = true;
        public bool Focused { get; set; } = true;
        public bool SelectionCurrent { get; set; } = true;
        public bool IsFocused => Focused;
        public bool SupportsReplacement => Supported;
        public bool CanPasteFallback => Supported;
        public bool IsPreparedSelectionCurrent => SelectionCurrent;
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
            => throw new AssertFailedException("Initial capture must never write.");
    }
}
