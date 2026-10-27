using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenNativeHardwareTests
{
    private static QwenNativeHardwareFacts Supported => new(
        IsWindows: true, IsX64Process: true, CudaVisibleDevices: null,
        CudaDriverVersion: 12040, DeviceCount: 1, DeviceName: "NVIDIA GeForce RTX 3060",
        ComputeCapabilityMajor: 8, ComputeCapabilityMinor: 6, TotalMemoryBytes: 12UL * 1024 * 1024 * 1024);

    [TestMethod]
    public void SupportedDeviceZeroRetainsCapabilityMetadata()
    {
        var result = QwenNativeHardware.Evaluate(Supported);

        Assert.IsTrue(result.IsSupported, result.Reason);
        Assert.AreEqual("NVIDIA GeForce RTX 3060", result.DeviceName);
        Assert.AreEqual(12040, result.CudaDriverVersion);
        Assert.AreEqual(8, result.ComputeCapabilityMajor);
        Assert.AreEqual(6, result.ComputeCapabilityMinor);
        Assert.AreEqual(12UL * 1024 * 1024 * 1024, result.TotalMemoryBytes);
    }

    [TestMethod]
    public void ExactMinimumHardwareIsEligible()
    {
        var result = QwenNativeHardware.Evaluate(Supported with
        {
            ComputeCapabilityMinor = 0,
            TotalMemoryBytes = 8_589_934_592,
        });

        Assert.IsTrue(result.IsSupported, result.Reason);
    }

    [TestMethod]
    public void NewerHardwareRemainsEligible()
    {
        var result = QwenNativeHardware.Evaluate(Supported with
        {
            CudaDriverVersion = 13000,
            ComputeCapabilityMajor = 10,
            ComputeCapabilityMinor = 0,
        });

        Assert.IsTrue(result.IsSupported, result.Reason);
    }

    [TestMethod]
    [DynamicData(nameof(IneligibleHardware))]
    public void IneligibleOrUnknownHardwareFailsClosed(object suppliedFacts, string expectedReason)
    {
        var result = QwenNativeHardware.Evaluate((QwenNativeHardwareFacts)suppliedFacts);

        Assert.IsFalse(result.IsSupported);
        StringAssert.Contains(result.Reason, expectedReason);
    }

    public static IEnumerable<object[]> IneligibleHardware()
    {
        yield return [Supported with { IsWindows = false }, "Windows x64"];
        yield return [Supported with { IsX64Process = false }, "Windows x64"];
        yield return [Supported with { CudaVisibleDevices = "" }, "CUDA_VISIBLE_DEVICES"];
        yield return [Supported with { CudaVisibleDevices = "0" }, "CUDA_VISIBLE_DEVICES"];
        yield return [Supported with { CudaVisibleDevices = "1,0" }, "CUDA_VISIBLE_DEVICES"];
        yield return [Supported with { CudaVisibleDevices = "GPU-1234" }, "CUDA_VISIBLE_DEVICES"];
        yield return [Supported with { CudaDriverVersion = null }, "CUDA 12.4"];
        yield return [Supported with { CudaDriverVersion = 12030 }, "CUDA 12.4"];
        yield return [Supported with { DeviceCount = null }, "device 0"];
        yield return [Supported with { DeviceCount = 0 }, "device 0"];
        yield return [Supported with { DeviceName = null }, "device 0"];
        yield return [Supported with { DeviceName = " " }, "device 0"];
        yield return [Supported with { ComputeCapabilityMajor = null }, "compute capability"];
        yield return [Supported with { ComputeCapabilityMajor = 7, ComputeCapabilityMinor = 5 }, "compute capability"];
        yield return [Supported with { ComputeCapabilityMinor = null }, "compute capability"];
        yield return [Supported with { ComputeCapabilityMinor = -1 }, "compute capability"];
        yield return [Supported with { TotalMemoryBytes = null }, "8 GiB"];
        yield return [Supported with { TotalMemoryBytes = 8_589_934_591 }, "8 GiB"];
        // More adapters must not make an unsupported ordinal 0 eligible.
        yield return [Supported with { DeviceCount = 2, ComputeCapabilityMajor = 7 }, "compute capability"];
        yield return [Supported with { ProbeError = "Driver query failed." }, "Driver query failed."];
    }
}
