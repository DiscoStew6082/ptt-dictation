using System.Runtime.InteropServices;
using System.Text;

namespace PttDictation.App;

internal sealed record QwenNativeHardwareStatus(
    bool IsSupported, string Reason, int? CudaDriverVersion = null, string? DeviceName = null,
    int? ComputeCapabilityMajor = null, int? ComputeCapabilityMinor = null, ulong? TotalMemoryBytes = null);

internal sealed record QwenNativeHardwareFacts(
    bool IsWindows, bool IsX64Process, string? CudaVisibleDevices,
    int? CudaDriverVersion = null, int? DeviceCount = null, string? DeviceName = null,
    int? ComputeCapabilityMajor = null, int? ComputeCapabilityMinor = null,
    ulong? TotalMemoryBytes = null, string? ProbeError = null);

internal static class QwenNativeHardware
{
    // Driver API version encoding: 1000 * major + 10 * minor.
    private const int MinimumDriverVersion = 12040;
    private const ulong MinimumMemoryBytes = 8UL * 1024 * 1024 * 1024;

    public static QwenNativeHardwareStatus Evaluate(QwenNativeHardwareFacts facts)
    {
        string? reason = null;
        if (!facts.IsWindows || !facts.IsX64Process)
            reason = "Native Qwen requires Windows x64.";
        else if (facts.CudaVisibleDevices is not null)
            reason = "Native Qwen automatic setup cannot verify CUDA device 0 while CUDA_VISIBLE_DEVICES is set.";
        else if (facts.ProbeError is not null)
            reason = facts.ProbeError;
        else if (facts.CudaDriverVersion is null || facts.CudaDriverVersion < MinimumDriverVersion)
            reason = "Native Qwen requires an NVIDIA driver supporting CUDA 12.4 or newer.";
        else if (facts.DeviceCount is null or <= 0 || string.IsNullOrWhiteSpace(facts.DeviceName))
            reason = "Native Qwen could not identify NVIDIA CUDA device 0.";
        else if (facts.ComputeCapabilityMajor is null or < 8 || facts.ComputeCapabilityMinor is null or < 0)
            reason = "Native Qwen requires compute capability 8.0 or newer on CUDA device 0 (Ampere or newer).";
        else if (facts.TotalMemoryBytes is null || facts.TotalMemoryBytes < MinimumMemoryBytes)
            reason = "Native Qwen requires at least 8 GiB of total GPU memory on CUDA device 0.";

        return new(reason is null,
            reason ?? "NVIDIA CUDA device 0 supports native Qwen (CUDA 12.4+, compute capability 8.0+, at least 8 GiB GPU memory).",
            facts.CudaDriverVersion, facts.DeviceName, facts.ComputeCapabilityMajor,
            facts.ComputeCapabilityMinor, facts.TotalMemoryBytes);
    }

    // A fixed number of driver queries, with no model, context, allocation, subprocess, or retry.
    // Call during asynchronous setup, not from a UI status getter. The OS driver owns call latency.
    public static QwenNativeHardwareStatus Probe()
    {
        var facts = new QwenNativeHardwareFacts(OperatingSystem.IsWindows(),
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"));
        if (!facts.IsWindows || !facts.IsX64Process || facts.CudaVisibleDevices is not null)
            return Evaluate(facts);

        try
        {
            Check(cuInit(0), "initialization");
            Check(cuDriverGetVersion(out var driverVersion), "driver version");
            facts = facts with { CudaDriverVersion = driverVersion };
            if (driverVersion < MinimumDriverVersion) return Evaluate(facts);
            Check(cuDeviceGetCount(out var count), "device count");
            facts = facts with { DeviceCount = count };
            if (count <= 0) return Evaluate(facts);
            // The worker selects CUDA ordinal 0. Never choose a different eligible adapter.
            Check(cuDeviceGet(out var device, 0), "device 0");
            var name = new StringBuilder(256);
            Check(cuDeviceGetName(name, name.Capacity, device), "device name");
            facts = facts with { DeviceName = name.ToString() };
            Check(cuDeviceGetAttribute(out var major, 75, device), "compute capability major");
            Check(cuDeviceGetAttribute(out var minor, 76, device), "compute capability minor");
            facts = facts with { ComputeCapabilityMajor = major, ComputeCapabilityMinor = minor };
            Check(cuDeviceTotalMem(out var bytes, device), "total GPU memory");
            facts = facts with { TotalMemoryBytes = bytes.ToUInt64() };
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            facts = facts with { ProbeError = "A compatible NVIDIA CUDA driver could not be loaded." };
        }
        catch (InvalidOperationException error)
        {
            facts = facts with { ProbeError = error.Message };
        }
        return Evaluate(facts);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"NVIDIA CUDA {operation} query failed (driver error {result}).");
    }

    // Restrict resolution to the installed Windows driver, never a DLL in the working directory.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuInit(uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuDriverGetVersion(out int version);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuDeviceGetCount(out int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuDeviceGet(out int device, int ordinal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern int cuDeviceGetName(StringBuilder name, int length, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true)]
    private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", ExactSpelling = true, EntryPoint = "cuDeviceTotalMem_v2")]
    private static extern int cuDeviceTotalMem(out UIntPtr bytes, int device);
}
