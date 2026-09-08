using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveOverlayTests
{
    [TestMethod]
    public void LongFallbackTranscriptCanScrollBackAndFollowsNewSpeech()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            var transcript = string.Join(Environment.NewLine,
                Enumerable.Range(1, 80).Select(i => $"Spoken sentence number {i}."));

            overlay.ShowListeningTranscript(transcript, ListeningTriggerMode.Toggle);
            var text = Descendants(overlay).OfType<TextBox>().SingleOrDefault();
            Assert.IsNotNull(text, "Long fallback dictation needs a scrollable transcript control.");
            Assert.IsTrue(text.ReadOnly);
            Assert.AreEqual(ScrollBars.Vertical, text.ScrollBars);
            Assert.AreEqual((nint)3, SendMessage(text.Handle, 0x0021, overlay.Handle, 0),
                "The transcript control must decline mouse activation.");
            StringAssert.Contains(text.Text, "Spoken sentence number 1.");
            StringAssert.Contains(text.Text, "Spoken sentence number 80.");
            Assert.IsTrue(text.GetCharIndexFromPosition(new Point(2, 2)) > 0,
                $"The latest speech should be brought into view automatically. Bounds {text.Bounds}, selection {text.SelectionStart}, last character {text.GetPositionFromCharIndex(text.TextLength - 1)}.");

            SendMessage(text.Handle, 0x00B5, 6, 0); // EM_SCROLL / SB_TOP
            Assert.AreEqual(0, text.GetCharIndexFromPosition(new Point(2, 2)),
                "Earlier speech must remain accessible by scrolling.");
            overlay.AdvanceLiveActivityForTest();
            Assert.AreEqual(0, text.GetCharIndexFromPosition(new Point(2, 2)),
                "Activity animation must not pull a reader back to the bottom.");

            overlay.ShowListeningTranscript(transcript + Environment.NewLine + "Newest spoken words.",
                ListeningTriggerMode.Toggle);
            Assert.IsTrue(text.GetCharIndexFromPosition(new Point(2, 2)) > 0);
            Assert.AreEqual(StatusOverlayForm.ListeningSizeForTest, overlay.Size);

            overlay.ShowProcessing();
            overlay.ShowProcessingTranscript(transcript + Environment.NewLine + "Final recognized words.");
            StringAssert.Contains(text.Text, "Spoken sentence number 1.");
            StringAssert.Contains(text.Text, "Final recognized words.");
            Assert.IsTrue(text.GetPositionFromCharIndex(text.TextLength - 1).Y < text.ClientSize.Height,
                "The end of the final transcript should remain in view while processing.");
        });
    }

    [TestMethod]
    public void InlinePreviewKeepsRecordingIndicatorAndRestoresFullFallbackWhenDisabled()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.SetInlinePreview(true);
            overlay.ShowListeningTranscript("The text is already in the destination.", ListeningTriggerMode.Toggle, "F9");

            Assert.IsTrue(overlay.Visible);
            Assert.IsTrue(overlay.ActivityMeterVisibleForTest);
            Assert.IsTrue(overlay.LiveActivityTimerEnabledForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Press F9 to transcribe");
            Assert.IsFalse(overlay.MessageTextForTest.Contains("already in the destination", StringComparison.Ordinal));
            Assert.IsFalse(Descendants(overlay).OfType<TextBox>().Single().Visible);

            overlay.SetInlinePreview(false);

            var transcript = Descendants(overlay).OfType<TextBox>().Single();
            Assert.IsTrue(transcript.Visible);
            StringAssert.Contains(transcript.Text, "The text is already in the destination.");
            Assert.IsTrue(overlay.ActivityMeterVisibleForTest);
            Assert.AreEqual(StatusOverlayForm.ListeningSizeForTest, overlay.Size);

            overlay.SetInlinePreview(true);
            overlay.ShowProcessing();
            Assert.IsFalse(transcript.Visible);
            Assert.AreEqual("Transcribing and preparing to paste…", overlay.MessageTextForTest);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Overlay test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
