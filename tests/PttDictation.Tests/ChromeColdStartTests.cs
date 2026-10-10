namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ChromeColdStartTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("ChromeColdStart")]
    [DataRow("textarea")]
    [DataRow("contenteditable")]
    public Task FreshChromeAcceptsFirstAndSecondDictation(string editorKind) =>
        new BrowserColdStartHarness(TestContext, ColdStartBrowser.Chrome).RunAsync(editorKind);
}

internal enum ColdStartBrowser { Firefox, Chrome }
