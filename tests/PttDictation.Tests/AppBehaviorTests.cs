using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class AppBehaviorTests
{
    [TestMethod]
    public async Task ClipboardPasterCompletesAfterPasteWithoutWaitingForClipboardRestore()
    {
        using var restoreStarted = new ManualResetEventSlim();
        using var allowRestoreToFinish = new ManualResetEventSlim();
        using var restoreFinished = new ManualResetEventSlim();
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.Zero);
        var clipboard = new BlockingRestoreClipboardBackend(
            restoreStarted,
            allowRestoreToFinish,
            restoreFinished);
        var foregroundWindow = new FakeForegroundWindowBackend();
        var paster = new ClipboardPaster(clipboard, restoreQueue, foregroundWindow);
        paster.CaptureTarget();

        try
        {
            await paster.PasteAsync("finished paste", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual("finished paste", clipboard.PastedText);
            Assert.IsTrue(restoreStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(restoreFinished.IsSet, "Clipboard restoration should still be blocked.");
        }
        finally
        {
            allowRestoreToFinish.Set();
        }

        Assert.IsTrue(restoreFinished.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(ApartmentState.STA, clipboard.RestoreApartmentState);
    }

    [TestMethod]
    public async Task ClipboardPasterDoesNotCompleteBeforePasteFinishes()
    {
        using var pasteStarted = new ManualResetEventSlim();
        using var allowPasteToFinish = new ManualResetEventSlim();
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.Zero);
        var clipboard = new BlockingPasteClipboardBackend(pasteStarted, allowPasteToFinish);
        var foregroundWindow = new FakeForegroundWindowBackend();
        var paster = new ClipboardPaster(clipboard, restoreQueue, foregroundWindow);
        paster.CaptureTarget();

        var pasteTask = Task.Run(() => paster.PasteAsync("still pasting", CancellationToken.None));
        try
        {
            Assert.IsTrue(pasteStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(pasteTask.IsCompleted, "Processing must remain visible while paste is blocked.");
        }
        finally
        {
            allowPasteToFinish.Set();
        }

        await pasteTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ClipboardPasterDoesNotPasteWhenOriginalWindowCannotRegainFocus()
    {
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.Zero);
        var clipboard = new RecordingClipboardBackend();
        var foregroundWindow = new FakeForegroundWindowBackend();
        var paster = new ClipboardPaster(clipboard, restoreQueue, foregroundWindow);
        paster.CaptureTarget();
        foregroundWindow.CurrentWindow = (IntPtr)43;
        foregroundWindow.SetForegroundWindowResult = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => paster.PasteAsync("private dictated text", CancellationToken.None));

        Assert.AreEqual(0, clipboard.SetTextCount);
        Assert.AreEqual(0, clipboard.SendPasteCount);
    }

    [TestMethod]
    public async Task ClipboardPasterDoesNotPasteWhenFocusChangesAfterClipboardUpdate()
    {
        var restoreQueue = new RecordingRestoreQueue();
        var foregroundWindow = new FakeForegroundWindowBackend();
        var clipboard = new RecordingClipboardBackend(
            () => foregroundWindow.CurrentWindow = (IntPtr)43);
        var paster = new ClipboardPaster(clipboard, restoreQueue, foregroundWindow);
        paster.CaptureTarget();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => paster.PasteAsync("private dictated text", CancellationToken.None));

        Assert.AreEqual(1, clipboard.SetTextCount);
        Assert.AreEqual(0, clipboard.SendPasteCount);
        Assert.AreEqual(1, restoreQueue.ImmediateEnqueueCount);
        Assert.AreEqual(1, clipboard.RestoreCount);
    }

    [TestMethod]
    public void WindowsClipboardBackendClearsTranscriptWhenPriorClipboardWasEmpty()
    {
        var clipboard = new FakeWindowsClipboardApi
        {
            SequenceNumber = 7,
            Text = "private dictated text"
        };
        var backend = new WindowsClipboardPasteBackend(clipboard);

        backend.RestoreIfCurrent(7, previous: null);

        Assert.AreEqual(1, clipboard.ClearCount);
        Assert.AreEqual(0, clipboard.SetDataObjectCount);
    }

    [TestMethod]
    public void WindowsClipboardBackendPreservesNewerIdenticalClipboardContent()
    {
        var clipboard = new FakeWindowsClipboardApi
        {
            SequenceNumber = 8,
            Text = "private dictated text"
        };
        var backend = new WindowsClipboardPasteBackend(clipboard);

        backend.RestoreIfCurrent(7, new DataObject("previous clipboard contents"));

        Assert.AreEqual(0, clipboard.ClearCount);
        Assert.AreEqual(0, clipboard.SetDataObjectCount);
    }

    [TestMethod]
    public void WindowsClipboardBackendDetachesTextSnapshotBeforeReplacingClipboard()
    {
        var clipboard = new MutableTextWindowsClipboardApi("previous clipboard contents");
        var backend = new WindowsClipboardPasteBackend(clipboard);

        var snapshot = backend.GetDataObject();
        backend.SetText("private dictated text");

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(
            "previous clipboard contents",
            snapshot.GetData(DataFormats.UnicodeText, autoConvert: false));
    }

    [TestMethod]
    public async Task ClipboardPasterRapidPastesRestoreOriginalClipboardSnapshot()
    {
        using var restored = new ManualResetEventSlim();
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.FromMilliseconds(100));
        var clipboard = new StatefulClipboardBackend(restored);
        var foregroundWindow = new FakeForegroundWindowBackend();
        var paster = new ClipboardPaster(clipboard, restoreQueue, foregroundWindow);
        paster.CaptureTarget();

        await paster.PasteAsync("first transcript", CancellationToken.None);
        await paster.PasteAsync("second transcript", CancellationToken.None);

        Assert.IsTrue(restored.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreSame(clipboard.OriginalData, clipboard.CurrentData);
    }

    [TestMethod]
    public void AudioResidueCleanerDeletesOnlyAppOwnedWavFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ptt-residue-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var utterance = Path.Combine(directory, "utterance-20260901-120000-000.wav");
        var chunk = Path.Combine(directory, "chunk-20260901-120000-000-000.wav");
        var unrelatedWav = Path.Combine(directory, "meeting.wav");
        var unrelatedFile = Path.Combine(directory, "settings.json");

        try
        {
            File.WriteAllText(utterance, "private audio");
            File.WriteAllText(chunk, "private audio");
            File.WriteAllText(unrelatedWav, "keep");
            File.WriteAllText(unrelatedFile, "keep");

            AudioResidueCleaner.DeleteStaleFiles(directory);

            Assert.IsFalse(File.Exists(utterance));
            Assert.IsFalse(File.Exists(chunk));
            Assert.IsTrue(File.Exists(unrelatedWav));
            Assert.IsTrue(File.Exists(unrelatedFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void AudioResidueCleanerReportsLockedPrivateAudio()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ptt-residue-locked-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var lockedPath = Path.Combine(directory, "utterance-20260901-120000-000.wav");

        try
        {
            File.WriteAllText(lockedPath, "private audio");
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failures = AudioResidueCleaner.DeleteStaleFiles(directory);

                CollectionAssert.Contains(failures.ToArray(), lockedPath);
                Assert.IsTrue(File.Exists(lockedPath));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ClipboardRestoreQueueKeepsOnlyLatestSnapshotWhenRestoreStalls()
    {
        using var firstRestoreStarted = new ManualResetEventSlim();
        using var allowFirstRestoreToFinish = new ManualResetEventSlim();
        using var secondRestoreRan = new ManualResetEventSlim();
        using var thirdRestoreRan = new ManualResetEventSlim();
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.Zero);

        try
        {
            restoreQueue.Enqueue(() =>
            {
                firstRestoreStarted.Set();
                allowFirstRestoreToFinish.Wait();
            });
            Assert.IsTrue(firstRestoreStarted.Wait(TimeSpan.FromSeconds(2)));

            restoreQueue.Enqueue(secondRestoreRan.Set);
            restoreQueue.Enqueue(thirdRestoreRan.Set);

            allowFirstRestoreToFinish.Set();
            Assert.IsTrue(thirdRestoreRan.Wait(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(secondRestoreRan.IsSet, "Only the latest pending clipboard snapshot should be retained.");
        }
        finally
        {
            allowFirstRestoreToFinish.Set();
        }
    }

    [TestMethod]
    public void ClipboardRestoreQueueImmediateWorkInterruptsPendingDelay()
    {
        using var delayedRestoreRan = new ManualResetEventSlim();
        using var immediateRestoreRan = new ManualResetEventSlim();
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.FromSeconds(5));

        restoreQueue.Enqueue(delayedRestoreRan.Set);
        Thread.Sleep(100);
        restoreQueue.EnqueueImmediate(immediateRestoreRan.Set);

        Assert.IsTrue(
            immediateRestoreRan.Wait(TimeSpan.FromMilliseconds(500)),
            "Failed-paste cleanup should interrupt the normal restore delay.");
        Assert.IsFalse(delayedRestoreRan.IsSet, "Immediate cleanup should replace obsolete pending work.");
    }

    [TestMethod]
    public void PersistentServerResponsePreservesWordsAndRemovesEndOfUtteranceToken()
    {
        const string json = """
            {
              "text": "count one two three<EOU>",
              "words": [
                { "word": "count", "start": 0.16, "end": 0.40, "conf": 0.98 },
                { "word": "three<EOU>", "start": 0.80, "end": 1.20, "conf": 0.91 }
              ]
            }
            """;

        var result = PersistentParakeetServerTranscriber.ParseResponse(json, TimeSpan.FromMilliseconds(140));

        Assert.AreEqual("count one two three", result.Text);
        Assert.AreEqual(TimeSpan.FromMilliseconds(140), result.InferenceTime);
        Assert.AreEqual(2, result.Words.Count);
        Assert.AreEqual("three", result.Words[1].Text);
        Assert.AreEqual(TimeSpan.FromSeconds(0.80), result.Words[1].Start);
        Assert.AreEqual(0.91, result.Words[1].Confidence);
    }

    [TestMethod]
    public async Task PersistentServerContinuesAfterEveryEndOfUtterance()
    {
        var responses = new Queue<PersistentParakeetServerTranscriber.ServerTranscriptSegment>(
        [
            Segment("one", TimeSpan.FromSeconds(2)),
            Segment("two", TimeSpan.FromSeconds(2)),
            Segment("three", null)
        ]);
        var submittedLengths = new List<int>();

        var result = await PersistentParakeetServerTranscriber.TranscribeAllUtterancesAsync(
            CreatePcmWave(TimeSpan.FromSeconds(6)),
            (wav, _) =>
            {
                submittedLengths.Add(wav.Length);
                return Task.FromResult(responses.Dequeue());
            },
            CancellationToken.None);

        Assert.AreEqual("one two three", result.Text);
        CollectionAssert.AreEqual(
            new[] { "one", "two", "three" },
            result.Words.Select(word => word.Text).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0.1, 2.1, 4.1 },
            result.Words.Select(word => word.Start.TotalSeconds).ToArray());
        Assert.AreEqual(3, submittedLengths.Count);
        Assert.IsTrue(submittedLengths[0] > submittedLengths[1]);
        Assert.IsTrue(submittedLengths[1] > submittedLengths[2]);
        Assert.AreEqual(0, responses.Count);
    }

    [TestMethod]
    public void StatusOverlayUsesNonActivatingTopmostWindow()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            Assert.IsTrue(overlay.ShowWithoutActivationForTest);
            Assert.IsTrue((overlay.ExtendedWindowStyleForTest & StatusOverlayForm.NoActivateExtendedStyleForTest) != 0);
            Assert.IsTrue(overlay.TopMost);
            Assert.IsFalse(overlay.ShowInTaskbar);
            Assert.AreEqual(FormStartPosition.Manual, overlay.StartPosition);
        });
    }

    [TestMethod]
    public void SettingsFormBuildsSelectedTranscriptionMode()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            form.SelectedModelIdForTest = "realtime-eou-120m-v1-q8_0";
            form.SelectedTranscriptionModeForTest = TranscriptionMode.Streaming;
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual("realtime-eou-120m-v1-q8_0", settings.SelectedModelId);
            Assert.AreEqual(TranscriptionMode.Streaming, settings.TranscriptionMode);
        });
    }

    [TestMethod]
    public void SettingsFormClearsAutoModelPathWhenBuiltInModelChanges()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default with
            {
                SelectedModelId = ModelRegistry.DefaultModelId,
                ModelPath = "C:\\Users\\stewa\\AppData\\Local\\PttDictation\\models\\tdt_ctc-110m-f16.gguf"
            });

            form.SelectedModelIdForTest = "realtime-eou-120m-v1-q8_0";
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual("realtime-eou-120m-v1-q8_0", settings.SelectedModelId);
            Assert.IsNull(settings.ModelPath);
        });
    }

    [TestMethod]
    public void SettingsFormFallsBackToAutoWhenStreamingModeIsNotValidForSelectedModel()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            form.SelectedTranscriptionModeForTest = TranscriptionMode.Streaming;
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual(ModelRegistry.DefaultModelId, settings.SelectedModelId);
            Assert.AreEqual(TranscriptionMode.Auto, settings.TranscriptionMode);
        });
    }

    [TestMethod]
    public void TrayPresentsSettingsImmediatelyFromCurrentSettings()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            var settings = AppSettings.Default with
            {
                SelectedModelId = "realtime-eou-120m-v1-f16",
                HoldHotkey = DictationHotkey.RightControl,
                ToggleHotkey = DictationHotkey.RightShift
            };

            var presented = TrayApplicationContext.PresentSettingsForm(
                form,
                () => throw new InvalidOperationException("The existing form should be reused."),
                settings);

            Assert.AreSame(form, presented);
            Assert.IsTrue(form.Visible);
            Assert.AreEqual("realtime-eou-120m-v1-f16", form.SelectedModelIdForTest);
            Assert.AreEqual(DictationHotkey.RightControl, form.SelectedHoldHotkeyForTest);
            Assert.AreEqual(DictationHotkey.RightShift, form.SelectedToggleHotkeyForTest);
        });
    }

    [TestMethod]
    public void TrayRecreatesDisposedSettingsFormBeforePresenting()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            var disposedForm = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            disposedForm.Dispose();
            SettingsForm? replacement = null;
            try
            {
                replacement = TrayApplicationContext.PresentSettingsForm(
                    disposedForm,
                    () => new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault()),
                    AppSettings.Default);

                Assert.IsNotNull(replacement);
                Assert.AreNotSame(disposedForm, replacement);
                Assert.IsTrue(replacement.Visible);
            }
            finally
            {
                replacement?.Dispose();
            }
        });
    }

    [TestMethod]
    public void TrayOpenSettingsMenuWorksOnFirstClickAndAfterDisposedForm()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            TrayApplicationContext? context = null;
            var firstFormWasOwnedWhenShown = false;
            context = new TrayApplicationContext(() =>
            {
                var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
                form.Shown += (_, _) =>
                {
                    firstFormWasOwnedWhenShown = ReferenceEquals(context!.SettingsFormForTest, form);
                };
                return form;
            });
            try
            {
                var notifyIcon = (NotifyIcon?)typeof(TrayApplicationContext)
                    .GetField("_notifyIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(context);
                Assert.IsNotNull(notifyIcon?.ContextMenuStrip);
                var openSettings = notifyIcon.ContextMenuStrip.Items
                    .OfType<ToolStripMenuItem>()
                    .Single(item => item.Text == "Open Settings");

                openSettings.PerformClick();
                Application.DoEvents();

                var firstForm = (SettingsForm?)typeof(TrayApplicationContext)
                    .GetField("_settingsForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(context);
                Assert.IsNotNull(firstForm);
                Assert.IsTrue(firstForm.Visible);
                Assert.IsTrue(firstFormWasOwnedWhenShown);

                firstForm.Dispose();
                openSettings.PerformClick();
                Application.DoEvents();

                var secondForm = (SettingsForm?)typeof(TrayApplicationContext)
                    .GetField("_settingsForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(context);
                Assert.IsNotNull(secondForm);
                Assert.AreNotSame(firstForm, secondForm);
                Assert.IsTrue(secondForm.Visible);
            }
            finally
            {
                typeof(TrayApplicationContext)
                    .GetMethod("ExitApplication", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(context, null);
            }
        });
    }

    [TestMethod]
    public void SettingsFormBuildsIndependentHoldAndToggleHotkeys()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            form.SelectedHoldHotkeyForTest = DictationHotkey.F8;
            form.SelectedToggleHotkeyForTest = DictationHotkey.F9;
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual(DictationHotkey.F8, settings.HoldHotkey);
            Assert.AreEqual(DictationHotkey.F9, settings.ToggleHotkey);
            StringAssert.Contains(form.SummaryTextForTest, "F8");
            StringAssert.Contains(form.SummaryTextForTest, "F9");
            Assert.IsTrue(form.HotkeySelectorsUseDarkFlatStyleForTest);
            Assert.IsFalse(form.HasRuntimePathEditorForTest);
            Assert.IsFalse(form.HasModelPathEditorForTest);
        });
    }

    [TestMethod]
    public void SettingsFormRejectsMatchingHoldAndToggleHotkeys()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            form.SelectedHoldHotkeyForTest = DictationHotkey.F8;
            form.SelectedToggleHotkeyForTest = DictationHotkey.F8;

            Assert.ThrowsExactly<InvalidOperationException>(() => form.BuildSettingsForTest());
        });
    }

    [TestMethod]
    public void SettingsFormUsesResponsiveDarkSectionsAtMinimumSize()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            form.Show();
            Application.DoEvents();
            form.Size = form.MinimumSize;
            Application.DoEvents();
            form.PerformLayout();

            CollectionAssert.AreEqual(
                new[] { "Recording", "Transcription", "Corrections" },
                form.SectionTitlesForTest);
            Assert.IsTrue(form.SelectorsUseDarkFlatStyleForTest);
            Assert.AreEqual(DarkTheme.Surface, form.SummaryBackColorForTest);
            Assert.AreEqual(DarkTheme.Accent, form.SaveBackColorForTest);
            Assert.IsTrue(form.PrimarySectionsFitContentForTest, form.ContentLayoutForTest);
            Assert.IsFalse(form.ContentHasHorizontalScrollForTest, form.ContentLayoutForTest);
            AssertControlInsideClient(form, form.QuitButtonForTest);
            AssertControlInsideClient(form, form.CancelButtonForTest);
            AssertControlInsideClient(form, form.SaveButtonForTest);
            AssertButtonTextFits(form.ModelDownloadButtonForTest);
            AssertButtonTextFits(form.QuitButtonForTest);
            AssertButtonTextFits(form.CancelButtonForTest);
            AssertButtonTextFits(form.SaveButtonForTest);

            var previewPath = Environment.GetEnvironmentVariable("PARAKEET_SETTINGS_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                preview.Save(previewPath);
            }
        });
    }

    [TestMethod]
    public void SettingsFormOpensAtAComfortableDefaultWithinDesktopLimits()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());

            var maximumWindowSize = SystemInformation.MaxWindowTrackSize;
            Assert.IsGreaterThanOrEqualTo(Math.Min(1100, maximumWindowSize.Width), form.Width);
            Assert.IsGreaterThanOrEqualTo(Math.Min(900, maximumWindowSize.Height), form.Height);
            Assert.IsGreaterThanOrEqualTo(800, form.MinimumSize.Width);
            Assert.IsGreaterThanOrEqualTo(700, form.MinimumSize.Height);
        });
    }

    [TestMethod]
    public void SettingsFormStacksSectionsAtHighDpi()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);
            form.Show();
            Application.DoEvents();
            form.Size = form.MinimumSize;
            Application.DoEvents();
            form.ApplyHighDpiLayoutForTest();
            form.PerformLayout();

            Assert.AreEqual(1, form.PrimarySectionColumnCountForTest);
            Assert.AreEqual(1, form.CorrectionColumnCountForTest);
            Assert.IsTrue(form.PrimarySectionsFitContentForTest, form.ContentLayoutForTest);
            Assert.IsFalse(form.ContentHasHorizontalScrollForTest, form.ContentLayoutForTest);
            AssertControlInsideClient(form, form.QuitButtonForTest);
            AssertControlInsideClient(form, form.CancelButtonForTest);
            AssertControlInsideClient(form, form.SaveButtonForTest);
        });
    }

    [TestMethod]
    public void SettingsFormUsesTwoColumnsWhenWide()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);
            form.ApplyWideLayoutForTest();

            Assert.AreEqual(2, form.PrimarySectionColumnCountForTest);
            Assert.AreEqual(2, form.CorrectionColumnCountForTest);
        });
    }

    [TestMethod]
    public void SettingsFormDefaultLayoutAlignsCorrectionColumnsWithoutWindowScrolling()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default with
            {
                TranscriptCorrections =
                [
                    new TranscriptCorrection("quinn", "qwen"),
                    new TranscriptCorrection("quin", "qwen"),
                    new TranscriptCorrection("stuart", "stewart"),
                    new TranscriptCorrection("steward", "stewart"),
                    new TranscriptCorrection("blm", "vlm")
                ]
            });

            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            Application.DoEvents();

            Assert.AreEqual(2, form.CorrectionColumnCountForTest);
            Assert.IsFalse(
                form.ContentHasVerticalScrollForTest,
                $"{form.ContentLayoutForTest}, fields={form.CorrectionFieldsBoundsForTest}, preview={form.CorrectionPreviewBoundsForTest}");
            Assert.AreEqual(form.CorrectionFieldsBoundsForTest.Top, form.CorrectionPreviewBoundsForTest.Top);
            Assert.AreEqual(form.CorrectionFieldsBoundsForTest.Bottom, form.CorrectionPreviewBoundsForTest.Bottom);

            var previewPath = Environment.GetEnvironmentVariable("PARAKEET_SETTINGS_WIDE_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                preview.Save(previewPath);
            }
        });
    }

    [TestMethod]
    public void SettingsFormDisablesModelDownloadWhenSelectedModelIsPresent()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ModelRegistry.DefaultModelId
            };
            using var form = new SettingsForm(
                new AppSettingsStore(path),
                ModelRegistry.CreateDefault(),
                (_, _) => Task.FromResult("downloaded.gguf"),
                model => downloaded.Contains(model.Id));
            form.UseSettings(AppSettings.Default);

            Assert.IsFalse(form.ModelDownloadEnabledForTest);
            Assert.AreEqual("Downloaded", form.ModelDownloadTextForTest);

            form.SelectedModelIdForTest = "tdt-0.6b-v3-f16";

            Assert.IsTrue(form.ModelDownloadEnabledForTest);
            Assert.AreEqual("Download", form.ModelDownloadTextForTest);
        });
    }

    [TestMethod]
    public void SettingsFormDownloadButtonSavesSelectedModelPath()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var form = new SettingsForm(
                new AppSettingsStore(path),
                ModelRegistry.CreateDefault(),
                (model, _) =>
                {
                    downloaded.Add(model.Id);
                    return Task.FromResult($"C:\\models\\{Path.GetFileName(model.DownloadUrl.LocalPath)}");
                },
                model => downloaded.Contains(model.Id));
            form.UseSettings(AppSettings.Default);

            form.SelectedModelIdForTest = "tdt-0.6b-v3-f16";
            form.DownloadSelectedModelForTest();
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual("tdt-0.6b-v3-f16", settings.SelectedModelId);
            Assert.AreEqual("C:\\models\\tdt-0.6b-v3-f16.gguf", settings.ModelPath);
            Assert.IsFalse(form.ModelDownloadEnabledForTest);
            Assert.AreEqual("Downloaded", form.ModelDownloadTextForTest);
        });
    }

    [TestMethod]
    public void SettingsFormDisablesModelSelectorWhileDownloadIsInFlight()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            var download = new TaskCompletionSource<string>();
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var form = new SettingsForm(
                new AppSettingsStore(path),
                ModelRegistry.CreateDefault(),
                (model, _) =>
                {
                    downloaded.Add(model.Id);
                    return download.Task;
                },
                model => downloaded.Contains(model.Id));
            form.UseSettings(AppSettings.Default);
            form.SelectedModelIdForTest = "tdt-0.6b-v3-f16";

            var task = form.DownloadSelectedModelTaskForTest();
            Application.DoEvents();

            Assert.IsFalse(form.ModelSelectorEnabledForTest);

            download.SetResult("C:\\models\\tdt-0.6b-v3-f16.gguf");
            while (!task.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            task.GetAwaiter().GetResult();

            Assert.IsTrue(form.ModelSelectorEnabledForTest);
        });
    }

    [TestMethod]
    public void SettingsFormKeepsDownloadFailureVisible()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(
                new AppSettingsStore(path),
                ModelRegistry.CreateDefault(),
                (_, _) => throw new InvalidOperationException("network unavailable"),
                _ => false);
            form.UseSettings(AppSettings.Default);

            form.DownloadSelectedModelForTest();

            StringAssert.Contains(form.ModelStatusTextForTest, "Download failed");
            StringAssert.Contains(form.ModelStatusTextForTest, "network unavailable");
            Assert.IsTrue(form.ModelDownloadEnabledForTest);
            Assert.AreEqual("Download", form.ModelDownloadTextForTest);
        });
    }

    [TestMethod]
    public void SettingsFormTreatsExistingPersistedModelPathAsDownloaded()
    {
        RunOnStaThread(() =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            var modelPath = Path.Combine(Path.GetTempPath(), $"parakeet-model-{Guid.NewGuid():N}.gguf");
            File.WriteAllText(modelPath, "custom model");

            try
            {
                using var form = new SettingsForm(
                    new AppSettingsStore(settingsPath),
                    ModelRegistry.CreateDefault(),
                    (_, _) => Task.FromResult(modelPath),
                    _ => false);
                form.UseSettings(AppSettings.Default with
                {
                    SelectedModelId = "tdt-0.6b-v3-f16",
                    ModelPath = modelPath
                });

                Assert.IsFalse(form.ModelDownloadEnabledForTest);
                Assert.AreEqual("Downloaded", form.ModelDownloadTextForTest);
            }
            finally
            {
                File.Delete(modelPath);
            }
        });
    }

    [TestMethod]
    public void SettingsFormBuildsTranscriptCorrectionsAndShowsPreview()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default with
            {
                TranscriptCorrections =
                [
                    new TranscriptCorrection("kuda", "CUDA")
                ]
            });

            form.CorrectionPreviewInputForTest = "kuda likes c sharp";

            Assert.AreEqual("CUDA likes c sharp", form.CorrectionPreviewOutputForTest);

            form.SetCorrectionDraftForTest("c sharp", "C#");
            form.AddCorrectionForTest();
            form.CorrectionPreviewInputForTest = "kuda likes c sharp";
            var settings = form.BuildSettingsForTest();

            Assert.AreEqual("CUDA likes C#", form.CorrectionPreviewOutputForTest);
            Assert.AreEqual(2, settings.TranscriptCorrections.Count);
            Assert.AreEqual("c sharp", settings.TranscriptCorrections[1].HeardAs);
            Assert.AreEqual("C#", settings.TranscriptCorrections[1].ReplaceWith);
        });
    }

    [TestMethod]
    public void SettingsFormCorrectionEditorShowsRulesAndKeepsEditorAndPreviewInSync()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default with
            {
                TranscriptCorrections =
                [
                    new TranscriptCorrection("quinn", "qwen"),
                    new TranscriptCorrection("quin", "qwen"),
                    new TranscriptCorrection("stuart", "stewart"),
                    new TranscriptCorrection("steward", "stewart")
                ]
            });
            form.Show();
            Application.DoEvents();

            CollectionAssert.AreEqual(
                new[] { "Heard as", "Replace with" },
                form.CorrectionColumnHeadersForTest);
            Assert.IsGreaterThanOrEqualTo(5, form.CorrectionVisibleRowCapacityForTest);

            form.SelectCorrectionForTest(2);

            Assert.AreEqual("stuart", form.CorrectionHeardAsForTest);
            Assert.AreEqual("stewart", form.CorrectionReplaceWithForTest);
            Assert.AreEqual("Update rule", form.CorrectionActionTextForTest);

            form.StartNewCorrectionForTest();
            form.SetCorrectionDraftForTest("stork", "Stewart");
            form.CorrectionPreviewInputForTest = "Trying stork again. Go to the store.";

            Assert.AreEqual(
                "Trying Stewart again. Go to the store.",
                form.CorrectionPreviewOutputForTest);

            form.AddCorrectionForTest();

            Assert.AreEqual(5, form.CorrectionRuleCountForTest);
            Assert.AreEqual(string.Empty, form.CorrectionHeardAsForTest);
            Assert.AreEqual(string.Empty, form.CorrectionReplaceWithForTest);
            Assert.AreEqual("Add rule", form.CorrectionActionTextForTest);
            Assert.AreEqual(
                "Trying Stewart again. Go to the store.",
                form.CorrectionPreviewOutputForTest);

            var previewPath = Environment.GetEnvironmentVariable("PARAKEET_CORRECTIONS_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                var editor = form.CorrectionEditorForTest;
                using var preview = new Bitmap(editor.Width, editor.Height);
                editor.DrawToBitmap(preview, new Rectangle(Point.Empty, editor.Size));
                preview.Save(previewPath);
            }
        });
    }

    [TestMethod]
    public void SettingsFormSavePersistsCorrectionRulesWithoutClosingSettings()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            try
            {
                var store = new AppSettingsStore(path);
                using var form = new SettingsForm(store, ModelRegistry.CreateDefault());
                form.UseSettings(AppSettings.Default);
                form.Show();
                Application.DoEvents();
                form.SetCorrectionDraftForTest("steward", "Stewart");

                form.SaveForTest();

                Assert.IsTrue(form.Visible);
                StringAssert.Contains(form.SaveStatusTextForTest, "Saved");
                var saved = store.Load();
                Assert.AreEqual(1, saved.TranscriptCorrections.Count);
                Assert.AreEqual("steward", saved.TranscriptCorrections[0].HeardAs);
                Assert.AreEqual("Stewart", saved.TranscriptCorrections[0].ReplaceWith);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [TestMethod]
    public void StatusOverlayAutoHidesExceptionalCompletionStatesWithoutShowingWindow()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.EmptyTranscript);

            Assert.AreEqual("No speech detected", overlay.TitleTextForTest);
            Assert.AreEqual("Nothing was pasted.", overlay.MessageTextForTest);
            Assert.IsTrue(overlay.AutoHideTimerEnabledForTest);
            Assert.IsFalse(overlay.Visible);
            Assert.AreEqual(StatusOverlayForm.DefaultSizeForTest, overlay.Size);
        });
    }

    [TestMethod]
    public void StatusOverlayKeepsListeningTextAwayFromClippedEdges()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            Assert.AreEqual(ContentAlignment.MiddleLeft, overlay.TitleAlignmentForTest);
            Assert.AreEqual(ContentAlignment.MiddleLeft, overlay.MessageAlignmentForTest);
            Assert.AreEqual("Recording 00:00" + Environment.NewLine + "Release to transcribe", overlay.MessageTextForTest);
            Assert.IsTrue(overlay.TitleHeightForTest >= overlay.TitlePreferredHeightForTest + 10);
            Assert.IsTrue(overlay.MessageHeightForTest >= overlay.MessagePreferredHeightForTest + 10);
        });
    }

    [TestMethod]
    public void StatusOverlayReservesRoomForCompleteTranscriptAboveActivityMeter()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            Assert.AreEqual(560, StatusOverlayForm.ListeningSizeForTest.Width);
            Assert.AreEqual(StatusOverlayForm.ListeningSizeForTest, overlay.Size);
            Assert.AreEqual(134, overlay.ActivityMeterHeightForTest);
            Assert.IsTrue(overlay.TextPanelHeightForTest >= overlay.TitlePreferredHeightForTest + overlay.MessagePreferredHeightForTest + 20);
            Assert.IsTrue(overlay.ActivityMeterTopForTest >= overlay.TextPanelBottomForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayCanHideAfterPasteCompletes()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);
            overlay.Show();
            Assert.IsTrue(overlay.Visible);

            overlay.HideRecording();

            Assert.IsFalse(overlay.Visible);
            Assert.IsFalse(overlay.LiveActivityTimerEnabledForTest);
            Assert.IsFalse(overlay.ActivityMeterVisibleForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayExplainsThatCancelledRecordingWasDiscarded()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.DictationCancelled);

            Assert.AreEqual("Dictation cancelled", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "discarded");
            StringAssert.Contains(overlay.MessageTextForTest, "Nothing was pasted");
            Assert.IsTrue(overlay.AutoHideTimerEnabledForTest);
            SaveOverlayPreviewIfRequested(overlay, "PTT_CANCELLED_PREVIEW_PATH");
        });
    }

    [TestMethod]
    public void CompletedPasteRemainsVisibleForAnotherQuarterSecond()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(250),
            DictationPresentation.PostPasteVisibilityDurationForTest);
    }

    [TestMethod]
    public void ListeningStatusFormatterShowsElapsedTimeAndReleaseHint()
    {
        var text = ListeningStatusFormatter.Format(TimeSpan.FromMinutes(61) + TimeSpan.FromSeconds(5));

        Assert.AreEqual("Recording 61:05" + Environment.NewLine + "Release to transcribe", text);
    }

    [TestMethod]
    public void ListeningStatusFormatterShowsToggleHintWithoutRelease()
    {
        var text = ListeningStatusFormatter.Format(
            TimeSpan.FromSeconds(9),
            ListeningTriggerMode.Toggle);

        Assert.AreEqual("Recording 00:09" + Environment.NewLine + "Press Right Shift to transcribe", text);
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("Release"));
    }

    [TestMethod]
    public void ListeningStatusFormatterUsesConfiguredHotkeyName()
    {
        var holdText = ListeningStatusFormatter.Format(
            TimeSpan.FromSeconds(2),
            ListeningTriggerMode.PushToTalk,
            "F8");
        var toggleText = ListeningStatusFormatter.Format(
            TimeSpan.FromSeconds(2),
            ListeningTriggerMode.Toggle,
            "F9");

        StringAssert.Contains(holdText, "Release F8 to transcribe");
        StringAssert.Contains(toggleText, "Press F9 to transcribe");
    }

    [TestMethod]
    public void StatusOverlayKeepsConfiguredToggleKeyInLiveTranscriptHint()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening, ListeningTriggerMode.Toggle, "F9");

            overlay.ApplyListeningTranscriptForTest("Testing configurable keys.", ListeningTriggerMode.Toggle);

            StringAssert.Contains(overlay.MessageTextForTest, "Press F9 to transcribe");
        });
    }

    [TestMethod]
    public void StatusOverlayShowsToggleListeningTextWithoutRelease()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening, ListeningTriggerMode.Toggle);

            Assert.AreEqual("Recording 00:00" + Environment.NewLine + "Press Right Shift to transcribe", overlay.MessageTextForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayKeepsToggleRecordingVisibleWhenPartialTranscriptArrives()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening, ListeningTriggerMode.Toggle);

            overlay.ApplyListeningTranscriptForTest("Test two. Test three.", ListeningTriggerMode.Toggle);

            StringAssert.StartsWith(overlay.TitleTextForTest, "Recording 00:00");
            StringAssert.Contains(overlay.MessageTextForTest, "Press Right Shift to transcribe");
            StringAssert.Contains(overlay.MessageTextForTest, "Test two. Test three.");
            Assert.IsTrue(overlay.LiveActivityTimerEnabledForTest);
            Assert.IsTrue(overlay.ActivityMeterVisibleForTest);
            Assert.IsFalse(overlay.AutoHideTimerEnabledForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayShowsTheCompletePartialTranscriptWithoutStoppingRecording()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening, ListeningTriggerMode.Toggle);

            overlay.ApplyListeningTranscriptForTest(
                "The beginning and every word in the middle should remain visible while the final spoken words are still arriving on screen",
                ListeningTriggerMode.Toggle);

            StringAssert.Contains(overlay.MessageTextForTest, "The beginning");
            StringAssert.Contains(overlay.MessageTextForTest, "every word in the middle");
            StringAssert.Contains(overlay.MessageTextForTest, "final spoken words are still arriving on screen");
            Assert.IsFalse(overlay.MessageAutoEllipsisForTest);
            Assert.IsTrue(overlay.MessageHeightForTest >= overlay.MessagePreferredHeightForTest);
            Assert.IsTrue(overlay.LiveActivityTimerEnabledForTest);

            SaveOverlayPreviewIfRequested(overlay, "PTT_RECORDING_PREVIEW_PATH");
        });
    }

    [TestMethod]
    public void StatusOverlayTracksProcessingInPlaceUntilPasteCompletes()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);
            overlay.ApplyListeningTranscriptForTest(
                "The full accumulated transcript remains visible while final transcription finishes.",
                ListeningTriggerMode.PushToTalk);
            overlay.Show();
            var recordingSize = overlay.Size;

            overlay.ShowProcessing();

            Assert.IsTrue(overlay.Visible);
            Assert.AreEqual(recordingSize, overlay.Size);
            Assert.AreEqual("Processing", overlay.TitleTextForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Transcribing and preparing to paste");
            StringAssert.Contains(overlay.MessageTextForTest, "full accumulated transcript remains visible");
            Assert.IsFalse(overlay.LiveActivityTimerEnabledForTest);

            overlay.ShowProcessingDetail("Loading the selected model locally.");

            StringAssert.Contains(overlay.MessageTextForTest, "Loading the selected model locally.");
            StringAssert.Contains(overlay.MessageTextForTest, "full accumulated transcript remains visible");

            SaveOverlayPreviewIfRequested(overlay, "PTT_PROCESSING_PREVIEW_PATH");
        });
    }

    [TestMethod]
    public void BackgroundModelWarmUpDoesNotReplaceTheRecordingTranscript()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);
            overlay.ApplyListeningTranscriptForTest(
                "The recording transcript must stay visible during model warmup.",
                ListeningTriggerMode.PushToTalk);
            var recordingMessage = overlay.MessageTextForTest;
            overlay.Show();

            overlay.ShowProcessingDetail("Loading the selected model locally.");

            Assert.AreEqual("Recording 00:00", overlay.TitleTextForTest);
            Assert.AreEqual(recordingMessage, overlay.MessageTextForTest);
        });
    }

    [TestMethod]
    public void AudioLevelCalculatorConvertsPcmSamplesToNormalizedPeak()
    {
        var pcm = new byte[6];
        WriteInt16(pcm, 0, 0);
        WriteInt16(pcm, 2, short.MaxValue / 2);
        WriteInt16(pcm, 4, short.MinValue);

        var level = AudioLevelCalculator.CalculatePeakLevel(pcm);

        Assert.AreEqual(1.0, level, 0.001);
    }

    [TestMethod]
    public void AudioLevelCalculatorHandlesSilenceAndOddByteCount()
    {
        var level = AudioLevelCalculator.CalculatePeakLevel([0, 0, 255]);

        Assert.AreEqual(0, level);
    }

    [TestMethod]
    public void PcmChunkBufferWaitsForEnoughAudioBeforeCreatingChunk()
    {
        var buffer = new PcmChunkBuffer(bytesPerSecond: 4, chunkBytes: 8, overlapBytes: 2);

        buffer.Append([1, 2, 3, 4]);

        Assert.IsNull(buffer.TryCreateChunk("chunk.wav"));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, buffer.ToArray());
    }

    [TestMethod]
    public void PcmChunkBufferCreatesOverlappedChunks()
    {
        var buffer = new PcmChunkBuffer(bytesPerSecond: 4, chunkBytes: 8, overlapBytes: 2);

        buffer.Append([1, 2, 3, 4, 5, 6, 7, 8]);
        var first = buffer.TryCreateChunk("chunk-1.wav");
        buffer.Append([9, 10, 11, 12, 13, 14]);
        var second = buffer.TryCreateChunk("chunk-2.wav");

        Assert.IsNotNull(first);
        Assert.AreEqual("chunk-1.wav", first.Path);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, first.Pcm);
        Assert.AreEqual(TimeSpan.FromSeconds(2), first.Duration);
        Assert.AreEqual(TimeSpan.Zero, first.OverlapDuration);
        Assert.IsNotNull(second);
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 }, second.Pcm);
        Assert.AreEqual(TimeSpan.FromSeconds(2), second.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), second.OverlapDuration);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 }, buffer.ToArray());
    }

    [TestMethod]
    public void PcmChunkBufferKeepsChunksFixedSizeAfterLargeAppend()
    {
        var buffer = new PcmChunkBuffer(bytesPerSecond: 4, chunkBytes: 8, overlapBytes: 2);

        buffer.Append([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        var first = buffer.TryCreateChunk("chunk-1.wav");
        var second = buffer.TryCreateChunk("chunk-2.wav");
        var third = buffer.TryCreateChunk("chunk-3.wav");

        Assert.IsNotNull(first);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, first.Pcm);
        Assert.AreEqual(TimeSpan.Zero, first.OverlapDuration);
        Assert.IsNotNull(second);
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 }, second.Pcm);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), second.OverlapDuration);
        Assert.IsNull(third);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, buffer.ToArray());
    }

    [TestMethod]
    public void AudioChunkPublisherDeletesChunkWhenHandlerIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parakeet-late-chunk-{Guid.NewGuid():N}.wav");
        var deletedPaths = new List<string>();

        AudioChunkPublisher.Publish(
            new PendingAudioChunk(path, [1, 2, 3, 4], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250)),
            handler: null,
            writeWav: (chunkPath, pcm) => File.WriteAllBytes(chunkPath, pcm),
            delete: chunkPath =>
            {
                deletedPaths.Add(chunkPath);
                File.Delete(chunkPath);
            });

        CollectionAssert.AreEqual(new[] { path }, deletedPaths.ToArray());
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void AudioChunkPublisherPublishesWrittenChunkWhenHandlerExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parakeet-published-chunk-{Guid.NewGuid():N}.wav");
        RecordedAudio? published = null;

        try
        {
            AudioChunkPublisher.Publish(
                new PendingAudioChunk(path, [1, 2, 3, 4], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250)),
                audio => published = audio,
                writeWav: (chunkPath, pcm) => File.WriteAllBytes(chunkPath, pcm),
                delete: File.Delete);

            Assert.IsNotNull(published);
            Assert.AreEqual(path, published.Path);
            Assert.AreEqual(TimeSpan.FromSeconds(1), published.Duration);
            Assert.AreEqual(TimeSpan.FromMilliseconds(250), published.OverlapDuration);
            Assert.IsTrue(published.DeleteAfterUse);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void StatusOverlayRunsLiveActivityOnlyWhileListening()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            Assert.IsTrue(overlay.LiveActivityTimerEnabledForTest);
            Assert.IsTrue(overlay.ActivityMeterVisibleForTest);
            StringAssert.Contains(overlay.MessageTextForTest, "Recording 00:00");

            overlay.HideRecording();

            Assert.IsFalse(overlay.LiveActivityTimerEnabledForTest);
            Assert.IsFalse(overlay.ActivityMeterVisibleForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayActivityMeterStoresLatestMicrophoneLevel()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            overlay.UpdateActivityLevelForTest(0.75);

            Assert.AreEqual(0.75, overlay.LatestActivityLevelForTest, 0.001);
        });
    }

    [TestMethod]
    public void StatusOverlayActivityMeterUsesFixedVerticalResponseProfile()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            overlay.UpdateActivityLevelForTest(0.75);
            var firstProfile = overlay.ActivityMeterBarHeightsForTest;

            overlay.UpdateActivityLevelForTest(0.75);
            var secondProfile = overlay.ActivityMeterBarHeightsForTest;

            var center = firstProfile.Length / 2;
            Assert.IsTrue(firstProfile[center] > firstProfile[0]);
            Assert.IsTrue(firstProfile[center] > firstProfile[^1]);
            CollectionAssert.AreEqual(firstProfile, secondProfile);
        });
    }

    [TestMethod]
    public void StatusOverlayDecaysMicrophoneLevelInsteadOfAnimatingFakeMotion()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);
            overlay.UpdateActivityLevelForTest(0.8);

            overlay.AdvanceLiveActivityForTest();

            Assert.IsTrue(overlay.LatestActivityLevelForTest < 0.8);
            Assert.IsTrue(overlay.LatestActivityLevelForTest > 0);
        });
    }

    [TestMethod]
    public void StatusOverlayIgnoresMicrophoneLevelWhenNotListening()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.HideRecording();
            overlay.UpdateActivityLevelForTest(0.75);

            Assert.AreEqual(0, overlay.LatestActivityLevelForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayResetsActivityMeterForEachListeningSession()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);
            overlay.UpdateActivityLevelForTest(0.75);

            overlay.HideRecording();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            Assert.AreEqual(0, overlay.LatestActivityLevelForTest);
            Assert.IsFalse(overlay.HasActivityHistoryForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayPositionsAtBottomCenterOfWorkingArea()
    {
        var location = StatusOverlayForm.CalculateBottomCenterLocationForTest(
            new Rectangle(100, 50, 1200, 800),
            StatusOverlayForm.DefaultSizeForTest);

        Assert.AreEqual(new Point(420, 670), location);
    }

    [TestMethod]
    public void StatusOverlayListeningPositionAccountsForTallActivityMeter()
    {
        var location = StatusOverlayForm.CalculateBottomCenterLocationForTest(
            new Rectangle(100, 50, 1200, 800),
            StatusOverlayForm.ListeningSizeForTest);

        Assert.AreEqual(new Point(420, 504), location);
    }

    [TestMethod]
    public void StatusOverlayPositionStaysInsideNarrowWorkingArea()
    {
        var location = StatusOverlayForm.CalculateBottomCenterLocationForTest(
            new Rectangle(100, 50, 420, 800),
            StatusOverlayForm.DefaultSizeForTest);

        Assert.AreEqual(new Point(120, 670), location);
    }

    [TestMethod]
    public void SessionHistoryWrapsLongTextAndKeepsButtonsVisibleAtMinimumSize()
    {
        RunOnStaThread(() =>
        {
            var history = new SessionHistory();
            history.Add("That seems." + Environment.NewLine +
                "Why did it take longer on Kuda than CBU and CUDA? That seems backwards and should wrap instead of sliding behind a horizontal scrollbar.");

            using var form = new SessionHistoryForm(history)
            {
                Size = new Size(520, 420)
            };

            form.CreateControl();
            form.PerformLayout();

            var historyText = FindControl<TextBox>(form, textBox => textBox.Multiline);
            var closeButton = FindControl<Button>(form, button => button.Text == "Close");
            var quitButton = FindControl<Button>(form, button => button.Text == "Quit App");

            Assert.IsTrue(historyText.WordWrap);
            Assert.AreEqual(ScrollBars.Vertical, historyText.ScrollBars);
            Assert.IsFalse(historyText.TabStop);
            Assert.AreEqual(DarkTheme.SurfaceRaised, historyText.BackColor);
            StringAssert.Contains(historyText.Text, "Why did it take longer on Kuda than CBU and CUDA?");
            Assert.AreEqual(closeButton.Size, quitButton.Size);
            Assert.AreEqual(ContentAlignment.MiddleCenter, closeButton.TextAlign);
            Assert.AreEqual(ContentAlignment.MiddleCenter, quitButton.TextAlign);
            Assert.IsInstanceOfType<DarkButton>(closeButton);
            Assert.IsInstanceOfType<DarkButton>(quitButton);
            Assert.AreEqual(Padding.Empty, closeButton.Padding);
            Assert.AreEqual(Padding.Empty, quitButton.Padding);
            Assert.AreEqual(DarkTheme.Accent, closeButton.BackColor);
            Assert.AreEqual(DarkTheme.Danger, quitButton.ForeColor);
            Assert.AreEqual(DarkTheme.Danger, quitButton.FlatAppearance.BorderColor);
            AssertControlInsideClient(form, closeButton);
            AssertControlInsideClient(form, quitButton);

            var previewPath = Environment.GetEnvironmentVariable("PARAKEET_HISTORY_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                form.Show();
                Application.DoEvents();
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                preview.Save(previewPath);
                form.Hide();
            }
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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

    private static void WriteInt16(byte[] buffer, int offset, short sample)
    {
        var bytes = BitConverter.GetBytes(sample);
        buffer[offset] = bytes[0];
        buffer[offset + 1] = bytes[1];
    }

    private static PersistentParakeetServerTranscriber.ServerTranscriptSegment Segment(
        string text,
        TimeSpan? endOfUtterance)
    {
        return new PersistentParakeetServerTranscriber.ServerTranscriptSegment(
            new TranscriptResult(
                text,
                null,
                null,
                [new TranscriptWord(text, TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.3), 0.9)]),
            endOfUtterance);
    }

    private static byte[] CreatePcmWave(TimeSpan duration)
    {
        var pcm = new byte[checked((int)(duration.TotalSeconds * 32000))];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    private static void SaveOverlayPreviewIfRequested(StatusOverlayForm overlay, string environmentVariable)
    {
        var previewPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        var wasVisible = overlay.Visible;
        if (!wasVisible)
        {
            overlay.Show();
            Application.DoEvents();
        }

        using var preview = new Bitmap(overlay.Width, overlay.Height);
        overlay.DrawToBitmap(preview, new Rectangle(Point.Empty, overlay.Size));
        preview.Save(previewPath);

        if (!wasVisible)
        {
            overlay.Hide();
        }
    }

    private static T FindControl<T>(Control root, Predicate<T> match)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed && match(typed))
            {
                return typed;
            }

            var nested = FindControlOrDefault(child, match);
            if (nested is not null)
            {
                return nested;
            }
        }

        Assert.Fail($"Expected to find {typeof(T).Name}.");
        throw new InvalidOperationException($"Expected to find {typeof(T).Name}.");
    }

    private static T? FindControlOrDefault<T>(Control root, Predicate<T> match)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed && match(typed))
            {
                return typed;
            }

            var nested = FindControlOrDefault(child, match);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void AssertControlInsideClient(Form form, Control control)
    {
        var topLeft = Point.Empty;
        for (Control? current = control; current is not null && current != form; current = current.Parent)
        {
            topLeft.Offset(current.Location);
        }

        var bounds = new Rectangle(topLeft, control.Size);

        Assert.IsTrue(bounds.Left >= 0, $"{control.Text} left edge is clipped.");
        Assert.IsTrue(bounds.Top >= 0, $"{control.Text} top edge is clipped.");
        Assert.IsTrue(bounds.Right <= form.ClientSize.Width, $"{control.Text} right edge is clipped.");
        Assert.IsTrue(bounds.Bottom <= form.ClientSize.Height, $"{control.Text} bottom edge is clipped.");
    }

    private static void AssertButtonTextFits(Button button)
    {
        var textSize = TextRenderer.MeasureText(button.Text, button.Font);
        var requiredWidth = textSize.Width + button.Padding.Horizontal + 8;
        Assert.IsTrue(
            button.ClientSize.Width >= requiredWidth,
            $"{button.Text} requires {requiredWidth}px but has {button.ClientSize.Width}px.");
    }

    private sealed class BlockingRestoreClipboardBackend(
        ManualResetEventSlim restoreStarted,
        ManualResetEventSlim allowRestoreToFinish,
        ManualResetEventSlim restoreFinished) : IClipboardPasteBackend
    {
        private readonly IDataObject _previous = new DataObject("previous clipboard contents");

        public string? PastedText { get; private set; }

        public ApartmentState RestoreApartmentState { get; private set; } = ApartmentState.Unknown;

        public IDataObject? GetDataObject() => _previous;

        public uint SetText(string text)
        {
            PastedText = text;
            return 1;
        }

        public bool IsCurrent(uint expectedSequence, string pastedText) => true;

        public bool IsSequenceCurrent(uint expectedSequence) => true;

        public void SendPaste()
        {
        }

        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
        {
            RestoreApartmentState = Thread.CurrentThread.GetApartmentState();
            restoreStarted.Set();
            allowRestoreToFinish.Wait();
            restoreFinished.Set();
        }
    }

    private sealed class BlockingPasteClipboardBackend(
        ManualResetEventSlim pasteStarted,
        ManualResetEventSlim allowPasteToFinish) : IClipboardPasteBackend
    {
        public IDataObject? GetDataObject() => null;

        public uint SetText(string text)
        {
            return 1;
        }

        public bool IsCurrent(uint expectedSequence, string pastedText) => true;

        public bool IsSequenceCurrent(uint expectedSequence) => true;

        public void SendPaste()
        {
            pasteStarted.Set();
            allowPasteToFinish.Wait();
        }

        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
        {
        }
    }

    private sealed class RecordingClipboardBackend(Action? textSet = null) : IClipboardPasteBackend
    {
        public int SetTextCount { get; private set; }

        public int SendPasteCount { get; private set; }

        public int RestoreCount { get; private set; }

        public IDataObject? GetDataObject() => null;

        public uint SetText(string text)
        {
            SetTextCount++;
            textSet?.Invoke();
            return 1;
        }

        public bool IsCurrent(uint expectedSequence, string pastedText) => true;

        public bool IsSequenceCurrent(uint expectedSequence) => true;

        public void SendPaste() => SendPasteCount++;

        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
        {
            RestoreCount++;
        }
    }

    private sealed class RecordingRestoreQueue : IClipboardRestoreQueue
    {
        public int ImmediateEnqueueCount { get; private set; }

        public void Enqueue(Action restore)
        {
            restore();
        }

        public void EnqueueImmediate(Action restore)
        {
            ImmediateEnqueueCount++;
            restore();
        }
    }

    private sealed class StatefulClipboardBackend(ManualResetEventSlim restored) : IClipboardPasteBackend
    {
        private readonly object _sync = new();
        private uint _sequence = 1;

        public IDataObject OriginalData { get; } = new DataObject("original clipboard contents");

        public IDataObject? CurrentData { get; private set; }

        public IDataObject? GetDataObject()
        {
            lock (_sync)
            {
                CurrentData ??= OriginalData;
                return CurrentData;
            }
        }

        public uint SetText(string text)
        {
            lock (_sync)
            {
                CurrentData = new DataObject(text);
                return ++_sequence;
            }
        }

        public bool IsSequenceCurrent(uint expectedSequence)
        {
            lock (_sync)
            {
                return _sequence == expectedSequence;
            }
        }

        public bool IsCurrent(uint expectedSequence, string pastedText)
        {
            return IsSequenceCurrent(expectedSequence);
        }

        public void SendPaste()
        {
        }

        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous)
        {
            lock (_sync)
            {
                if (_sequence != expectedSequence)
                {
                    return;
                }

                CurrentData = previous;
                _sequence++;
            }

            restored.Set();
        }
    }

    private sealed class FakeWindowsClipboardApi : IWindowsClipboardApi
    {
        public uint SequenceNumber { get; set; }

        public string Text { get; set; } = string.Empty;

        public int ClearCount { get; private set; }

        public int SetDataObjectCount { get; private set; }

        public IDataObject? GetDataObject() => null;

        public void SetText(string text)
        {
            Text = text;
            SequenceNumber++;
        }

        public bool ContainsText() => true;

        public string GetText() => Text;

        public void SetDataObject(IDataObject data) => SetDataObjectCount++;

        public void Clear() => ClearCount++;

        public uint GetSequenceNumber() => SequenceNumber;
    }

    private sealed class MutableTextWindowsClipboardApi : IWindowsClipboardApi
    {
        private readonly DataObject _liveClipboard = new();
        private uint _sequence = 1;

        public MutableTextWindowsClipboardApi(string text)
        {
            _liveClipboard.SetData(DataFormats.UnicodeText, autoConvert: false, text);
        }

        public IDataObject? GetDataObject() => _liveClipboard;

        public void SetText(string text)
        {
            _liveClipboard.SetData(DataFormats.UnicodeText, autoConvert: false, text);
            _sequence++;
        }

        public bool ContainsText() => true;

        public string GetText() =>
            (string)((IDataObject)_liveClipboard).GetData(DataFormats.UnicodeText, autoConvert: false)!;

        public void SetDataObject(IDataObject data)
        {
        }

        public void Clear()
        {
        }

        public uint GetSequenceNumber() => _sequence;
    }

    private sealed class FakeForegroundWindowBackend : IForegroundWindowBackend
    {
        private static readonly IntPtr OriginalWindow = (IntPtr)42;

        public IntPtr CurrentWindow { get; set; } = OriginalWindow;

        public bool SetForegroundWindowResult { get; set; } = true;

        public IntPtr GetForegroundWindow() => CurrentWindow;

        public bool IsWindow(IntPtr window) => window == OriginalWindow;

        public bool SetForegroundWindow(IntPtr window)
        {
            if (SetForegroundWindowResult)
            {
                CurrentWindow = window;
            }

            return SetForegroundWindowResult;
        }
    }
}
