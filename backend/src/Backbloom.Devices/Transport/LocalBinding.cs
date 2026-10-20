using System.Net;
using System.Net.Sockets;

namespace Backbloom.Devices.Transport;

/// <summary>
/// A UDP socket connected to a controller on this computer (simulators, test fakes) is bound to loopback first:
/// otherwise Windows binds it to every interface, and the program counts as listening on the network (firewall
/// prompt). Sockets for controllers on the network keep the implicit binding.
/// </summary>
internal static class LocalBinding
{
    public static void BindIfLoopback(Socket socket, IPEndPoint remote)
    {
        if (IPAddress.IsLoopback(remote.Address))
        {
            socket.Bind(new IPEndPoint(remote.Address, 0));
        }
    }
}
