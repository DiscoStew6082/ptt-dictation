using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class TrayIconFactoryTests
{
    [TestMethod]
    public void TrayIconFactoryCreatesCustomMicrophoneIcon()
    {
        using var icon = TrayIconFactory.Create();
        using var bitmap = icon.ToBitmap();

        Assert.IsNotNull(icon);
        Assert.AreEqual(new Size(16, 16), icon.Size);
        Assert.AreNotEqual(SystemIcons.Application.Handle, icon.Handle);
        Assert.AreEqual(0, bitmap.GetPixel(0, 0).A);
        Assert.IsTrue(bitmap.GetPixel(8, 8).A > 0);
        Assert.IsTrue(bitmap.GetPixel(1, 8).G > bitmap.GetPixel(1, 8).R);
    }
}
