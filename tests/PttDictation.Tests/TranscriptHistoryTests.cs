using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class TranscriptHistoryTests
{
    [TestMethod]
    public void HistoryKeepsRecognitionStagesWithTheirCompletedTranscript()
    {
        var history = new SessionHistory();
        history.Add("Existing transcript.");
        history.Add("Use the textbox.", new TranscriptComparison(
            "use the textbook", "Use the textbook.", "use the text box", "use the textbox", "Use the textbox."));
        history.Add("   ");

        CollectionAssert.AreEqual(new[] { "Existing transcript.", "Use the textbox." }, history.Items.ToArray());
        Assert.HasCount(2, history.Entries);
        Assert.IsNull(history.Entries[0].Comparison);
        Assert.AreEqual("Use the textbox.", history.Entries[1].Transcript);
        var comparison = history.Entries[1].Comparison!;
        Assert.AreEqual("use the textbook", comparison.RawPreview);
        Assert.AreEqual("Use the textbook.", comparison.LastPreview);
        Assert.AreEqual("use the text box", comparison.RawFinal);
        Assert.AreEqual("use the textbox", comparison.CorrectedFinal);
        Assert.AreEqual("Use the textbox.", comparison.FinalText);
    }

    [TestMethod]
    public void SelectingSessionShowsAllRecognitionAndCorrectionStagesWithoutTruncation()
    {
        RunOnStaThread(() =>
        {
            var lastPreview = "Use the textbook. " + new string('x', 5000) + " preview end";
            var history = new SessionHistory();
            history.Add("Old transcript.");
            history.Add("Use the textbox.", new TranscriptComparison(
                "use the textbook", lastPreview, "use the text box", "use the textbox", "Use the textbox."));
            using var form = new SessionHistoryForm(history) { Size = new Size(520, 420) };
            form.CreateControl();
            form.PerformLayout();
            var text = FindControls<TextBox>(form).Single();
            StringAssert.StartsWith(text.Text, "Use the textbox.");
            StringAssert.Contains(text.Text, "Old transcript.");

            var sessions = FindControls<ComboBox>(form).Single(control => control.AccessibleName == "Session");
            var view = FindControls<ComboBox>(form).Single(control => control.AccessibleName == "History view");
            sessions.SelectedIndex = 1;
            view.SelectedIndex = 1;

            StringAssert.Contains(text.Text, "Raw preview recognition");
            StringAssert.Contains(text.Text, "use the textbook");
            StringAssert.Contains(text.Text, lastPreview);
            StringAssert.Contains(text.Text, "Final recognition");
            StringAssert.Contains(text.Text, "use the text box");
            StringAssert.Contains(text.Text, "Saved phrase replacements: changed");
            StringAssert.Contains(text.Text, "use the textbox");
            StringAssert.Contains(text.Text, "Final text");
            StringAssert.Contains(text.Text, "Use the textbox.");
            Assert.IsTrue(text.ReadOnly && text.WordWrap);
            Assert.AreEqual(ScrollBars.Vertical, text.ScrollBars);
            Assert.IsTrue(text.Height > 120, "Comparison should retain a usable scrollable text area at minimum size.");

            var previewPath = Environment.GetEnvironmentVariable("PARAKEET_COMPARISON_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                form.Show();
                Application.DoEvents();
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                preview.Save(previewPath);
                form.Hide();
            }

            sessions.SelectedIndex = 2;
            StringAssert.Contains(text.Text, "No recognition comparison was captured");
            StringAssert.Contains(text.Text, "Old transcript.");
        });
    }

    [TestMethod]
    public void RefreshKeepsSelectedComparisonAndIdentifiesUnchangedStages()
    {
        RunOnStaThread(() =>
        {
            var history = new SessionHistory();
            history.Add("Already correct.", new TranscriptComparison(
                "Already correct.", "Already correct.", "Already correct.", "Already correct.", "Already correct."));
            using var form = new SessionHistoryForm(history);
            var sessions = FindControls<ComboBox>(form).Single(control => control.AccessibleName == "Session");
            var view = FindControls<ComboBox>(form).Single(control => control.AccessibleName == "History view");
            sessions.SelectedIndex = 1;
            view.SelectedIndex = 1;
            history.Add("New transcript.");

            form.RefreshItems();

            var text = FindControls<TextBox>(form).Single();
            StringAssert.Contains(text.Text, "preview corrections and formatting: unchanged");
            StringAssert.Contains(text.Text, "compared with raw preview: unchanged");
            StringAssert.Contains(text.Text, "Saved phrase replacements: unchanged");
            StringAssert.Contains(text.Text, "formatting: unchanged");
            Assert.IsFalse(text.Text.Contains("New transcript.", StringComparison.Ordinal));
            sessions.SelectedIndex = 0;
            StringAssert.StartsWith(text.Text, "New transcript.");
            StringAssert.Contains(text.Text, "Already correct.");
            Assert.IsFalse(view.Enabled);
        });
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindControls<T>(child)) yield return descendant;
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "History form test timed out.");
        if (exception is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
