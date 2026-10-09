using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveClipboardWaitTests
{
    [TestMethod]
    public async Task ReadyOperationShowsOneWaitAndResumesWithoutDuplicateInput()
    {
        await using var run = await BlockedRun.Start();
        run.Now += 5100;
        run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual("Waiting for textbox to respond.", run.Output.HoldingReason);
        run.Now += 1000;
        run.Output.Pump();
        Assert.AreEqual(1, run.WaitPresentations);
        Assert.AreEqual(1, run.Target.Calls);
        await run.Release();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(1, run.Acknowledgements);
        Assert.IsNull(run.Output.HoldingReason);
        Assert.IsTrue(run.Output.InlinePreview);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("focus")]
    [DataRow("selection")]
    public async Task WaitingDoesNotPermitInputAfterCancellationOrTargetChange(string change)
    {
        await using var run = await BlockedRun.Start();
        run.Now += 5100;
        run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        if (change == "cancel") run.Output.EndSession();
        else if (change == "focus") run.Target.Focused = false;
        else run.Target.SelectionCurrent = false;
        await run.Release();
        Assert.AreEqual(0, run.Writes);
        Assert.AreEqual(0, run.Acknowledgements);
        Assert.AreEqual(change == "cancel" ? 0 : 1, run.Failures);
        Assert.AreNotEqual("Waiting for textbox to respond.", run.Output.HoldingReason);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeReturnAfterHardDeadlineCannotWriteOrAcknowledgeWithoutTimerPump(bool pasteBeforeBlock)
    {
        await using var run = await BlockedRun.Start(pasteBeforeBlock);
        run.Now = 100 + LiveClipboardPaster.ReadyOperationTimeoutMilliseconds;
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(pasteBeforeBlock ? 1 : 0, run.Writes);
        Assert.AreEqual(0, run.Acknowledgements);
        Assert.AreEqual(pasteBeforeBlock ? 1 : 0, run.Abandonments);
        run.Output.Pump();
        Assert.AreEqual(1, run.Failures);
    }

    [TestMethod]
    public async Task HardDeadlineFailsOnceAndLateCompletionCannotAcknowledge()
    {
        await using var run = await BlockedRun.Start(pasteBeforeBlock: true);
        run.Now += 5100;
        run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        run.Now = 100 + 15000;
        run.Output.Pump();
        run.Output.Pump();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Abandonments);
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(0, run.Acknowledgements);
        Assert.AreEqual(1, run.Abandonments);
        Assert.AreNotEqual("Waiting for textbox to respond.", run.Output.HoldingReason);
    }

    private sealed class BlockedRun : IAsyncDisposable
    {
        private readonly ApartmentWorker _worker = new(ApartmentState.MTA, "Blocked ready target test");
        public readonly BlockedTarget Target;
        public readonly LiveClipboardPaster Output;
        public long Now = 100;
        public int Failures, Writes, Acknowledgements, Abandonments, WaitPresentations;

        private BlockedRun(bool pasteBeforeBlock)
        {
            Target = new(pasteBeforeBlock);
            Output = new LiveClipboardPaster(() => Target, (_, _) => Assert.Fail("Expected acknowledged paste path."),
                automationWorker: _worker, tickCount: () => Interlocked.Read(ref Now),
                pasteAwaitingAcknowledgement: (_, canWrite) =>
                {
                    if (!canWrite()) throw new InvalidOperationException("Original target permission was revoked.");
                    Interlocked.Increment(ref Writes);
                    return new Lease(this);
                });
            Output.InsertionFailed += _ => Interlocked.Increment(ref Failures);
            Output.PresentationChanged += () =>
            {
                if (Output.HoldingReason == "Waiting for textbox to respond.") Interlocked.Increment(ref WaitPresentations);
            };
        }

        public static async Task<BlockedRun> Start(bool pasteBeforeBlock = false)
        {
            var run = new BlockedRun(pasteBeforeBlock);
            run.Output.CaptureTarget();
            await run._worker.InvokeAsync(() => { });
            run.Output.UpdatePreview("words");
            run.Output.Pump();
            Assert.IsTrue(run.Target.Entered.Wait(TimeSpan.FromSeconds(2)));
            return run;
        }

        public async Task Release()
        {
            Target.Continue.Set();
            await _worker.InvokeAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2));
        }

        public async ValueTask DisposeAsync()
        {
            await Release();
            Output.Dispose();
            Target.Entered.Dispose();
            Target.Continue.Dispose();
        }

        private sealed class Lease(BlockedRun run) : IPendingClipboardPaste
        {
            public void Acknowledge() => Interlocked.Increment(ref run.Acknowledgements);
            public void Dispose() => Interlocked.Increment(ref run.Abandonments);
        }
    }

    private sealed class BlockedTarget(bool pasteBeforeBlock) : IWindowsTextTarget
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Continue = new();
        public volatile bool Focused = true;
        public volatile bool SelectionCurrent = true;
        public int Calls;
        public bool IsFocused => Focused;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public bool IsPreparedSelectionCurrent => Focused && SelectionCurrent;
        public bool HasAttemptedWrite { get; private set; }
        public TextTargetUpdateResult TryReplace(string previous, string replacement, Action<string> paste)
        {
            Interlocked.Increment(ref Calls);
            if (pasteBeforeBlock) { HasAttemptedWrite = true; paste(replacement); }
            Entered.Set();
            Assert.IsTrue(Continue.Wait(TimeSpan.FromSeconds(10)));
            if (!pasteBeforeBlock) { HasAttemptedWrite = true; paste(replacement); }
            return TextTargetUpdateResult.Success;
        }
    }
}
