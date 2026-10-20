using Backbloom.Discovery.Mdns;
using Backbloom.Discovery.Ssdp;
using Backbloom.Discovery.Usb;

namespace Backbloom.Discovery;

/// <summary>What the drivers use to find their controllers; built once by the composition root.</summary>
public sealed record DiscoveryServices(IMdnsBrowser Mdns, ISsdpSearcher Ssdp, ISerialPortEnumerator SerialPorts, IHidEnumerator HidDevices, IUdpProbe Broadcast);
