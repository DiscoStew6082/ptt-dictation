using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed record QwenTranscriberOptions(string PythonPath, string ModelPath, string WorkerPath);

/// <summary>Owns one offline, resident Qwen worker and serializes its JSON-lines protocol.</summary>
internal sealed class PersistentQwenTranscriber : ITranscriber, IWarmableTranscriber, IDisposable
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);
    private readonly QwenTranscriberOptions _options;
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private Worker? _worker;
    private bool _disposed;

    public PersistentQwenTranscriber(QwenTranscriberOptions options)
        : this(options, start => new Process { StartInfo = start }, DefaultStartupTimeout, DefaultRequestTimeout)
    {
    }

    internal PersistentQwenTranscriber(QwenTranscriberOptions options,
        Func<ProcessStartInfo, Process> processFactory, TimeSpan startupTimeout, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        _options = new(
            RequireLocalPath(options.PythonPath, directory: false),
            RequireLocalPath(options.ModelPath, directory: true),
            RequireLocalPath(options.WorkerPath, directory: false));
        _processFactory = processFactory;
        _startupTimeout = startupTimeout;
        _requestTimeout = requestTimeout;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        using var operation = CreateOperation(cancellationToken);
        await _requests.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            _ = await EnsureWorkerAsync(operation.Token).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        using var operation = CreateOperation(cancellationToken);
        var audioPath = RequireLocalPath(wavPath, directory: false);
        await _requests.WaitAsync(operation.Token).ConfigureAwait(false);
        Worker? worker = null;
        var recordingId = DiagnosticTrace.CurrentRecordingId;
        try
        {
            worker = await EnsureWorkerAsync(operation.Token).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_requestTimeout);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, timeout.Token);
            var id = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var payload = JsonSerializer.Serialize(new { id, audioPath });
                await worker.Process.StandardInput.WriteLineAsync(payload.AsMemory(), request.Token)
                    .WaitAsync(request.Token).ConfigureAwait(false);
                await worker.Process.StandardInput.FlushAsync(request.Token)
                    .WaitAsync(request.Token).ConfigureAwait(false);
                using var response = await worker.ReadMessageAsync(request.Token).ConfigureAwait(false);
                var root = response.RootElement;
                if (ReadString(root, "id") != id)
                    throw new InvalidDataException("Qwen returned a response for a different request.");
                var type = ReadString(root, "type");
                if (type == "error")
                    throw new InvalidOperationException("Qwen transcription failed: " + ReadString(root, "message"));
                if (type != "result")
                    throw new InvalidDataException("Qwen returned an unsupported response type.");
                var text = ReadString(root, "text"); // An empty transcript is a valid silence result.
                request.Token.ThrowIfCancellationRequested();
                stopwatch.Stop();
                DiagnosticTrace.Write("qwen.request_completed",
                    new { elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds, text },
                    recordingId: recordingId);
                return new TranscriptResult(text, stopwatch.Elapsed, null);
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Qwen transcription did not finish within {_requestTimeout.TotalSeconds:g} seconds.");
            }
        }
        catch (Exception error)
        {
            if (worker is not null)
            {
                Retire(worker);
                DiagnosticTrace.Write(operation.IsCancellationRequested ? "qwen.request_cancelled" : "qwen.request_failed",
                    new { stderrTail = worker.ErrorTail }, error, recordingId);
            }
            if (operation.IsCancellationRequested)
                throw new OperationCanceledException("Qwen transcription was cancelled.", error, operation.Token);
            throw;
        }
        finally
        {
            _requests.Release();
        }
    }

    private CancellationTokenSource CreateOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
    }

    private async Task<Worker> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Worker? previous;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { } running && !running.Process.HasExited) return running;
            previous = _worker;
            _worker = null;
        }
        previous?.Stop();

        Worker worker;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var process = _processFactory(CreateStartInfo());
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Windows could not start the Qwen worker.");
                var job = QwenOwnedProcessJob.Assign(process);
                worker = new Worker(process, job);
                _worker = worker;
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
                throw;
            }
        }

        using var timeout = new CancellationTokenSource(_startupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await worker.ReadMessageAsync(startup.Token).ConfigureAwait(false);
            var root = response.RootElement;
            if (ReadString(root, "type") != "ready"
                || !root.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number
                || !protocol.TryGetInt32(out var version)
                || version != 1)
                throw new InvalidDataException("Qwen worker did not announce supported protocol version 1.");
            startup.Token.ThrowIfCancellationRequested();
            DiagnosticTrace.Write("qwen.worker_ready", new { processId = worker.Process.Id });
            return worker;
        }
        catch (Exception error)
        {
            Retire(worker);
            DiagnosticTrace.Write("qwen.start_failed", new { stderrTail = worker.ErrorTail }, error);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Qwen startup was cancelled.", error, cancellationToken);
            if (error is OperationCanceledException && timeout.IsCancellationRequested)
                throw new TimeoutException($"Qwen worker did not become ready within {_startupTimeout.TotalSeconds:g} seconds.", error);
            throw;
        }
    }

    internal ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = _options.PythonPath,
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(_options.WorkerPath);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(_options.ModelPath);
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["TOKENIZERS_PARALLELISM"] = "false";
        return start;
    }

    private void Retire(Worker worker)
    {
        lock (_lifecycle)
        {
            if (ReferenceEquals(_worker, worker)) _worker = null;
        }
        worker.Stop();
    }

    public void Dispose()
    {
        Worker? worker;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
            _worker = null;
        }
        // Linked operations release their own registrations. Do not dispose the
        // semaphore while in-flight/waiting operations still have a finally path.
        _shutdown.Cancel();
        worker?.Stop();
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Qwen protocol response is missing string '{property}'.");
        return value.GetString()!;
    }

    private static string RequireLocalPath(string value, bool directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Qwen requires an absolute local filesystem path.", nameof(value));
        var path = Path.GetFullPath(value);
        if (directory ? !Directory.Exists(path) : !File.Exists(path))
            throw new FileNotFoundException(directory ? "The local Qwen model directory was not found." : "A required local Qwen file was not found.", path);
        return path;
    }

    private sealed class Worker
    {
        private const int MaximumMessageCharacters = 1024 * 1024;
        private const int MaximumErrorCharacters = 8192;
        private readonly object _errors = new();
        private string _errorTail = "";
        private int _stopped;
        private readonly Task _errorDrain;
        private readonly QwenOwnedProcessJob _job;
        public Process Process { get; }

        public Worker(Process process, QwenOwnedProcessJob job)
        {
            Process = process;
            _job = job;
            _errorDrain = DrainErrorsAsync();
        }

        public string ErrorTail { get { lock (_errors) return _errorTail; } }

        public async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
        {
            // Incremental reading bounds even a worker which never emits newline.
            var line = new StringBuilder();
            var character = new char[1];
            while (true)
            {
                var count = await Process.StandardOutput.ReadAsync(character.AsMemory(), cancellationToken)
                    .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Qwen worker exited before completing its protocol response.");
                if (character[0] == '\n') break;
                if (line.Length >= MaximumMessageCharacters)
                    throw new InvalidDataException("Qwen worker response exceeded the protocol size limit.");
                line.Append(character[0]);
            }
            try { return JsonDocument.Parse(line.ToString()); }
            catch (JsonException error) { throw new InvalidDataException("Qwen worker returned malformed JSON.", error); }
        }

        private async Task DrainErrorsAsync()
        {
            var buffer = new char[1024];
            try
            {
                int count;
                while ((count = await Process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                {
                    lock (_errors)
                    {
                        _errorTail += new string(buffer, 0, count);
                        if (_errorTail.Length > MaximumErrorCharacters)
                            _errorTail = _errorTail[^MaximumErrorCharacters..];
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The owned process/pipe can close while cancellation drains it.
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            // Closing the job is authoritative for the process tree, including
            // when the owning app is terminated abruptly by its updater.
            _job.Dispose();
            try
            {
                if (!Process.WaitForExit(3000))
                {
                    Process.Kill(entireProcessTree: true);
                    if (!Process.WaitForExit(3000))
                        throw new InvalidOperationException("The owned Qwen worker did not exit during cleanup.");
                }
            }
            finally
            {
                Process.Dispose();
                _ = _errorDrain.Exception; // Drain catches pipe shutdown; observe any unexpected fault.
            }
        }
    }
}

/// <summary>Windows closes this handle on owner death, terminating only its worker job.</summary>
internal sealed class QwenOwnedProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private QwenOwnedProcessJob(SafeFileHandle handle) => _handle = handle;

    public static QwenOwnedProcessJob Assign(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new QwenOwnedProcessJob(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}