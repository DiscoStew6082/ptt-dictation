using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class AppBehaviorTests
{
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
    public void SettingsFormExplainsHoldAndToggleHotkeysWithoutPathEditors()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault());
            form.UseSettings(AppSettings.Default);

            StringAssert.Contains(form.SummaryTextForTest, "Right Ctrl");
            StringAssert.Contains(form.SummaryTextForTest, "Right Shift");
            Assert.IsFalse(form.HasRuntimePathEditorForTest);
            Assert.IsFalse(form.HasModelPathEditorForTest);
        });
    }

    [TestMethod]
    public void SettingsFormUsesResponsiveDarkSectionsAtMinimumSize()
    {
        RunOnStaThread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-form-{Guid.NewGuid():N}.json");
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault())
            {
                Size = new Size(680, 620)
            };
            form.UseSettings(AppSettings.Default);

            form.Show();
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
            using var form = new SettingsForm(new AppSettingsStore(path), ModelRegistry.CreateDefault())
            {
                Size = new Size(680, 620)
            };
            form.UseSettings(AppSettings.Default);
            form.Show();
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
    public void StatusOverlayAutoHidesCompletionStatesWithoutShowingWindow()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Pasted);

            Assert.AreEqual("Pasted", overlay.TitleTextForTest);
            Assert.AreEqual("Transcript pasted into the active app.", overlay.MessageTextForTest);
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
    public void StatusOverlayReservesListeningTextAboveLargeActivityMeter()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();

            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            Assert.AreEqual(560, StatusOverlayForm.ListeningSizeForTest.Width);
            Assert.AreEqual(StatusOverlayForm.ListeningSizeForTest, overlay.Size);
            Assert.AreEqual(194, overlay.ActivityMeterHeightForTest);
            Assert.IsTrue(overlay.TextPanelHeightForTest >= overlay.TitlePreferredHeightForTest + overlay.MessagePreferredHeightForTest + 20);
            Assert.IsTrue(overlay.ActivityMeterTopForTest >= overlay.TextPanelBottomForTest);
        });
    }

    [TestMethod]
    public void StatusOverlayReturnsToCompactSizeAfterListening()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening);

            overlay.ApplyStatusForTest(DictationStatusCatalog.Transcribing);

            Assert.AreEqual(StatusOverlayForm.DefaultSizeForTest, overlay.Size);
        });
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
    public void LiveTranscriptPreviewKeepsNewestWordsVisible()
    {
        const string transcript = "The beginning is no longer useful but these newest words must stay visible";

        var preview = LiveTranscriptPreviewFormatter.LatestWords(transcript, maximumCharacters: 38);

        StringAssert.StartsWith(preview, "…");
        StringAssert.EndsWith(preview, "newest words must stay visible");
        Assert.IsFalse(preview.Contains("beginning", StringComparison.Ordinal));
        Assert.IsTrue(preview.Length <= 38);
    }

    [TestMethod]
    public void StatusOverlayShowsNewestPartialWordsWithoutStoppingRecording()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            overlay.ApplyStatusForTest(DictationStatusCatalog.Listening, ListeningTriggerMode.Toggle);

            overlay.ApplyListeningTranscriptForTest(
                "The beginning is old and should disappear while the final spoken words remain visible",
                ListeningTriggerMode.Toggle);

            StringAssert.Contains(overlay.MessageTextForTest, "final spoken words remain visible");
            Assert.IsFalse(overlay.MessageTextForTest.Contains("The beginning", StringComparison.Ordinal));
            Assert.IsTrue(overlay.LiveActivityTimerEnabledForTest);
        });
    }

    [TestMethod]
    public void FinalTranscriptPreviewShowsTheCompleteCorrectedText()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            var transcript = string.Join(' ', Enumerable.Repeat(
                "The complete corrected transcript stays visible before paste.",
                8));

            overlay.ApplyStatusForTest(DictationStatusCatalog.TranscriptPreview(transcript));

            Assert.AreEqual(
                "Corrected transcript — click to edit · pasting in 3 seconds",
                overlay.TitleTextForTest);
            Assert.AreEqual(transcript, overlay.MessageTextForTest);
            Assert.AreEqual(StatusOverlayForm.FinalPreviewSizeForTest, overlay.Size);
            Assert.IsFalse(overlay.MessageAutoEllipsisForTest);
            Assert.IsTrue(overlay.MessageHeightForTest >= overlay.MessagePreferredHeightForTest);
        });
    }

    [TestMethod]
    public void FinalTranscriptPreviewCanRequestEditing()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new StatusOverlayForm();
            var requestCount = 0;
            overlay.TranscriptEditRequested += () => requestCount++;
            overlay.ApplyStatusForTest(DictationStatusCatalog.TranscriptPreview("Fix contacts to context."));

            overlay.RequestTranscriptEditForTest();

            Assert.AreEqual(1, requestCount);
        });
    }

    [TestMethod]
    public void TranscriptReviewFormShowsTheCompleteEditableTranscript()
    {
        RunOnStaThread(() =>
        {
            var transcript = string.Join(' ', Enumerable.Repeat(
                "Context and contacts remain editable.",
                20));
            using var review = new TranscriptReviewForm(transcript);

            Assert.AreEqual(transcript, review.Transcript);
            Assert.IsTrue(review.EditorForTest.Multiline);
            Assert.IsTrue(review.EditorForTest.WordWrap);
            Assert.AreEqual(ScrollBars.Vertical, review.EditorForTest.ScrollBars);
            Assert.AreEqual("Paste corrected text", review.PasteButtonForTest.Text);
            Assert.AreEqual(DialogResult.OK, review.PasteButtonForTest.DialogResult);
            Assert.AreEqual(DialogResult.Cancel, review.CancelButtonForTest.DialogResult);
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

            overlay.ApplyStatusForTest(DictationStatusCatalog.Transcribing);

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

            overlay.ApplyStatusForTest(DictationStatusCatalog.Transcribing);
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

            overlay.ApplyStatusForTest(DictationStatusCatalog.Transcribing);
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
            StringAssert.Contains(historyText.Text, "Why did it take longer on Kuda than CBU and CUDA?");
            AssertControlInsideClient(form, closeButton);
            AssertControlInsideClient(form, quitButton);
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
}
