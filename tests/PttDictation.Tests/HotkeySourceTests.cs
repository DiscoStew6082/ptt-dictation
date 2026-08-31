using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class HotkeySourceTests
{
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
