using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class DictationPresentationTests
{
    [TestMethod]
    public void EnteringToggleRecordingPresentsConfiguredTriggerAndEnablesCancellation()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem { Enabled = false };
            var sounds = new List<StatusSound>();
            var state = new DictationWorkflowState(
                DictationWorkflowPhase.Recording,
                DictationTriggerMode.Toggle);
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default with { ToggleHotkey = DictationHotkey.F9 },
                    () => state,
                    playStatusSound: sounds.Add));

            await presentation.ApplyAsync(state);

            Assert.IsTrue(cancelItem.Enabled);
            CollectionAssert.AreEqual(new[] { StatusSound.Listening }, sounds);
            Assert.AreEqual("Listening", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Press F9 to transcribe");
        });
    }

    [TestMethod]
    public void ProcessingUpdatesTranscriptAndDetailWithoutReplayingTransitionSound()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            var sounds = new List<StatusSound>();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Recording);
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    playStatusSound: sounds.Add));
            await presentation.ApplyAsync(state);
            sounds.Clear();

            state = new DictationWorkflowState(
                DictationWorkflowPhase.Processing,
                Transcript: "first words",
                ProcessingDetail: "Loading the local model.");
            await presentation.ApplyAsync(state);
            state = state with
            {
                Transcript = "first words and more",
                ProcessingDetail = "Preparing to paste."
            };
            await presentation.ApplyAsync(state);

            Assert.IsTrue(cancelItem.Enabled);
            CollectionAssert.AreEqual(new[] { StatusSound.Transcribing }, sounds);
            Assert.AreEqual("Processing", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Preparing to paste.");
            StringAssert.Contains(overlay.MessageTextForTest, "first words and more");
        });
    }

    [TestMethod]
    public void CompletedPasteRefreshesHistoryAndHidesAfterQuarterSecond()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            overlay.ShowProcessing();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Pasted);
            var sounds = new List<StatusSound>();
            var delays = new List<TimeSpan>();
            var historyRefreshes = 0;
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    refreshHistory: () => historyRefreshes++,
                    playStatusSound: sounds.Add,
                    delayAsync: duration =>
                    {
                        delays.Add(duration);
                        return Task.CompletedTask;
                    }));

            await presentation.ApplyAsync(state);

            Assert.IsFalse(cancelItem.Enabled);
            Assert.AreEqual(1, historyRefreshes);
            CollectionAssert.AreEqual(new[] { StatusSound.Done }, sounds);
            CollectionAssert.AreEqual(
                new[] { TimeSpan.FromMilliseconds(250) },
                delays);
            Assert.IsFalse(overlay.Visible);
        });
    }

    [TestMethod]
    public void CompletedPasteDoesNotHideAWorkflowThatStartedDuringVisibilityDelay()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            overlay.ShowProcessing();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Pasted);
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    delayAsync: _ =>
                    {
                        state = new DictationWorkflowState(DictationWorkflowPhase.Recording);
                        return Task.CompletedTask;
                    }));

            await presentation.ApplyAsync(state);

            Assert.IsTrue(overlay.Visible);
        });
    }

    [TestMethod]
    public void FailedStateDisablesCancellationAndPresentsErrorOnce()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem { Enabled = true };
            var sounds = new List<StatusSound>();
            var notifications = new List<(string Title, string Message, ToolTipIcon Icon)>();
            var state = new DictationWorkflowState(
                DictationWorkflowPhase.Failed,
                ErrorMessage: "The selected model could not start.");
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    showTrayNotification: (title, message, icon) => notifications.Add((title, message, icon)),
                    playStatusSound: sounds.Add));

            await presentation.ApplyAsync(state);
            await presentation.ApplyAsync(state);

            Assert.IsFalse(cancelItem.Enabled);
            CollectionAssert.AreEqual(new[] { StatusSound.Error }, sounds);
            Assert.AreEqual("Dictation failed", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "selected model could not start");
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual(ToolTipIcon.Error, notifications[0].Icon);
        });
    }

    [TestMethod]
    public void CleanupWarningIsForwardedWithTheState()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            var warnings = new List<string>();
            var state = new DictationWorkflowState(
                DictationWorkflowPhase.Idle,
                CleanupWarningPath: @"C:\audio\leftover.wav");
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    showCleanupWarning: warnings.Add));

            await presentation.ApplyAsync(state);

            CollectionAssert.AreEqual(new[] { @"C:\audio\leftover.wav" }, warnings);
        });
    }

    [TestMethod]
    public void CleanupWarningIsPresentedOnceAcrossProcessingAndTerminalState()
    {
        RunOnStaThread(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            var warnings = new List<string>();
            var state = new DictationWorkflowState(
                DictationWorkflowPhase.Processing,
                CleanupWarningPath: @"C:\audio\leftover.wav");
            var presentation = new DictationPresentation(
                overlay,
                cancelItem,
                CreateEnvironment(
                    () => AppSettings.Default,
                    () => state,
                    showCleanupWarning: warnings.Add));

            await presentation.ApplyAsync(state);
            state = new DictationWorkflowState(
                DictationWorkflowPhase.Pasted,
                CleanupWarningPath: @"C:\audio\leftover.wav");
            await presentation.ApplyAsync(state);

            CollectionAssert.AreEqual(new[] { @"C:\audio\leftover.wav" }, warnings);
        });
    }

    private static DictationPresentationEnvironment CreateEnvironment(
        Func<AppSettings> getSettings,
        Func<DictationWorkflowState> getCurrentState,
        Action? refreshHistory = null,
        Action<string>? showCleanupWarning = null,
        Action<string, string, ToolTipIcon>? showTrayNotification = null,
        Action<StatusSound>? playStatusSound = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        return new DictationPresentationEnvironment(
            getSettings,
            getCurrentState,
            IsUnavailable: () => false,
            RefreshHistory: refreshHistory ?? (() => { }),
            ShowCleanupWarning: showCleanupWarning ?? (_ => { }),
            ShowTrayNotification: showTrayNotification ?? ((_, _, _) => { }),
            PlayStatusSound: playStatusSound ?? (_ => { }),
            DelayAsync: delayAsync ?? (_ => Task.CompletedTask));
    }

    private static void RunOnStaThread(Func<Task> action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }
}
