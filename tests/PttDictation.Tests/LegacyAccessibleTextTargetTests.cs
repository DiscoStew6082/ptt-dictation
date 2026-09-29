using System.Runtime.InteropServices;
using System.Windows.Automation;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class LegacyAccessibleTextTargetTests
{
    [TestMethod]
    public void OnlyUnsupportedNonPasswordContainersCanResolveLegacyFocus()
    {
        foreach (var control in new[] { ControlType.Window, ControlType.Document, ControlType.Pane, ControlType.Group })
        {
            Assert.IsTrue(AutomationTextSurface.AllowsLegacyFocusResolution(true, false, false, control));
            Assert.IsFalse(AutomationTextSurface.AllowsLegacyFocusResolution(false, false, false, control));
            Assert.IsFalse(AutomationTextSurface.AllowsLegacyFocusResolution(true, true, false, control));
            Assert.IsFalse(AutomationTextSurface.AllowsLegacyFocusResolution(true, false, true, control));
        }
        foreach (var control in new[] { ControlType.Edit, ControlType.Text, ControlType.Button, ControlType.ComboBox })
            Assert.IsFalse(AutomationTextSurface.AllowsLegacyFocusResolution(true, false, false, control));
    }

    [TestMethod]
    public async Task ProductionOutputUsesOneFinalPasteAndNeverSendsLivePreview()
    {
        var root = new Node { FocusValue = new Node() };
        var writes = new List<string>();
        using var output = new LiveClipboardPaster(() => Capture(root)!, (text, current) =>
        {
            Assert.IsTrue(current());
            writes.Add(text);
        });
        output.CaptureTarget();
        output.UpdatePreview("live words");
        output.Pump();
        Assert.AreEqual(0, writes.Count);
        await output.PasteAsync("Final words.", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Final words." }, writes);
    }

    [TestMethod]
    public async Task FocusChangeAtFinalClipboardValidationPreventsDispatch()
    {
        var root = new Node { FocusValue = new Node() };
        using var output = new LiveClipboardPaster(() => Capture(root)!, (_, current) =>
        {
            root.FocusValue = new Node();
            Assert.IsFalse(current());
            throw new InvalidOperationException("The target changed before dispatch.");
        });
        output.CaptureTarget();
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.PasteAsync("Final words.", CancellationToken.None));
    }

    [TestMethod]
    public void ContainerFocusChainCapturesOnlyTheFocusedWritableTextChild()
    {
        var editor = new Node();
        var document = new Node { RoleValue = 15, StateValue = 64, FocusValue = editor };
        var window = new Node { RoleValue = 9, FocusValue = document };
        var target = Capture(window);
        Assert.IsNotNull(target);
        Assert.IsTrue(target.IsFocused);
        Assert.IsTrue(target.IsPreparedSelectionCurrent);
        Assert.IsTrue(target.CanPasteFallback);
        Assert.IsFalse(target.SupportsReplacement);
        Assert.AreEqual(TextTargetUpdateResult.Unsupported, target.TryReplace("", "words", _ => Assert.Fail()));
    }

    [TestMethod]
    public void MovingToAnotherEditorInSameWindowNeverRedirectsThePaste()
    {
        var original = new Node();
        var root = new Node { FocusValue = original };
        var target = Capture(root)!;
        Assert.IsNotNull(target);
        root.FocusValue = new Node();
        Assert.IsFalse(target.IsFocused);
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
        root.FocusValue = original;
        Assert.IsTrue(target.IsFocused);
    }

    [TestMethod]
    public void SimpleChildIdsRemainPartOfIdentity()
    {
        var root = new Node { FocusValue = 1 };
        var target = Capture(root)!;
        Assert.IsNotNull(target);
        root.FocusValue = 2;
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
        root.FocusValue = 1;
        Assert.IsTrue(target.IsPreparedSelectionCurrent);
    }

    [TestMethod]
    public void NativeChildObjectReturnedForChildIdIsFollowed()
    {
        var editor = new Node();
        var root = new Node { FocusValue = 2, Child = editor };
        Assert.IsNotNull(Capture(root));
        editor.StateValue = 64 | 4;
        Assert.IsNull(Capture(root));
    }

    [TestMethod]
    public void UnsafeUnknownOrForeignTargetsCannotBecomeFallbacks()
    {
        foreach (var state in new int?[] { 0, 4 | 1, 4 | 64, 4 | 0x20000000, 4 | 0x8000, null })
            Assert.IsNull(Capture(new Node { StateValue = state }), $"state={state}");
        foreach (var role in new int?[] { 9, 15, 16, null })
            Assert.IsNull(Capture(new Node { RoleValue = role }), $"role={role}");
        Assert.IsNull(Capture(new Node { ProcessId = 99 }));
        Assert.IsNull(Capture(new Node { ThrowState = true }));
    }

    [TestMethod]
    public void TargetStateAndOriginalWindowAreRecheckedBeforePaste()
    {
        var editor = new Node();
        var currentWindow = true;
        var target = LegacyAccessibleTextTarget.TryCapture(() => editor, 7, () => true, () => currentWindow)!;
        Assert.IsNotNull(target);
        currentWindow = false;
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
        currentWindow = true;
        editor.StateValue = 64 | 4;
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
        editor.StateValue = 4;
        editor.ThrowState = true;
        Assert.IsFalse(target.IsPreparedSelectionCurrent);
    }

    [TestMethod]
    public void ChangedCaptureGuardRejectsBeforeAndAfterResolution()
    {
        var editor = new Node();
        Assert.IsNull(LegacyAccessibleTextTarget.TryCapture(() => editor, 7, () => false, () => true));
        var unchanged = true;
        var root = new Node { OnFocus = () => unchanged = false, FocusValue = editor };
        Assert.IsNull(LegacyAccessibleTextTarget.TryCapture(() => root, 7, () => unchanged, () => true));
    }

    [TestMethod]
    public void MissingUnknownCyclicAndExcessiveFocusChainsFailClosed()
    {
        Assert.IsNull(Capture(new Node { FocusValue = null }));
        Assert.IsNull(Capture(new Node { FocusValue = "unknown" }));
        var root = new Node();
        var child = new Node { FocusValue = root };
        root.FocusValue = child;
        Assert.IsNull(Capture(root));
        root = new Node();
        for (var index = 0; index < 32; index++) root = new Node { FocusValue = root };
        Assert.IsNull(Capture(root));
    }

    private static IWindowsTextTarget? Capture(Node root)
        => LegacyAccessibleTextTarget.TryCapture(() => root, 7, () => true, () => true);

    private sealed class Node : ILegacyAccessibleNode
    {
        public object? FocusValue { get; set; } = 0;
        public Action? OnFocus { get; init; }
        public object? Focus { get { OnFocus?.Invoke(); return FocusValue; } }
        public ILegacyAccessibleNode? Child { get; init; }
        public int? RoleValue { get; init; } = 42;
        public int? StateValue { get; set; } = 4;
        public bool ThrowState { get; set; }
        public int ProcessId { get; init; } = 7;
        public ILegacyAccessibleNode? GetChild(int childId) => Child;
        public int? Role(int childId) => RoleValue;
        public int? State(int childId) => ThrowState ? throw new COMException("Unavailable") : StateValue;
        public bool HasSameIdentity(ILegacyAccessibleNode other) => ReferenceEquals(this, other);
    }
}
