using System.Runtime.InteropServices;
using PttDictation.Core;

namespace PttDictation.App;

internal static class GraphicsHardware
{
    private const string NvidiaPciVendorId = "VEN_10DE";

    public static bool HasNvidiaAdapter()
    {
        if (!OperatingSystem.IsWindows()) return false;

        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) return false;
            if (IsNvidiaAdapter(device.DeviceId, device.Description)) return true;
        }
    }

    internal static bool IsNvidiaAdapter(string? deviceId, string? description) =>
        deviceId?.Contains(NvidiaPciVendorId, StringComparison.OrdinalIgnoreCase) == true
        || description?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;

    internal static AppSettings UseSupportedSettings(AppSettings settings, bool hasNvidiaGpu)
    {
        if (hasNvidiaGpu) return settings;
        return settings with
        {
            DevicePreference = DevicePreference.Cpu,
            RuntimePath = settings.DevicePreference == DevicePreference.Cuda ? null : settings.RuntimePath,
            FinalTranscriptionEngine = FinalTranscriptionEngine.Parakeet
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);
}
