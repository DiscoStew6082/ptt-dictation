using System.Runtime.InteropServices;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

// Agent-runnable, self-owned native editor coverage. Hotkey events, recognition,
// and paste transport are synthetic; capture and selection use production UIA.
// This does not accept the physical keyboard hook, microphone, or any browser.
[TestClass]
[DoNotParallelize]
public sealed class NativeDictationPipelineTests
{
    [TestMethod]
    [VisibleUi]
    public async Task SyntheticHoldRevisesNativeEditorPreviewAndFinalTwice()
    {
        VisibleUiTestGate.RequireOptIn();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        Form? ownedForm = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOwnedEditor(shutdown.Token, form => Volatile.Write(ref ownedForm, form));
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true, Name = "Synthetic dictation native editor fixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(75)); }
        catch (TimeoutException)
        {
            shutdown.Cancel();
            var form = Volatile.Read(ref ownedForm);
            try
            {
                // Never block the runner on a stuck native provider. Close only
                // our owned window when its message loop can process the request.
                if (form is { IsDisposed: false, IsHandleCreated: true })
                    form.BeginInvoke(new Action(form.Close));
            }
            catch (InvalidOperationException) { } // The fixture closed concurrently.
            _ = completed.Task.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }
    }

    private static void RunOwnedEditor(CancellationToken shutdown, Action<Form> registerForm)
    {
        shutdown.ThrowIfCancellationRequested();
        Exception? failure = null;
        var successful = false;
        using var form = new Form
        {
            Text = "PTT synthetic native editor test (no microphone)",
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            ClientSize = new Size(600, 170),
            StartPosition = FormStartPosition.CenterScreen
        };
        using var editor = new TextBox
        {
            Name = "SyntheticDictationOwnedEditor",
            AccessibleName = "Synthetic dictation test editor",
            Multiline = true,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(32, 32, 32),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 12)
        };
        form.Controls.Add(editor);
        registerForm(form);
        async Task Exercise()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            LiveClipboardPaster? paster = null;
            DictationWorkflow? workflow = null;
            try
            {
                // Let the first Show finish before requesting normal activation.
                await Task.Delay(100, deadline.Token);
                form.Activate();
                editor.Focus();
                // A background test host may lack Windows foreground permission.
                // Give supported desktop automation time to activate this exact
                // owned fixture; never force input into another foreground app.
                var activationStarted = Environment.TickCount64;
                while ((GetForegroundWindow() != form.Handle || !editor.Focused)
                    && Environment.TickCount64 - activationStarted < 30000)
                    await Task.Delay(30, deadline.Token);
                await Task.Delay(250, deadline.Token);
                void RequireOwnedFocus()
                {
                    if (form.IsDisposed || GetForegroundWindow() != form.Handle || !editor.Focused)
                        Assert.Inconclusive($"The synthetic fixture does not own foreground focus; no external editor is written. "
                            + $"OwnedForeground={GetForegroundWindow() == form.Handle}, EditorFocused={editor.Focused}, "
                            + $"Visible={form.Visible}, UserInteractive={Environment.UserInteractive}.");
                }
                RequireOwnedFocus();
                paster = new LiveClipboardPaster(WindowsTextTarget.Capture, (text, canPaste) =>
                {
                    // Deliberately avoid Clipboard/SendInput: production UIA has
                    // already selected a range, and this transport may write only
                    // to our exact owned control after all production checks.
                    if (!canPaste()) throw new InvalidOperationException("Production selection validation rejected the fixture write.");
                    editor.Invoke(() =>
                    {
                        RequireOwnedFocus();
                        editor.SelectedText = text;
                    });
                }, startTimer: true, canPaste: () => GetForegroundWindow() == form.Handle);
                var sessions = new SyntheticSessions();
                var history = new SessionHistory();
                workflow = new DictationWorkflow(sessions, paster, history,
                    synchronizationContext: SynchronizationContext.Current);
                using var hotkey = new GlobalHotkeySource(DictationHotkey.F8, DictationHotkey.F9);
                // Never Start(): events enter the production parsing seam only,
                // so a separately running PTT app receives no test keystrokes.
                Task dispatched = Task.CompletedTask;
                hotkey.Pressed += () => dispatched = workflow.HandleAsync(DictationIntent.BeginHold, deadline.Token);
                hotkey.Released += () => dispatched = workflow.HandleAsync(DictationIntent.EndHold, deadline.Token);
                var key = GlobalHotkeySource.VirtualKeyForTest(DictationHotkey.F8);
                for (var run = 0; run < 2; run++)
                {
                    const string prefix = "Keep this prefix. ";
                    const string suffix = " Keep this suffix.";
                    editor.Text = prefix + "replace me" + suffix;
                    editor.Select(prefix.Length, "replace me".Length);
                    await Task.Delay(250, deadline.Token); // Drain native fixture focus/selection notifications.
                    RequireOwnedFocus();
                    Assert.IsTrue(hotkey.ProcessKeyEventForTest(key, GlobalHotkeySource.KeyDownMessageForTest));
                    await dispatched;
                    await Until(() => paster.InlinePreview, "native replacement capture");
                    sessions.Current!.Preview("Synthetic early words");
                    await Until(() => editor.Text == prefix + "Synthetic early words" + suffix, "initial preview insertion");
                    sessions.Current.Preview("Synthetic revised words");
                    await Until(() => editor.Text == prefix + "Synthetic revised words" + suffix, "preview replacement");
                    Assert.IsTrue(hotkey.ProcessKeyEventForTest(key, GlobalHotkeySource.KeyUpMessageForTest));
                    await dispatched;
                    RequireOwnedFocus();
                    Assert.AreEqual(DictationWorkflowPhase.Pasted, workflow.CurrentState.Phase,
                        workflow.CurrentState.ErrorMessage);
                    Assert.AreEqual(prefix + "Synthetic final text." + suffix, editor.Text);
                    Assert.AreEqual(run + 1, history.Items.Count);
                    Assert.AreEqual("Synthetic final text.", history.Items[^1]);
                }
                successful = true;

                async Task Until(Func<bool> condition, string stage)
                {
                    var started = Environment.TickCount64;
                    while (!condition())
                    {
                        RequireOwnedFocus();
                        Assert.AreNotEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase,
                            $"{stage}: {workflow.CurrentState.ErrorMessage}");
                        Assert.IsLessThan(8000L, Environment.TickCount64 - started, $"Timed out at {stage}.");
                        await Task.Delay(30, deadline.Token);
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try
                {
                    if (workflow is not null) await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
                }
                catch (Exception error) { failure ??= error; }
                finally { paster?.Dispose(); }
            }
        }
        form.Shown += async (_, _) =>
        {
            try { await Exercise(); }
            catch (Exception error) { failure = error; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsTrue(successful, "The owned fixture closed before the synthetic pipeline completed.");
    }

    private sealed class SyntheticSessions : IDictationSessionFactory
    {
        public SyntheticSession? Current { get; private set; }
        public IDictationSession CreateSession() => Current = new SyntheticSession();
    }

    private sealed class SyntheticSession : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Preview(string text) => TranscriptUpdated?.Invoke(new(TranscriptUpdateKind.Partial, text));
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DictationSessionResult(new TranscriptResult("Synthetic final text.", TimeSpan.Zero, 1)));
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
