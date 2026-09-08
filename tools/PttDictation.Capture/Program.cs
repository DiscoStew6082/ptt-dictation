using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PttDictation.Core;

var appRoot = args.Length > 0 ? Path.GetFullPath(args[0])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PttDictation");
var traceRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(appRoot, "diagnostics", "capture");
if (!Directory.Exists(appRoot))
    throw new DirectoryNotFoundException("The existing dictation data directory was not found: " + appRoot);
var mutexName = "Local\\PttDictation.Capture." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appRoot.ToUpperInvariant())))[..24];
using var singleton = new Mutex(true, mutexName, out var created);
if (!created)
    return;
using var diagnostics = DiagnosticTrace.Configure(traceRoot, "stable-app-external-audio-capture");
DiagnosticTrace.Write("capture.started", new { appRoot, traceRoot, processId = Environment.ProcessId, executable = Environment.ProcessPath, scope = "Full utterance WAV observation only; working app unchanged" });
if (args.Length > 2)
{
    try
    {
        var appBinary = Path.GetFullPath(args[2]);
        using var binary = File.OpenRead(appBinary);
        DiagnosticTrace.Write("capture.app_build", new { path = appBinary, sha256 = Convert.ToHexString(SHA256.HashData(binary)), version = System.Diagnostics.FileVersionInfo.GetVersionInfo(appBinary).FileVersion });
    }
    catch (Exception error) { DiagnosticTrace.Write("capture.app_build_failed", error: error); }
}

var settingsPath = Path.Combine(appRoot, "settings.json");
try
{
    using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
    // Copy only model/device choices. Never log the entire settings file or phrase replacements.
    var choices = settings.RootElement.EnumerateObject()
        .Where(property => property.Name.Contains("model", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("device", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("engine", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(property => property.Name, property => property.Value.Clone());
    DiagnosticTrace.Write("capture.configuration", choices);
}
catch (Exception error) { DiagnosticTrace.Write("capture.configuration_failed", error: error); }

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
var capture = new RecordingCapture(appRoot, (path, audio) =>
{
    var id = DiagnosticTrace.BeginRecording("external-full-recording-observed");
    DiagnosticTrace.Write("capture.recording_ready", new { source = Path.GetFileName(path), bytes = audio.Length }, recordingId: id);
    DiagnosticTrace.RetainRecording(id, audio);
}, (path, error) => DiagnosticTrace.Write("capture.failed", new { source = Path.GetFileName(path) }, error));
try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
catch (OperationCanceledException) { }
finally { await capture.DisposeAsync(); }
DiagnosticTrace.Write("capture.stopped");
await DiagnosticTrace.FlushAsync();
