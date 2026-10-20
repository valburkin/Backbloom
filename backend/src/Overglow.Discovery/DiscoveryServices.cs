using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;

namespace Overglow.Discovery;

/// <summary>What the drivers use to find their controllers; built once by the composition root.</summary>
public sealed record DiscoveryServices(IMdnsBrowser Mdns, ISsdpSearcher Ssdp, ISerialPortEnumerator SerialPorts, IHidEnumerator HidDevices, IUdpProbe Broadcast);
