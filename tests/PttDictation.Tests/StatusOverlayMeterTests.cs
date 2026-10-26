using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class StatusOverlayMeterTests
{
    [TestMethod]
    public void StatusOverlayActivityMeterEmphasizesCenterBarsAtModerateLevels()
    {
        StaTest.Run(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            overlay.UpdateActivityLevelForTest(0.35);

            var profile = overlay.ActivityMeterBarHeightsForTest;
            var center = profile.Length / 2;
            var tallestEdge = Math.Max(profile[0], profile[^1]);
            var minimumFullHeightFeel = (int)(overlay.ActivityMeterHeightForTest * 0.8);

            Assert.IsTrue(profile[center] >= minimumFullHeightFeel);
            Assert.IsTrue(profile[center] <= overlay.ActivityMeterHeightForTest);
            Assert.IsTrue(profile[center] >= tallestEdge * 2);

            overlay.UpdateActivityLevelForTest(0.75);
            var strongerProfile = overlay.ActivityMeterBarHeightsForTest;

            Assert.IsTrue(strongerProfile[center] > profile[center]);
        });
    }

}
