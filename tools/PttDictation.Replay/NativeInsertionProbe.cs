using System.Text.Json;
using System.Windows.Automation;
using PttDictation.App;
using PttDictation.Core;

// Diagnostic invocation of production insertion against a controlled empty editor.
// This is insertion-path evidence, not hotkey/app acceptance evidence.
internal static class NativeInsertionProbe
{
    public static int Run(string outputDirectory, string? requiredAutomationId = null, string? savedTrace = null,
        bool injectClipboardReadFailure = false)
    {
        var result = 1;
        var thread = new Thread(() => result = RunOnSta(Path.GetFullPath(outputDirectory), requiredAutomationId, savedTrace, injectClipboardReadFailure));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static int RunOnSta(string output, string? requiredAutomationId, string? savedTrace, bool injectClipboardReadFailure)
    {
        Directory.CreateDirectory(output);
        using var diagnostics = DiagnosticTrace.Configure(output,
            "experimental-native-insertion-probe-" + typeof(LiveClipboardPaster).Assembly.ManifestModule.ModuleVersionId);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.FromMilliseconds(750));
        var faultApi = new SingleReadFailureClipboardApi();
        var clipboard = new ClipboardPaster(new WindowsClipboardPasteBackend(faultApi), restoreQueue,
            new WindowsForegroundWindowBackend());
        using var paster = injectClipboardReadFailure
            ? new LiveClipboardPaster(WindowsTextTarget.Capture, clipboard.PasteToCurrentTarget,
                startTimer: true, canPaste: () => WindowsPasteInput.CanPasteNow)
            : new LiveClipboardPaster();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = 1;
        var started = false;
        timer.Tick += async (_, _) =>
        {
            if (started) return;
            if (!File.Exists(Path.Combine(output, "go")))
            {
                if (deadline.IsCancellationRequested) Application.ExitThread();
                return;
            }
            started = true;
            timer.Stop();
            var recording = DiagnosticTrace.BeginRecording("controlled-native-insertion");
            using var scope = DiagnosticTrace.EnterRecording(recording);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                if (injectClipboardReadFailure)
                {
                    var focused = AutomationElement.FocusedElement;
                    using var process = System.Diagnostics.Process.GetProcessById(focused.Current.ProcessId);
                    if (!string.Equals(process.ProcessName, "Notepad", StringComparison.OrdinalIgnoreCase)
                        || !focused.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
                        || raw is not TextPattern pattern || !string.IsNullOrWhiteSpace(pattern.DocumentRange.GetText(-1)))
                        throw new InvalidOperationException("Recovery probe requires a focused empty Notepad test tab.");
                }
                if (requiredAutomationId is not null
                    && AutomationElement.FocusedElement?.Current.AutomationId != requiredAutomationId)
                    throw new InvalidOperationException("The controlled fixture textbox is not focused; no write attempted.");
                var updates = savedTrace is null
                    ? [new SavedUpdate(0, "Probe early words", false), new SavedUpdate(1500, "Probe revised words", false), new SavedUpdate(3000, "Probe final text.", true)]
                    : ReadSavedUpdates(savedTrace);
                paster.CaptureTarget();
                var replayClock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var update in updates)
                {
                    var delay = update.ElapsedMs - updates[0].ElapsedMs - replayClock.Elapsed.TotalMilliseconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), timeout.Token);
                    if (update.Final) await paster.PasteAsync(update.Text, timeout.Token);
                    else paster.UpdatePreview(update.Text);
                }
                File.WriteAllText(Path.Combine(output, "expected.txt"), updates[^1].Text);
                if (injectClipboardReadFailure && !faultApi.Injected)
                    throw new InvalidOperationException("The recovery probe did not exercise the clipboard read failure.");
                DiagnosticTrace.Write("probe.completed", new { expectedLength = updates[^1].Text.Length, updates = updates.Length,
                    injectedReadFailure = faultApi.Injected });
                result = 0;
            }
            catch (Exception error)
            {
                DiagnosticTrace.Write("probe.failed", new { paster.InlinePreview, paster.HoldingReason }, error);
            }
            finally
            {
                paster.EndSession();
                await Task.Delay(1000); // Allow the existing clipboard restore queue to finish.
                await DiagnosticTrace.FlushAsync();
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { exitCode = result }));
                Application.ExitThread();
            }
        };
        timer.Start();
        File.WriteAllText(Path.Combine(output, "ready"), "Select the controlled empty Notepad tab, then create the go file.");
        Application.Run();
        return result;
    }

    private sealed record SavedUpdate(double ElapsedMs, string Text, bool Final);

    // Fault injection only in this opt-in diagnostic harness. All writes, key
    // injection, focus/range validation and restoration use production code.
    private sealed class SingleReadFailureClipboardApi : IWindowsClipboardApi
    {
        private readonly WindowsClipboardApi _inner = new();
        private int _writes;
        public bool Injected { get; private set; }
        public IDataObject? GetDataObject() => _inner.GetDataObject();
        public void SetText(string text) { _inner.SetText(text); _writes++; }
        public bool ContainsText()
        {
            if (_writes >= 3 && !Injected)
            {
                Injected = true;
                DiagnosticTrace.Write("probe.injected_clipboard_read_failure");
                return false;
            }
            return _inner.ContainsText();
        }
        public string GetText() => _inner.GetText();
        public void SetDataObject(IDataObject data) => _inner.SetDataObject(data);
        public void Clear() => _inner.Clear();
        public uint GetSequenceNumber() => _inner.GetSequenceNumber();
    }

    private static SavedUpdate[] ReadSavedUpdates(string path)
    {
        var updates = new List<SavedUpdate>();
        string? recordingId = null;
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var stage = entry.GetProperty("stage").GetString();
            if (stage is not ("workflow.preview_text_stages" or "workflow.final_text_stages")) continue;
            var id = entry.GetProperty("recordingId").GetString();
            recordingId ??= id;
            if (id != recordingId) throw new InvalidOperationException("Provide a trace with one recording only.");
            var final = stage == "workflow.final_text_stages";
            updates.Add(new SavedUpdate(entry.GetProperty("elapsedMs").GetDouble(),
                entry.GetProperty("data").GetProperty(final ? "normalized" : "dictionaryCorrected").GetString()!, final));
        }
        if (updates.Count < 2 || !updates[^1].Final || updates.Count(update => update.Final) != 1)
            throw new InvalidOperationException("Saved trace must contain previews followed by one final transcript.");
        return updates.ToArray();
    }
}
