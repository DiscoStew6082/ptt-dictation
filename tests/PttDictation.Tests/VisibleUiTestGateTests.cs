namespace PttDictation.Tests;

[TestClass]
public sealed class VisibleUiTestGateTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("true")]
    [DataRow("yes")]
    [DataRow(" 1 ")]
    public void MissingOrAmbiguousOptInDeniesVisibleUi(string? value)
    {
        Assert.IsFalse(VisibleUiTestGate.IsEnabled(value));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => VisibleUiTestGate.RequireOptIn(value));
    }

    [TestMethod]
    public void GateStopsDirectInvocationBeforeItsBodyRuns()
    {
        var bodyRan = false;
        void GuardedHeadlessProbe()
        {
            VisibleUiTestGate.RequireOptIn(null);
            bodyRan = true;
        }

        Assert.ThrowsExactly<AssertInconclusiveException>(GuardedHeadlessProbe);
        Assert.IsFalse(bodyRan);
    }

    [TestMethod]
    public void ScreenshotPathCannotBypassOptIn()
    {
        Assert.ThrowsExactly<AssertInconclusiveException>(() =>
            VisibleUiTestGate.ShouldCaptureScreenshot("requested-preview.png", null));
        Assert.IsFalse(VisibleUiTestGate.ShouldCaptureScreenshot(null, null));
        Assert.IsFalse(VisibleUiTestGate.ShouldCaptureScreenshot(" ", "1"));
    }

    [TestMethod]
    public void ExactOptInPermitsHeadlessGateProbeWithoutOpeningAnything()
    {
        Assert.IsTrue(VisibleUiTestGate.IsEnabled("1"));
        VisibleUiTestGate.RequireOptIn("1");
        Assert.IsTrue(VisibleUiTestGate.ShouldCaptureScreenshot("requested-preview.png", "1"));
    }

    [TestMethod]
    public void VisibleUiCategoryIsAvailableToDiscoveryWithoutExecutingTest()
    {
        CollectionAssert.AreEqual(new[] { "VisibleUi" }, new VisibleUiAttribute().TestCategories.ToArray());
    }
}
