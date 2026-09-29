using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class HotkeySourceTests
{
    [TestMethod]
    [DataRow(DictationHotkey.RightControl)]
    [DataRow(DictationHotkey.LeftControl)]
    public void ControlReleaseReachesWindowsAndEndsHoldExactlyOnce(DictationHotkey key)
    {
        using var source = new GlobalHotkeySource(key, DictationHotkey.F9);
        var releases = 0;
        source.Released += () => releases++;
        var vk = GlobalHotkeySource.VirtualKeyForTest(key);
        Assert.IsTrue(source.ProcessKeyEventForTest(vk, GlobalHotkeySource.KeyDownMessageForTest));
        Assert.IsFalse(source.ProcessKeyEventForTest(vk, GlobalHotkeySource.KeyUpMessageForTest),
            "Windows must receive Ctrl-up even if its state was already down before PTT intercepted a press.");
        Assert.AreEqual(1, releases);
        Assert.IsFalse(source.ProcessKeyEventForTest(vk, GlobalHotkeySource.KeyUpMessageForTest));
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public void UnmatchedReleaseAfterStartupOrReconfigurationIsNotSwallowed()
    {
        using var source = new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.F9);
        var vk = GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.RightControl);
        Assert.IsFalse(source.ProcessKeyEventForTest(vk, GlobalHotkeySource.KeyDownMessageForTest));
        source.Configure(DictationHotkey.RightControl, DictationHotkey.RightShift);
        Assert.IsFalse(source.ProcessKeyEventForTest(vk, GlobalHotkeySource.KeyUpMessageForTest),
            "The press reached Windows before PTT reserved this key; the release must also reach Windows.");
        Assert.IsFalse(source.ProcessKeyEventForTest(0xA1, GlobalHotkeySource.KeyUpMessageForTest));
    }

    [TestMethod]
    public void ControlToggleReleaseReachesWindowsWithoutAnExtraToggle()
    {
        using var source = new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.RightControl);
        var toggles = 0;
        source.ToggleRequested += () => toggles++;
        Assert.IsTrue(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyDownMessageForTest));
        Assert.IsFalse(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyUpMessageForTest));
        Assert.AreEqual(1, toggles);
        Assert.IsTrue(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyDownMessageForTest));
        Assert.AreEqual(2, toggles);
    }

    [TestMethod]
    public void ReconfigurationDuringHoldRetainsReleaseOwnershipAndSuppressesOldKeyRepeats()
    {
        using var source = new GlobalHotkeySource(DictationHotkey.RightControl, DictationHotkey.F9);
        var releases = 0;
        source.Released += () => releases++;
        Assert.IsTrue(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyDownMessageForTest));
        source.Configure(DictationHotkey.F10, DictationHotkey.F11);
        Assert.IsTrue(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyDownMessageForTest));
        Assert.IsFalse(source.ProcessKeyEventForTest(0xA3, GlobalHotkeySource.KeyUpMessageForTest));
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public void SelectedHoldKeyDownAndKeyUpEmitPushToTalkEvents()
    {
        using var hotkeySource = new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.F9);
        var pressed = 0;
        var released = 0;
        hotkeySource.Pressed += () => pressed++;
        hotkeySource.Released += () => released++;

        var virtualKey = GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.F8);
        var downHandled = hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyDownMessageForTest);
        var upHandled = hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyUpMessageForTest);

        Assert.AreEqual(1, pressed);
        Assert.AreEqual(1, released);
        Assert.IsTrue(downHandled);
        Assert.IsTrue(upHandled);
    }

    [TestMethod]
    public void SelectedToggleKeyDownEmitsOneTogglePerPhysicalPress()
    {
        using var hotkeySource = new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.F9);
        var toggles = 0;
        hotkeySource.ToggleRequested += () => toggles++;

        var virtualKey = GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.F9);
        hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyDownMessageForTest);
        hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyDownMessageForTest);
        hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyUpMessageForTest);
        hotkeySource.ProcessKeyEventForTest(virtualKey, GlobalHotkeySource.KeyDownMessageForTest);

        Assert.AreEqual(2, toggles);
    }

    [TestMethod]
    public void ReconfiguredKeysTakeEffectWithoutReinstallingHook()
    {
        using var hotkeySource = new GlobalHotkeySource(DictationHotkey.RightControl, DictationHotkey.RightShift);
        var pressed = 0;
        hotkeySource.Pressed += () => pressed++;

        hotkeySource.Configure(DictationHotkey.F10, DictationHotkey.F11);
        var oldHandled = hotkeySource.ProcessKeyEventForTest(
            GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.RightControl),
            GlobalHotkeySource.KeyDownMessageForTest);
        var newHandled = hotkeySource.ProcessKeyEventForTest(
            GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.F10),
            GlobalHotkeySource.KeyDownMessageForTest);

        Assert.AreEqual(1, pressed);
        Assert.IsFalse(oldHandled);
        Assert.IsTrue(newHandled);
    }

    [TestMethod]
    public void HoldAndToggleKeysMustBeDifferent()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.F8));
    }
}
