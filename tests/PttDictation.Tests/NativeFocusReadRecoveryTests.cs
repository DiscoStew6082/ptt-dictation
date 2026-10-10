using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class NativeFocusReadRecoveryTests
{
    [TestMethod]
    [DataRow(false, 0L, 19368L)]
    [DataRow(true, 0L, 19368L)]
    [DataRow(false, 12000L, 19400L)]
    [DataRow(true, 12000L, 19400L)]
    public async Task ExactSelectionBeforeSlowFocusReadResumesWithOneRevision(
        bool pumpWhileBlocked, long workBeforeFocus, long operationDuration)
    {
        await using var run = await Run.Start(workBeforeFocus);
        run.Now = 100 + 15050;
        if (pumpWhileBlocked) run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        run.Now = 100 + operationDuration;
        await run.Release();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual(2, run.Writes, "Exactly one new paste must follow the acknowledged preview.");
        Assert.AreEqual(1, run.Surface.SelectCalls, "Recovery must not replay selection.");
        Assert.AreEqual(1, run.Surface.Requeries);
        run.Output.Pump();
        await run.Drain();
        Assert.AreEqual(2, run.Acknowledgements);
        Assert.AreEqual("revised", run.Surface.Snapshot.Document);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("input")]
    [DataRow("focus")]
    [DataRow("snapshot")]
    public async Task FocusRecoveryCannotOverrideChangedIntent(string change)
    {
        await using var run = await Run.Start();
        run.Now += 15050;
        run.Output.Pump();
        if (change == "cancel") run.Output.EndSession();
        else if (change == "input") run.InputGeneration++;
        else if (change == "focus") run.Surface.Focused = false;
        else run.Surface.Snapshot = new("user edit", "user edit", "", "");
        run.Now = 100 + 19368;
        await run.Release();
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(1, run.Acknowledgements);
    }

    [TestMethod]
    public async Task InputBeforeSelectionDuringInitialFocusReadCannotBecomeNewIntent()
    {
        await using var run = await Run.Start(blockBeforeSelection: true);
        run.InputGeneration++;
        run.Now += 19368;
        await run.Release();
        run.Output.Pump();
        await run.Drain();
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(0, run.Surface.SelectCalls, "Intervening input must be rejected before selecting owned text.");
        Assert.AreEqual(1, run.Failures);
    }

    [TestMethod]
    public async Task OrdinaryWindowSwitchStillPausesAndResumesAtOriginalField()
    {
        await using var run = await Run.Start(blockBeforeSelection: true);
        run.InputGeneration++;
        run.Surface.Focused = false;
        run.Now += 1000;
        await run.Release();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(0, run.Surface.SelectCalls);
        run.Surface.Focused = true;
        run.Output.Pump();
        await run.Drain();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual(2, run.Writes);
        run.Output.Pump();
        await run.Drain();
        Assert.AreEqual(2, run.Acknowledgements);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ThirtySecondCeilingRejectsLateFocusReturnEvenWithoutTimer(bool pumpWhileBlocked)
    {
        await using var run = await Run.Start();
        run.Now += 15050;
        if (pumpWhileBlocked) run.Output.Pump();
        run.Now = 100 + 30000;
        if (pumpWhileBlocked) { run.Output.Pump(); run.Output.Pump(); }
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(1, run.Acknowledgements);
    }

    [TestMethod]
    public async Task InputDuringFinalSnapshotRevokesRecoveredPaste()
    {
        await using var run = await Run.Start();
        run.BeforeCanWrite = () => run.Surface.AfterRead = () => run.InputGeneration++;
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(1, run.Failures);
    }

    [TestMethod]
    public async Task FocusRecoveryCannotCarryIntoLaterAcknowledgementRead()
    {
        await using var run = await Run.Start();
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(2, run.Writes);
        run.Surface.Entered.Reset();
        run.Surface.Continue.Reset();
        run.Surface.BlockRead = true;
        run.Output.Pump();
        Assert.IsTrue(run.Surface.Entered.Wait(TimeSpan.FromSeconds(2)));
        run.Now += 15000;
        run.Output.Pump();
        Assert.AreEqual(1, run.Failures);
        await run.Release();
        Assert.AreEqual(1, run.Acknowledgements);
        Assert.AreEqual(2, run.Writes);
    }

    [TestMethod]
    public async Task SubmittedPasteObservationCannotExtendItsDeadlineWithARefreshFocusRead()
    {
        await using var run = await Run.Start();
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(2, run.Writes);
        run.Surface.Entered.Reset();
        run.Surface.Continue.Reset();
        run.Surface.RefreshFocusOnRead = true;
        run.Surface.BlockFocus = true;
        run.Output.Pump();
        Assert.IsTrue(run.Surface.Entered.Wait(TimeSpan.FromSeconds(2)));
        run.Now += 15000;
        run.Output.Pump();
        Assert.AreEqual(1, run.Failures);
        await run.Release();
        Assert.AreEqual(1, run.Acknowledgements);
        Assert.AreEqual(2, run.Writes);
    }

    [TestMethod]
    public async Task ShortFocusReadCannotExtendLongFinalSnapshot()
    {
        await using var run = await Run.Start();
        run.Now += 1000;
        run.BeforeCanWrite = () => run.Surface.AfterRead = () => run.Now += 19000;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GenericWorkExhaustingBudgetBeforeFocusReadCannotGrantRecovery(bool pumpWhileBlocked)
    {
        await using var run = await Run.Start(workBeforeSelection: 15000);
        if (pumpWhileBlocked) run.Output.Pump();
        run.Now++;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    [DataRow("unchanged")]
    [DataRow("document")]
    [DataRow("caret")]
    public async Task FinalFocusStallRequiresFreshSnapshotDespiteUnchangedNativeInput(string mutation)
    {
        await using var run = await Run.Start(blockDuringFinalValidation: true);
        var input = run.InputGeneration;
        if (mutation == "document") run.Surface.Snapshot = new("scripted change", "scripted change", "", "");
        else if (mutation == "caret")
        {
            var document = run.Surface.Snapshot.Document;
            run.Surface.Snapshot = new(document, document, "", "");
        }
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(input, run.InputGeneration);
        Assert.AreEqual(mutation == "unchanged" ? 2 : 1, run.Writes);
        Assert.AreEqual(mutation == "unchanged" ? 0 : 1, run.Failures);
        Assert.AreEqual(1, run.Surface.SelectCalls);
    }

    [TestMethod]
    public async Task NativeFocusChangeDuringRecoveredFinalReadRevokesPaste()
    {
        await using var run = await Run.Start(blockDuringFinalValidation: true);
        run.Surface.AfterRead = () => run.FocusGeneration++;
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    public async Task MissingNativeFocusTelemetryCannotAuthorizeRecoveredFinalSnapshot()
    {
        await using var run = await Run.Start(blockDuringFinalValidation: true);
        run.ThrowFocusTelemetry = true;
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    public async Task NativeForegroundChangeDuringFinalReadRevokesPasteBeforeEventDelivery()
    {
        await using var run = await Run.Start(blockDuringFinalValidation: true);
        run.Surface.AfterRead = () => run.Surface.ForegroundCurrent = false;
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    public async Task RecoveredValidationReadsOnlyOnceAndCannotResetHardCeiling()
    {
        await using var run = await Run.Start(blockDuringFinalValidation: true);
        var reads = run.Surface.Reads;
        run.Surface.AfterRead = () => run.Now += 15000;
        run.Now += 19368;
        await run.Release();
        Assert.AreEqual(reads + 1, run.Surface.Reads, "A slow final snapshot must not start another validation loop.");
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly ApartmentWorker _worker = new(ApartmentState.MTA, "Native focus recovery test");
        public readonly Surface Surface;
        public readonly LiveClipboardPaster Output;
        public long Now = 100, InputGeneration = 1, FocusGeneration = 1;
        public int Failures, Writes, Acknowledgements;
        public bool ThrowFocusTelemetry;
        public Action? BeforeCanWrite;

        private Run()
        {
            Surface = new(() => Interlocked.Read(ref Now));
            var target = new WindowsTextTarget(Surface,
                clock: () => DateTimeOffset.UnixEpoch.AddMilliseconds(Interlocked.Read(ref Now)),
                readFocusGeneration: () => ThrowFocusTelemetry
                    ? throw new InvalidOperationException("Native focus telemetry unavailable.")
                    : Interlocked.Read(ref FocusGeneration),
                readInputGeneration: () => Interlocked.Read(ref InputGeneration),
                operationTickCount: () => Interlocked.Read(ref Now));
            Output = new(() => target, (_, _) => Assert.Fail("Expected acknowledged paste."),
                automationWorker: _worker, tickCount: () => Interlocked.Read(ref Now),
                pasteAwaitingAcknowledgement: (text, allowed) =>
                {
                    BeforeCanWrite?.Invoke();
                    if (!allowed()) throw new InvalidOperationException("Original input intent was revoked.");
                    Writes++;
                    Surface.Snapshot = new(text, text, "", "");
                    return new Lease(this);
                });
            Output.InsertionFailed += _ => Failures++;
        }

        public static async Task<Run> Start(long workBeforeFocus = 0, bool blockBeforeSelection = false,
            long workBeforeSelection = 0, bool blockDuringFinalValidation = false)
        {
            var run = new Run();
            run.Output.CaptureTarget();
            await run.Drain();
            run.Output.UpdatePreview("first");
            run.Output.Pump();
            await run.Drain();
            run.Output.Pump();
            await run.Drain();
            Assert.AreEqual(1, run.Writes);
            Assert.AreEqual(1, run.Acknowledgements);
            run.Surface.BlockFocus = blockBeforeSelection;
            run.Surface.AfterRead = () =>
            {
                run.Now += workBeforeSelection;
                run.Surface.AfterRead = null;
            };
            run.Surface.AfterRequery = () =>
            {
                run.Now += workBeforeFocus;
                run.Surface.BlockFocus = !blockDuringFinalValidation;
            };
            if (blockDuringFinalValidation)
                run.BeforeCanWrite = () => run.Surface.AfterRead = () =>
                {
                    run.Surface.AfterRead = null;
                    run.Surface.BlockFocus = true;
                };
            run.Output.UpdatePreview("revised");
            run.Output.Pump();
            Assert.IsTrue(run.Surface.Entered.Wait(TimeSpan.FromSeconds(2)));
            return run;
        }

        public Task Drain() => _worker.InvokeAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2));
        public async Task Release() { Surface.Continue.Set(); await Drain(); }
        public async ValueTask DisposeAsync()
        {
            await Release();
            Output.Dispose();
            Surface.Entered.Dispose();
            Surface.Continue.Dispose();
        }
        private sealed class Lease(Run run) : IPendingClipboardPaste
        {
            public void Acknowledge() => run.Acknowledgements++;
            public void Dispose() { }
        }
    }

    private sealed class Surface(Func<long> tickCount) : IWindowsTextSurface
    {
        private readonly NativeFocusReadTracker _focusReads = new(tickCount);
        public readonly ManualResetEventSlim Entered = new(), Continue = new();
        public volatile bool Focused = true;
        public bool ForegroundCurrent = true;
        public bool IsOriginalForegroundCurrent => ForegroundCurrent;
        public bool BlockFocus, BlockRead, RefreshFocusOnRead;
        public int SelectCalls, Requeries, Reads;
        public Action? AfterRequery, AfterRead;
        public TextTargetSnapshot Snapshot = new("", "", "", "");
        private TextTargetSnapshot? _requested;
        public NativeFocusRead? FocusReadProgress => _focusReads.Progress;
        public void BeginReadyOperation(long startedAt) => _focusReads.BeginOperation(startedAt);
        public bool IsFocused => _focusReads.Read(() =>
        {
            if (BlockFocus)
            {
                BlockFocus = false;
                Entered.Set();
                Assert.IsTrue(Continue.Wait(TimeSpan.FromSeconds(10)));
            }
            return Focused;
        });
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read()
        {
            Reads++;
            if (RefreshFocusOnRead) _ = IsFocused;
            if (BlockRead)
            {
                Entered.Set();
                Assert.IsTrue(Continue.Wait(TimeSpan.FromSeconds(10)));
            }
            var snapshot = Snapshot;
            AfterRead?.Invoke();
            return snapshot;
        }
        public bool Select(string prefix, string ownedText, string suffix)
        {
            SelectCalls++;
            _requested = new(prefix + ownedText + suffix, prefix, ownedText, suffix);
            return true;
        }
        public TextPatternRequeryResult RequerySelectionPattern()
        {
            Requeries++;
            Snapshot = _requested!;
            AfterRequery?.Invoke();
            return TextPatternRequeryResult.Refreshed;
        }
    }
}
