using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class HotkeySourceTests
{
    [TestMethod]
    public void RightCtrlKeyDownAndKeyUpEmitPushToTalkEvents()
    {
        using var hotkeySource = new RightCtrlHotkeySource();
        var pressed = 0;
        var released = 0;
        hotkeySource.Pressed += () => pressed++;
        hotkeySource.Released += () => released++;

        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightControlVirtualKeyForTest, RightCtrlHotkeySource.KeyDownMessageForTest);
        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightControlVirtualKeyForTest, RightCtrlHotkeySource.KeyUpMessageForTest);

        Assert.AreEqual(1, pressed);
        Assert.AreEqual(1, released);
    }

    [TestMethod]
    public void RightShiftKeyDownEmitsOneTogglePerPhysicalPress()
    {
        using var hotkeySource = new RightCtrlHotkeySource();
        var toggles = 0;
        hotkeySource.ToggleRequested += () => toggles++;

        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightShiftVirtualKeyForTest, RightCtrlHotkeySource.KeyDownMessageForTest);
        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightShiftVirtualKeyForTest, RightCtrlHotkeySource.KeyDownMessageForTest);
        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightShiftVirtualKeyForTest, RightCtrlHotkeySource.KeyUpMessageForTest);
        hotkeySource.ProcessKeyEventForTest(RightCtrlHotkeySource.RightShiftVirtualKeyForTest, RightCtrlHotkeySource.KeyDownMessageForTest);

        Assert.AreEqual(2, toggles);
    }
}
