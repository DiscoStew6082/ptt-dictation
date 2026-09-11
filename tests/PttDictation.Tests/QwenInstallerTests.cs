using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenInstallerTests
{
    [TestMethod]
    public async Task HealthyExistingInstallationIsValidatedWithoutDownloadsOrPackageChanges()
    {
        using var fixture = new Fixture();
        fixture.RegisterExisting();
        var progress = new List<QwenSetupProgress>();
        var result = await fixture.Installer.InstallAsync(new InlineProgress(progress.Add), CancellationToken.None);
        Assert.IsTrue(result.IsReady);
        Assert.AreEqual(0, fixture.Requests.Count);
        Assert.AreEqual(1, fixture.Commands.Count);
        Assert.AreEqual(fixture.ExistingPython, fixture.Commands[0].Executable);
        Assert.AreEqual(fixture.ExistingModel, fixture.Commands[0].Arguments[^1]);
        Assert.IsTrue(progress.Any(item => item.Message.Contains("No model download")));
        Assert.IsFalse(Directory.GetDirectories(Path.Combine(fixture.Root, "qwen"), "runtime-*").Any());
        Assert.AreEqual(fixture.ExistingModel, QwenInstallation.ReadRegisteredPaths(fixture.Root).Model);
    }

    [TestMethod]
    public async Task FreshSetupDownloadsPinnedAssetsExtractsPythonInstallsHashesAndRegistersLast()
    {
        using var fixture = new Fixture();
        fixture.OnRun = (_, arguments, _) =>
        {
            Assert.IsFalse(File.Exists(QwenInstallation.ManifestPath(fixture.Root)), "Registration occurred before validation completed.");
            if (arguments.Contains("install"))
            {
                Assert.IsTrue(arguments.Contains("--no-index") && arguments.Contains("--no-deps")
                    && arguments.Contains("--require-hashes") && arguments.Contains("--only-binary=:all:"));
            }
            return Task.CompletedTask;
        };
        var result = await fixture.Installer.InstallAsync(null, CancellationToken.None);
        Assert.IsTrue(result.IsReady);
        Assert.AreEqual(fixture.Plan.ModelFiles.Length + 1, fixture.Requests.Count);
        Assert.AreEqual(3, fixture.Commands.Count);
        var (python, model) = QwenInstallation.ReadRegisteredPaths(fixture.Root);
        StringAssert.Contains(python, Path.Combine("qwen", "runtime-"));
        Assert.IsTrue(File.Exists(python));
        Assert.IsTrue(Directory.Exists(model));
        Assert.IsTrue(fixture.Commands.All(command => command.Executable == python));
    }

    [TestMethod]
    public async Task BrokenRegisteredRuntimeGetsPrivateReplacementAndReusesExistingModel()
    {
        using var fixture = new Fixture();
        fixture.RegisterExisting();
        fixture.OnRun = (python, _, _) => python == fixture.ExistingPython
            ? Task.FromException(new InvalidOperationException("Dependency import failed.")) : Task.CompletedTask;
        var result = await fixture.Installer.InstallAsync(null, CancellationToken.None);
        Assert.IsTrue(result.IsReady);
        Assert.AreEqual(1, fixture.Requests.Count, "Only the small Python runtime should download; the model is already verified.");
        var (python, model) = QwenInstallation.ReadRegisteredPaths(fixture.Root);
        Assert.AreNotEqual(fixture.ExistingPython, python);
        Assert.AreEqual(fixture.ExistingModel, model);
        Assert.AreEqual("old python", File.ReadAllText(fixture.ExistingPython));
    }

    [TestMethod]
    public async Task CancelledValidationPreservesExactRegistrationAndDownloadsNothing()
    {
        using var fixture = new Fixture();
        fixture.RegisterExisting();
        var before = File.ReadAllBytes(QwenInstallation.ManifestPath(fixture.Root));
        using var cancel = new CancellationTokenSource();
        fixture.OnRun = (_, _, _) => { cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(null, cancel.Token));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(QwenInstallation.ManifestPath(fixture.Root)));
        Assert.AreEqual(0, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task FailedFreshRuntimeNeverReplacesPreviousManifestAndRemovesItsPrivateDirectory()
    {
        using var fixture = new Fixture();
        fixture.RegisterExisting();
        var before = File.ReadAllBytes(QwenInstallation.ManifestPath(fixture.Root));
        fixture.OnRun = (_, _, _) => Task.FromException(new InvalidOperationException("Injected validation failure."));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(null, CancellationToken.None));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(QwenInstallation.ManifestPath(fixture.Root)));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen"), "runtime-*").Length);
    }

    [TestMethod]
    public async Task CorruptDownloadIsRejectedWithoutRegistrationOrRunningAnyCode()
    {
        using var fixture = new Fixture();
        fixture.OnRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wrong")) });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(null, CancellationToken.None));
        Assert.IsFalse(File.Exists(QwenInstallation.ManifestPath(fixture.Root)));
        Assert.AreEqual(0, fixture.Commands.Count);
    }

    [TestMethod]
    public async Task CancellationDuringDownloadIsPromptAndLeavesRegistrationUntouched()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnRequest = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("Unreachable.");
        };
        var work = fixture.Installer.InstallAsync(null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsFalse(File.Exists(QwenInstallation.ManifestPath(fixture.Root)));
    }

    [TestMethod]
    public async Task ExistingPartialDownloadResumesWithVerifiedRangeAndHash()
    {
        using var fixture = new Fixture();
        var payload = Encoding.UTF8.GetBytes("complete model bytes");
        var destination = Path.Combine(fixture.Root, "resume.bin");
        await File.WriteAllBytesAsync(destination + ".partial", payload[..5]);
        fixture.OnRequest = (request, _) =>
        {
            Assert.AreEqual(5L, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[5..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, payload.Length - 1, payload.Length);
            return Task.FromResult(response);
        };
        await fixture.Installer.DownloadAsync(new Uri("https://huggingface.co/test/resume.bin"), destination,
            Hash(payload), payload.Length, "test", null, CancellationToken.None);
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
    }

    [TestMethod]
    public async Task UntrustedRedirectIsRejectedBeforeSecondRequest()
    {
        using var fixture = new Fixture();
        fixture.OnRequest = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://untrusted.invalid/model");
            return Task.FromResult(response);
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Installer.DownloadAsync(
            new Uri("https://huggingface.co/test/file"), Path.Combine(fixture.Root, "file"), Hash([1]), 1, "file", null, CancellationToken.None));
        Assert.AreEqual(1, fixture.Requests.Count);
        Assert.IsFalse(QwenInstaller.TrustedDownload(new Uri("http://huggingface.co/file")));
        Assert.IsFalse(QwenInstaller.TrustedDownload(new Uri("https://huggingface.co.evil.invalid/file")));
        Assert.ThrowsExactly<InvalidOperationException>(() => QwenInstaller.RequireLocalPath("//server/share/file"));
    }

    [TestMethod]
    public async Task ArchiveTraversalCannotWriteOutsidePrivateRuntime()
    {
        using var fixture = new Fixture();
        var archive = Path.Combine(fixture.Root, "unsafe.tar.gz");
        await File.WriteAllBytesAsync(archive, Archive("../escaped.txt", [1, 2, 3]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => QwenInstaller.ExtractRuntimeAsync(
            archive, Path.Combine(fixture.Root, "private"), CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "escaped.txt")));
    }

    [TestMethod]
    public async Task SecondSetupCannotMutateInstallationWhileFirstIsValidating()
    {
        using var fixture = new Fixture();
        fixture.RegisterExisting();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnRun = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var first = fixture.Installer.InstallAsync(null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Installer.InstallAsync(null, CancellationToken.None));
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);
    }

    [TestMethod]
    public async Task SetupProcessCancellationKillsItsRealOwnedChildAndDrainsOutput()
    {
        using var fixture = new Fixture();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var pidPath = Path.Combine(fixture.Root, "pid.txt");
        using var cancel = new CancellationTokenSource();
        var running = QwenSetupProcess.RunAsync(powershell, new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$PID | Set-Content -LiteralPath '" + pidPath.Replace("'", "''") + "'; Start-Sleep -Seconds 60" }, null, cancel.Token);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(pidPath) && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
            Assert.IsTrue(File.Exists(pidPath), "Owned fixture did not start.");
            var pid = int.Parse((await File.ReadAllTextAsync(pidPath)).Trim());
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(8)));
            try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited, "Cancelled setup process survived."); }
            catch (ArgumentException) { }
        }
        finally
        {
            cancel.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) { }
        }
    }
    [TestMethod]
    public async Task SetupProcessReportsFailureWithBoundedDiagnosticTail()
    {
        using var fixture = new Fixture();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(fixture.Root, "failure.ps1");
        await File.WriteAllTextAsync(script, "[Console]::Error.Write(('x' * 20000)); [Console]::Error.Write('fixture-tail'); exit 7");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            QwenSetupProcess.RunAsync(powershell, new[] { "-NoProfile", "-NonInteractive", "-Command", File.ReadAllText(script) }, null, CancellationToken.None));
        StringAssert.Contains(error.Message, "exit 7");
        StringAssert.Contains(error.Message, "fixture-tail");
        Assert.IsLessThan(4500, error.Message.Length);
    }

    [TestMethod]
    public async Task HeaderDeadlineCancelsUnderlyingHttpRequest()
    {
        using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        var cancelled = false;
        fixture.OnRequest = async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new AssertFailedException("Unreachable."); }
            finally { cancelled = token.IsCancellationRequested; }
        };
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => fixture.Installer.DownloadAsync(
            new Uri("https://huggingface.co/test/file"), Path.Combine(fixture.Root, "file"), Hash([1]), 1,
            "file", null, CancellationToken.None));
        Assert.IsTrue(cancelled, "Timing out must cancel the underlying HTTP request.");
    }

    [TestMethod]
    public async Task BodyDeadlineCancelsUnderlyingReadAndDisposesResponse()
    {
        using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        var stream = new StallingStream();
        fixture.OnRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) });
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => fixture.Installer.DownloadAsync(
            new Uri("https://huggingface.co/test/file"), Path.Combine(fixture.Root, "file"), Hash([1]), 1,
            "file", null, CancellationToken.None));
        Assert.IsTrue(stream.Cancelled && stream.Disposed, "A stalled body read and its response stream must both be retired.");
    }

    [TestMethod]
    public async Task PresentationFailureCannotUndoCommittedRegistrationOrDeleteRuntime()
    {
        using var fixture = new Fixture();
        var result = await fixture.Installer.InstallAsync(new InlineProgress(value =>
        {
            if (value.Message.StartsWith("Qwen is ready.")) throw new InvalidOperationException("Closed progress UI.");
        }), CancellationToken.None);
        Assert.IsTrue(result.IsReady);
        Assert.IsTrue(File.Exists(QwenInstallation.ReadRegisteredPaths(fixture.Root).Python));
    }

    private sealed class StallingStream : Stream
    {
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Archive(string name, byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
        return output.ToArray();
    }
    private sealed class InlineProgress(Action<QwenSetupProgress> report) : IProgress<QwenSetupProgress>
    { public void Report(QwenSetupProgress value) => report(value); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "qwen-setup-test-" + Guid.NewGuid().ToString("N"));
        public string ExistingPython => Path.Combine(Root, "existing-python.exe");
        public string ExistingModel => Path.Combine(Root, "existing-model");
        public QwenInstallerPlan Plan { get; }
        public QwenInstaller Installer { get; }
        public List<string> Requests { get; } = [];
        public List<(string Executable, IReadOnlyList<string> Arguments)> Commands { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnRequest { get; set; }
        public Func<string, IReadOnlyList<string>, CancellationToken, Task>? OnRun { get; set; }
        private readonly HttpClient _http;
        private readonly Dictionary<string, byte[]> _assets;
        public Fixture(TimeSpan? downloadTimeout = null)
        {
            Directory.CreateDirectory(Root);
            _assets = new()
            {
                ["config.json"] = Encoding.UTF8.GetBytes("""{"model_type":"qwen3_asr","text_config":{"hidden_size":2048}}"""),
                ["model.safetensors"] = Encoding.UTF8.GetBytes("fixture model"),
                ["python.tar.gz"] = Archive("python/python.exe", Encoding.UTF8.GetBytes("fixture python"))
            };
            Plan = new("https://github.com/test/python.tar.gz", Hash(_assets["python.tar.gz"]), _assets["python.tar.gz"].Length,
                "Qwen/Qwen3-ASR-1.7B-hf", "fixture-revision",
                _assets.Where(entry => entry.Key != "python.tar.gz").Select(entry => new QwenModelFile(entry.Key, Hash(entry.Value), entry.Value.Length)).ToArray())
                { Requirements = "fixture @ https://files.pythonhosted.org/fixture.whl --hash=sha256:fixed" };
            _http = new HttpClient(new Handler(async (request, token) =>
            {
                Requests.Add(request.RequestUri!.AbsoluteUri);
                if (OnRequest is not null) return await OnRequest(request, token);
                var name = Path.GetFileName(request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_assets[name]) };
            }));
            Installer = new(Root, Plan, _http, async (python, arguments, _, token) =>
            {
                Commands.Add((python, arguments));
                if (OnRun is not null) await OnRun(python, arguments, token);
            }) { DownloadIdleTimeout = downloadTimeout ?? TimeSpan.FromSeconds(60) };
        }
        public void RegisterExisting()
        {
            Directory.CreateDirectory(ExistingModel);
            foreach (var file in Plan.ModelFiles) File.WriteAllBytes(Path.Combine(ExistingModel, file.Name), _assets[file.Name]);
            File.WriteAllText(ExistingPython, "old python");
            File.WriteAllText(QwenInstallation.ManifestPath(Root), JsonSerializer.Serialize(new
                { schema = 1, pythonPath = ExistingPython, modelPath = ExistingModel }) + Environment.NewLine);
        }
        public void Dispose() { _http.Dispose(); Directory.Delete(Root, recursive: true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}