using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class NativeSelectionRecoveryTests
{
    [TestMethod]
    [DataRow(false, 0L, 25828L)]
    [DataRow(true, 0L, 25828L)]
    [DataRow(false, 12000L, 26000L)]
    [DataRow(true, 12000L, 26000L)]
    public async Task SameSelectionReturningAfterObservedFirefoxStallResumesWithOnePaste(
        bool pumpWhileBlocked, long workBeforeSelect, long operationDuration)
    {
        await using var run = await Run.Start(workBeforeSelect);
        run.Now = 100 + 15050;
        if (pumpWhileBlocked) run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        run.Now = 100 + operationDuration;
        await run.Release();
        Assert.AreEqual(0, run.Failures);
        Assert.AreEqual(1, run.Writes);
        Assert.AreEqual(1, run.Surface.SelectCalls);
        run.Output.Pump();
        await run.Drain();
        Assert.AreEqual(1, run.Acknowledgements);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("input")]
    [DataRow("focus")]
    public async Task SelectionRecoveryNeverOverridesUserIntent(string change)
    {
        await using var run = await Run.Start();
        run.Now += 15050;
        run.Output.Pump();
        Assert.AreEqual(0, run.Failures);
        if (change == "cancel") run.Output.EndSession();
        else if (change == "input") run.InputGeneration++;
        else run.Surface.Focused = false;
        run.Now = 100 + 25828;
        await run.Release();
        Assert.AreEqual(0, run.Writes);
        Assert.AreEqual(0, run.Acknowledgements);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ThirtySecondWallCeilingCannotResetOrPermitLateInput(bool pumpWhileBlocked)
    {
        await using var run = await Run.Start();
        run.Now += 15050;
        if (pumpWhileBlocked) run.Output.Pump();
        run.Now = 100 + 30000;
        if (pumpWhileBlocked) { run.Output.Pump(); run.Output.Pump(); }
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(0, run.Writes);
        Assert.AreEqual(0, run.Acknowledgements);
    }

    [TestMethod]
    public async Task InputDuringFinalPreparedSnapshotAlsoRevokesRecoveredPaste()
    {
        await using var run = await Run.Start();
        run.BeforeCanWrite = () => run.Surface.AfterRead = () => run.InputGeneration++;
        run.Now = 100 + 25828;
        await run.Release();
        Assert.AreEqual(0, run.Writes);
        Assert.AreEqual(1, run.Failures);
    }

    [TestMethod]
    public async Task PreviousSelectionGraceCannotExtendSubsequentAcknowledgementRead()
    {
        await using var run = await Run.Start();
        run.Now = 100 + 25828;
        await run.Release();
        Assert.AreEqual(1, run.Writes);
        run.Surface.Entered.Reset();
        run.Surface.Continue.Reset();
        run.Surface.BlockRead = true;
        run.Output.Pump();
        Assert.IsTrue(run.Surface.Entered.Wait(TimeSpan.FromSeconds(2)));
        run.Now += 15000;
        run.Output.Pump();
        Assert.AreEqual(1, run.Failures);
        await run.Release();
        Assert.AreEqual(0, run.Acknowledgements);
        Assert.AreEqual(1, run.Writes);
    }

    [TestMethod]
    public async Task ShortSelectionFollowedByLongSnapshotCannotGrantRecovery()
    {
        await using var run = await Run.Start();
        run.Now = 100 + 1000;
        run.Surface.AfterRead = () => { run.Now += 25000; run.Surface.AfterRead = null; };
        await run.Release();
        Assert.AreEqual(1, run.Failures);
        Assert.AreEqual(0, run.Writes);
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly ApartmentWorker _worker = new(ApartmentState.MTA, "Native selection recovery test");
        public readonly Surface Surface = new();
        public readonly LiveClipboardPaster Output;
        public long Now = 100, InputGeneration = 1;
        public int Failures, Writes, Acknowledgements;
        public Action? BeforeCanWrite;

        private Run()
        {
            var target = new WindowsTextTarget(Surface,
                clock: () => DateTimeOffset.UnixEpoch.AddMilliseconds(Interlocked.Read(ref Now)),
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

        public static async Task<Run> Start(long workBeforeSelect = 0)
        {
            var run = new Run();
            run.Output.CaptureTarget();
            await run.Drain();
            run.Surface.AfterRead = () => { run.Now += workBeforeSelect; run.Surface.AfterRead = null; };
            run.Output.UpdatePreview("words");
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

    private sealed class Surface : IWindowsTextSurface
    {
        public readonly ManualResetEventSlim Entered = new(), Continue = new();
        public volatile bool Focused = true;
        public int SelectCalls;
        public bool BlockRead;
        public Action? AfterRead;
        public TextTargetSnapshot Snapshot = new("old", "", "old", "");
        public bool IsFocused => Focused;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read()
        {
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
            Entered.Set();
            Assert.IsTrue(Continue.Wait(TimeSpan.FromSeconds(10)));
            Snapshot = new(prefix + ownedText + suffix, prefix, ownedText, suffix);
            return true;
        }
    }
}
