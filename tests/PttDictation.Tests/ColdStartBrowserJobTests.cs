using System.Runtime.InteropServices;

namespace PttDictation.Tests;

[TestClass]
public sealed class ColdStartBrowserJobTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("--no-first-run")]
    [DataRow("--user-data-dir=C:\\profile folder\\")]
    [DataRow("C:\\Program Files\\Browser\\browser.exe")]
    [DataRow("argument with \\\"quotes\\\" and \\slashes\\")]
    public void NativeLaunchArgumentRoundTripsWithoutSplittingOrChangingProfile(string argument)
    {
        var parsed = CommandLineToArgvW("test.exe " + ColdStartBrowserJob.QuoteArgument(argument), out var count);
        Assert.AreNotEqual(IntPtr.Zero, parsed);
        try
        {
            Assert.AreEqual(2, count);
            Assert.AreEqual(argument, Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size)));
        }
        finally { LocalFree(parsed); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
