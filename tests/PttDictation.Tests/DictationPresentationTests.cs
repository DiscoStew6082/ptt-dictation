using PttDictation.App;
using PttDictation.Core;
using System.Runtime.InteropServices;

namespace PttDictation.Tests;

[TestClass]
public sealed class DictationPresentationTests
{
    [TestMethod]
    [VisibleUi]
    public void SlowOriginalTextboxShowsCancelableWaitAndClearsOnResumeOrCancellation()
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancel = new ToolStripMenuItem();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Recording, DictationTriggerMode.Hold);
            var waiting = false;
            var sounds = new List<StatusSound>();
            var environment = CreateEnvironment(() => AppSettings.Default, () => state,
                playStatusSound: sounds.Add) with
            {
                IsInlinePreview = () => true,
                IsWaitingForTargetResponse = () => waiting
            };
            var presentation = new DictationPresentation(overlay, cancel, environment);
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible);
            waiting = true;
            await presentation.ApplyAsync(state);
            await presentation.ApplyAsync(state);
            Assert.IsTrue(overlay.Visible);
            Assert.AreEqual("Waiting for textbox", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Recording continues");
            Assert.IsTrue(cancel.Enabled);
            Assert.IsTrue(overlay.ShowWithoutActivationForTest);
            Assert.IsFalse(overlay.AutoHideTimerEnabledForTest);
            CollectionAssert.AreEqual(new[] { StatusSound.Listening }, sounds);
            state = state with { Phase = DictationWorkflowPhase.Processing };
            await presentation.ApplyAsync(state);
            Assert.IsTrue(overlay.Visible);
            Assert.IsFalse(overlay.MessageTextForTest.Contains("Recording continues"));
            waiting = false;
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible, "The wait indicator must clear when the same operation returns.");
            waiting = true;
            state = state with { Phase = DictationWorkflowPhase.Cancelled };
            await presentation.ApplyAsync(state);
            await presentation.ApplyAsync(state);
            Assert.AreEqual("Dictation cancelled", overlay.TitleTextForTest,
                "A queued wait notification must not replace the current terminal state.");
            StringAssert.Contains(overlay.MessageTextForTest, "Text already inserted remains in the textbox.");
            Assert.IsFalse(cancel.Enabled);
        });
    }

    [TestMethod]
    [VisibleUi]
    public void PreparingOverlayPreservesNativeForegroundFocusOnFirstAndSecondShow()
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
        {
            var diagnosticsDirectory = Path.Combine(AppContext.BaseDirectory, "overlay-focus-" + Guid.NewGuid().ToString("N"));
            using var diagnostics = DiagnosticTrace.Configure(diagnosticsDirectory, "preparing-overlay-native-focus");
            using var overlay = new StatusOverlayForm();
            using var guard = new WindowsFocusCaptureGuard(() => overlay.IsHandleCreated ? overlay.Handle : IntPtr.Zero);
            Application.DoEvents();
            using var cancel = new ToolStripMenuItem();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Preparing, DictationTriggerMode.Hold);
            var presentation = new DictationPresentation(overlay, cancel,
                CreateEnvironment(() => AppSettings.Default, () => state) with { IsInlinePreview = () => false });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var foreground = GetForegroundWindow();
                var unchanged = guard.BeginPreparation(() => 0);
                if (!unchanged()) Assert.Inconclusive("No stable native foreground target is available for the overlay focus test.");
                await presentation.ApplyAsync(state);
                Application.DoEvents();
                Assert.IsTrue(overlay.Visible);
                Assert.AreEqual(foreground, GetForegroundWindow(), $"Preparing show {attempt + 1} changed foreground.");
                var accepted = unchanged();
                DiagnosticTrace.FlushAsync().GetAwaiter().GetResult();
                Assert.IsTrue(accepted, $"Preparing show {attempt + 1} invalidated native preparation guard. "
                    + $"Overlay HWND={overlay.Handle}. "
                    + File.ReadAllText(Path.Combine(diagnosticsDirectory, "events.jsonl")));
                overlay.HideRecording();
                Application.DoEvents();
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [TestMethod]
    [DataRow(DictationTriggerMode.Hold, "Keep holding")]
    [DataRow(DictationTriggerMode.Toggle, "Press F9 again to cancel")]
    [VisibleUi]
    public void PreparingShowsNonActivatingWaitUntilRecordingStarts(DictationTriggerMode mode, string expectedHint)
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancel = new ToolStripMenuItem();
            var sounds = new List<StatusSound>();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Preparing, mode);
            var environment = CreateEnvironment(
                () => AppSettings.Default with { ToggleHotkey = DictationHotkey.F9 },
                () => state, playStatusSound: sounds.Add) with { IsInlinePreview = () => false };
            var presentation = new DictationPresentation(overlay, cancel, environment);

            await presentation.ApplyAsync(state);
            Assert.IsTrue(overlay.Visible, "Cold-start waiting must be visible before microphone startup.");
            Assert.AreEqual("Preparing textbox", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Not recording yet");
            StringAssert.Contains(overlay.MessageTextForTest, expectedHint);
            Assert.IsTrue(overlay.ShowWithoutActivationForTest);
            Assert.AreNotEqual(0, overlay.ExtendedWindowStyleForTest & StatusOverlayForm.NoActivateExtendedStyleForTest);
            Assert.IsFalse(overlay.ActivityMeterVisibleForTest);
            Assert.IsFalse(overlay.AutoHideTimerEnabledForTest);
            Assert.AreEqual(0, sounds.Count);
            Assert.IsTrue(cancel.Enabled);

            state = state with { Phase = DictationWorkflowPhase.Recording };
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible);
            CollectionAssert.AreEqual(new[] { StatusSound.Listening }, sounds);
        });
    }

    [TestMethod]
    [DataRow(DictationTriggerMode.Hold)]
    [DataRow(DictationTriggerMode.Toggle)]
    [VisibleUi]
    public void LiveTextboxRecordingAndProcessingNeverShowFloatingOverlay(DictationTriggerMode mode)
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancel = new ToolStripMenuItem();
            var state = new DictationWorkflowState(DictationWorkflowPhase.Recording, mode);
            var inlineReady = false;
            var sounds = new List<StatusSound>();
            var environment = CreateEnvironment(() => AppSettings.Default, () => state,
                playStatusSound: sounds.Add) with { IsInlinePreview = () => inlineReady };
            var presentation = new DictationPresentation(overlay, cancel, environment);
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible, "Capturing the destination must not flash the large overlay.");
            inlineReady = true;
            state = state with { Transcript = "Words in the selected textbox" };
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible, "Live text needs no floating transcription window.");
            state = state with { Phase = DictationWorkflowPhase.Processing };
            await presentation.ApplyAsync(state);
            state = state with { Transcript = "Revised final words", ProcessingDetail = "Preparing to paste." };
            await presentation.ApplyAsync(state);
            Assert.IsFalse(overlay.Visible, "Final processing must not open a corner indicator.");
            Assert.IsTrue(cancel.Enabled);
            CollectionAssert.AreEqual(new[] { StatusSound.Listening, StatusSound.Transcribing }, sounds);
        });
    }

    [TestMethod]
    [DataRow(DictationWorkflowPhase.Pasted)]
    [DataRow(DictationWorkflowPhase.InsertedPreview)]
    [VisibleUi]
    public void CompletedPasteRefreshesHistoryAndHidesAfterQuarterSecond(DictationWorkflowPhase phase)
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
        {
            using var overlay = new StatusOverlayForm();
            using var cancelItem = new ToolStripMenuItem();
            overlay.ShowProcessing();
            var state = new DictationWorkflowState(phase);
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
    [VisibleUi]
    public void CompletedPasteDoesNotHideAWorkflowThatStartedDuringVisibilityDelay()
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
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
    [VisibleUi]
    public void FailedStateDisablesCancellationAndPresentsErrorOnce()
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
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
        StaTest.Run(async () =>
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
    [VisibleUi]
    public void CleanupWarningIsPresentedOnceAcrossProcessingAndTerminalState()
    {
        VisibleUiTestGate.RequireOptIn();
        StaTest.Run(async () =>
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
            DelayAsync: delayAsync ?? (_ => Task.CompletedTask),
            IsInlinePreview: () => false);
    }

}
