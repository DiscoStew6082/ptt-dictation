using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal static class TcpListenerProcessInspector
{
    private const int AddressFamilyInterNetwork = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    public static bool IsOwnedBy(IPAddress localAddress, int port, int processId)
    {
        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort
            || processId <= 0)
        {
            return false;
        }

        var expectedAddress = EncodeAddress(localAddress);
        return AnyRow(
            TcpTableClass.OwnerPidListener,
            row => row.LocalAddress == expectedAddress
                && DecodePort(row.LocalPort) == port
                && row.OwningProcessId == processId);
    }

    public static bool IsConnectionOwnedBy(Socket clientSocket, int processId)
    {
        if (processId <= 0
            || !clientSocket.Connected
            || clientSocket.LocalEndPoint is not IPEndPoint clientEndpoint
            || clientSocket.RemoteEndPoint is not IPEndPoint serverEndpoint
            || clientEndpoint.AddressFamily != AddressFamily.InterNetwork
            || serverEndpoint.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var serverAddress = EncodeAddress(serverEndpoint.Address);
        var clientAddress = EncodeAddress(clientEndpoint.Address);
        return AnyRow(
            TcpTableClass.OwnerPidAll,
            row => row.State == TcpStateEstablished
                && row.LocalAddress == serverAddress
                && DecodePort(row.LocalPort) == serverEndpoint.Port
                && row.RemoteAddress == clientAddress
                && DecodePort(row.RemotePort) == clientEndpoint.Port
                && row.OwningProcessId == processId);
    }

    private static bool AnyRow(TcpTableClass tableClass, Func<TcpRowOwnerPid, bool> predicate)
    {
        var bufferSize = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            AddressFamilyInterNetwork,
            tableClass,
            reserved: 0);
        if (result != ErrorInsufficientBuffer)
        {
            if (result == ErrorSuccess && bufferSize == 0)
            {
                return false;
            }

            throw new Win32Exception((int)result);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = GetExtendedTcpTable(
                    buffer,
                    ref bufferSize,
                    order: false,
                    AddressFamilyInterNetwork,
                    tableClass,
                    reserved: 0);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != ErrorSuccess)
                {
                    throw new Win32Exception((int)result);
                }

                var rowCount = Marshal.ReadInt32(buffer);
                var rowPointer = IntPtr.Add(buffer, sizeof(uint));
                var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
                for (var index = 0; index < rowCount; index++)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(rowPointer);
                    if (predicate(row))
                    {
                        return true;
                    }

                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception((int)ErrorInsufficientBuffer);
    }

    private const uint TcpStateEstablished = 5;

    private static uint EncodeAddress(IPAddress address) =>
        BitConverter.ToUInt32(address.GetAddressBytes());

    private static ushort DecodePort(uint port) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)port));

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidListener = 3,
        OwnerPidAll = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly int OwningProcessId;
    }
}
