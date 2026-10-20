namespace Overglow.Discovery;

/// <summary>mDNS service types (port of <c>DiscoveryRecord::getmDnsHeader</c>, sources/bonjour/DiscoveryRecord.cpp).</summary>
public static class KnownServices
{
    public const string Wled = "_wled._tcp.local.";
    public const string PhilipsHue = "_hue._tcp.local.";
    public const string HomeAssistant = "_home-assistant._tcp.local.";

    /// <summary>The web UI of this application (advertised, never browsed).</summary>
    public const string WebUi = "_overglow-http._tcp.local.";
}
