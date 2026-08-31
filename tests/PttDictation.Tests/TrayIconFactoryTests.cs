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
        Assert.IsTrue(bitmap.GetPixel(8, 4).A > 0);
        Assert.IsTrue(bitmap.GetPixel(8, 4).G > bitmap.GetPixel(8, 4).R);
    }
}
