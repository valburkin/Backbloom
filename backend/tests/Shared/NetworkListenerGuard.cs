using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Backbloom.Tests.Network;

[assembly: AssemblyFixture(typeof(NetworkListenerGuard))]

namespace Backbloom.Tests.Network;

/// <summary>
/// Fails the test run when the test process listens on anything but loopback: a TCP listener or a UDP socket bound to
/// a network address (or to all of them) makes Windows Firewall ask the user to allow the test host, once per test
/// executable path. Every fake server, device and test host must bind to 127.0.0.1 (or ::1).
/// </summary>
/// <remarks>
/// Linked into every test project (tests/Directory.Build.props) as an assembly fixture: it samples the process's TCP
/// listeners and UDP sockets (iphlpapi owner-PID tables) every 20 ms while the tests run and reports every
/// non-loopback endpoint when the assembly finishes. Windows only.
/// </remarks>
public sealed partial class NetworkListenerGuard : IAsyncDisposable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;

    private readonly HashSet<string> _violations = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _sampler;
    private readonly uint _pid = (uint)Environment.ProcessId;
    private byte[] _buffer = new byte[64 * 1024];

    public NetworkListenerGuard()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _sampler = new Thread(Sample) { IsBackground = true, Name = "Network listener guard" };
        _sampler.Start();
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _sampler?.Join();
        _stop.Dispose();
        if (_violations.Count > 0)
        {
            throw new InvalidOperationException(
                "The tests listened on non-loopback addresses (Windows Firewall prompts the user for this); bind fakes and hosts to 127.0.0.1: " +
                string.Join(", ", _violations.Order(StringComparer.Ordinal)));
        }

        return ValueTask.CompletedTask;
    }

    private void Sample()
    {
        while (!_stop.IsCancellationRequested)
        {
            Collect("TCP", AfInet, isTcp: true, rowSize: 24, addressOffset: 4, portOffset: 8, pidOffset: 20, addressLength: 4);
            Collect("TCP", AfInet6, isTcp: true, rowSize: 56, addressOffset: 0, portOffset: 20, pidOffset: 52, addressLength: 16);
            Collect("UDP", AfInet, isTcp: false, rowSize: 12, addressOffset: 0, portOffset: 4, pidOffset: 8, addressLength: 4);
            Collect("UDP", AfInet6, isTcp: false, rowSize: 28, addressOffset: 0, portOffset: 20, pidOffset: 24, addressLength: 16);
            _stop.Token.WaitHandle.WaitOne(20);
        }
    }

    private void Collect(string protocol, int family, bool isTcp, int rowSize, int addressOffset, int portOffset, int pidOffset, int addressLength)
    {
        var table = _buffer;
        var ok = false;
        for (var attempt = 0; attempt < 4 && !ok; attempt++)
        {
            var size = table.Length;
            var result = isTcp
                ? GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0)
                : GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0);
            ok = result == 0;
            if (result == 122)
            {
                // ERROR_INSUFFICIENT_BUFFER: the size needed is in size
                table = _buffer = new byte[size * 2];
            }
            else if (!ok)
            {
                return;
            }
        }

        if (!ok)
        {
            return;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + (i * rowSize), rowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[pidOffset..]) != _pid)
            {
                continue;
            }

            var address = new IPAddress(row.Slice(addressOffset, addressLength));
            if (IPAddress.IsLoopback(address))
            {
                continue;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(row[portOffset..]);
            lock (_violations)
            {
                _violations.Add($"{protocol} {new IPEndPoint(address, port)}");
            }
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(byte[]? table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(byte[]? table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
