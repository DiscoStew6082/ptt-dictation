using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TextProviderDiagnosticsTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize() => directory = Path.Combine(Path.GetTempPath(), "ptt-provider-diagnostics-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory, recursive: false);
    }

    [TestMethod]
    public async Task ProviderFailuresAreLoggedOnceAndRethrownWithoutReplacementOrRetry()
    {
        using var configured = DiagnosticTrace.Configure(directory, "provider-test");
        Exception[] failures =
        [
            new ElementNotAvailableException("private document content"),
            new InvalidOperationException("private document content"),
            new NotSupportedException("private document content"),
            new COMException("private document content", unchecked((int)0x80040201)),
            new ArgumentException("private document content")
        ];
        foreach (var failure in failures)
        {
            var calls = 0;
            Exception? caught = null;
            try
            {
                TextProviderDiagnostics.Observe<object>("selection.get_text", () =>
                {
                    calls++;
                    throw failure;
                });
                Assert.Fail("The original provider failure must escape.");
            }
            catch (Exception error) { caught = error; }
            Assert.AreSame(failure, caught);
            Assert.AreEqual(1, calls);
        }
        await DiagnosticTrace.FlushAsync();
        var errorPath = Path.Combine(directory, "errors.jsonl");
        Assert.IsTrue(File.Exists(errorPath), "Provider failures must appear in the error index.");
        Assert.HasCount(failures.Length, File.ReadAllLines(errorPath));
    }

    [TestMethod]
    public async Task SuccessReturnsSameObjectWithOneReadAndNoErrorEntry()
    {
        using var configured = DiagnosticTrace.Configure(directory, "provider-test");
        var expected = new object();
        var calls = 0;
        var result = TextProviderDiagnostics.Observe("document.range", () => { calls++; return expected; });
        await DiagnosticTrace.FlushAsync();

        Assert.AreSame(expected, result);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "errors.jsonl")));
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "events.jsonl")).Contains("target.provider_operation_failed"));
    }

    [TestMethod]
    public async Task NonProviderExceptionEscapesWithoutBeingClassifiedAsProviderFailure()
    {
        using var configured = DiagnosticTrace.Configure(directory, "provider-test");
        var failure = new IOException("not a provider failure");
        var calls = 0;
        var caught = Assert.ThrowsExactly<IOException>(() =>
            TextProviderDiagnostics.Observe<object>("document.range", () => { calls++; throw failure; }));
        await DiagnosticTrace.FlushAsync();

        Assert.AreSame(failure, caught);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "errors.jsonl")));
    }

    [TestMethod]
    public async Task ErrorIndexContainsOperationTypeAndCodeWithoutProviderMessageOrText()
    {
        using var configured = DiagnosticTrace.Configure(directory, "provider-test");
        var recordingId = DiagnosticTrace.BeginRecording("test");
        const string secret = "private text from the focused document";
        var failure = new COMException(secret, unchecked((int)0x80040201));
        Assert.ThrowsExactly<COMException>(() =>
            TextProviderDiagnostics.Observe<string>("selection.get_text", () => throw failure));
        await DiagnosticTrace.FlushAsync();

        var errorPath = Path.Combine(directory, "errors.jsonl");
        Assert.IsTrue(File.Exists(errorPath), "Provider operation must be present in the error index.");
        var lines = File.ReadAllLines(errorPath);
        Assert.HasCount(1, lines);
        using var entry = JsonDocument.Parse(lines[0]);
        var root = entry.RootElement;
        Assert.AreEqual("target.provider_operation_failed", root.GetProperty("stage").GetString());
        Assert.AreEqual(recordingId, root.GetProperty("recordingId").GetString());
        var data = root.GetProperty("data");
        Assert.AreEqual("selection.get_text", data.GetProperty("operation").GetString());
        Assert.AreEqual(typeof(COMException).FullName, data.GetProperty("exceptionType").GetString());
        Assert.AreEqual(failure.HResult, data.GetProperty("HResult").GetInt32());
        Assert.AreEqual(3, data.EnumerateObject().Count());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("exception").ValueKind);
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            Assert.IsFalse(File.ReadAllText(file).Contains(secret), "Diagnostic files must not include provider exception messages.");
    }
}
