namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FirefoxColdStartTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("FirefoxColdStart")]
    [DataRow("textarea")]
    [DataRow("contenteditable")]
    public Task FreshFirefoxAcceptsFirstAndSecondDictation(string editorKind) =>
        new BrowserColdStartHarness(TestContext, ColdStartBrowser.Firefox).RunAsync(editorKind);
}
