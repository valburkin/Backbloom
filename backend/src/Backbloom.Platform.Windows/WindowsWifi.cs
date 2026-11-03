using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Backbloom.Devices.Setup;

namespace Backbloom.Platform.Windows;

/// <summary>
/// <see cref="IWifi"/> over the Native Wifi API (wlanapi.dll): the current connection (<c>WlanQueryInterface</c>), a
/// scan (<c>WlanScan</c>, then <c>WlanGetAvailableNetworkList</c>), a visit to a network with a temporary per-user
/// profile (<c>WlanSetProfile</c> under a name of its own, never overwriting one, then <c>WlanConnect</c>) and the way
/// back (<c>WlanConnect</c> with the previous profile, then <c>WlanDeleteProfile</c> of the temporary profiles only).
/// The user's own profiles are never changed or deleted. Nothing is called until a method is: constructing it touches
/// no Wi-Fi.
/// </summary>
/// <remarks>
/// Windows 11 24H2 and newer refuse the scan, the network list and the current connection with ERROR_ACCESS_DENIED to
/// apps without location access, and show a one-time location prompt per app the first time one is refused; that is
/// reported as <see cref="WifiBlock.Location"/>. Joining and leaving (profiles, connect) aren't affected; while the
/// current connection is hidden the way back relies on Windows reconnecting by itself once the temporary profile is
/// deleted.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsWifi : IWifi
{
    private const uint ClientVersion = 2;
    private const uint Success = 0;
    private const uint AccessDenied = 5;
    private const uint UserProfile = 0x2;
    private const uint PlaintextKey = 0x4;
    private const int OpcodeInterfaceState = 6;
    private const int OpcodeCurrentConnection = 7;
    private const int StateConnected = 1;
    private const int ConnectionModeProfile = 0;
    private const int BssInfrastructure = 1;
    private const int InterfaceInfoSize = 532;
    private const int AvailableNetworkSize = 628;
    private const string ProfilePrefix = "Backbloom setup ";

    private static readonly TimeSpan s_scanWait = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly List<(Guid Interface, string Profile)> _temporary = [];

    public unsafe WifiState GetState()
    {
        using var client = Client.Open();
        if (client is null)
        {
            return WifiState.None;
        }

        var interfaces = Interfaces(client.Handle);
        if (interfaces.Count == 0)
        {
            return WifiState.None;
        }

        var blocked = WifiBlock.None;
        foreach (var id in interfaces)
        {
            var (result, connected, profile, ssid) = CurrentConnection(client.Handle, id);
            if (result == AccessDenied)
            {
                blocked = WifiBlock.Location;
                continue;
            }

            if (result == Success && connected)
            {
                return new WifiState(true, ssid, profile, id.ToString(), WifiBlock.None);
            }
        }

        return new WifiState(true, null, null, interfaces[0].ToString(), blocked);
    }

    public async Task<WifiScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        var (interfaces, blocked) = StartScan();
        if (interfaces is null)
        {
            return new WifiScanResult([], blocked);
        }

        // the scan reports its end by notification; the list is complete after a few seconds
        await Task.Delay(s_scanWait, cancellationToken);
        return ReadNetworks(interfaces);
    }

    private static unsafe (List<Guid>? Interfaces, WifiBlock Blocked) StartScan()
    {
        using var client = Client.Open();
        if (client is null)
        {
            return (null, WifiBlock.None);
        }

        var interfaces = Interfaces(client.Handle);
        foreach (var id in interfaces)
        {
            var guid = id;
            if (WlanScan(client.Handle, &guid, null, null, null) == AccessDenied)
            {
                return (null, WifiBlock.Location);
            }
        }

        return (interfaces, WifiBlock.None);
    }

    private static unsafe WifiScanResult ReadNetworks(List<Guid> interfaces)
    {
        using var reader = Client.Open();
        if (reader is null)
        {
            return new WifiScanResult([], WifiBlock.None);
        }

        var found = new Dictionary<string, WifiNetworkInfo>(StringComparer.Ordinal);
        foreach (var id in interfaces)
        {
            var guid = id;
            void* list = null;
            var result = WlanGetAvailableNetworkList(reader.Handle, &guid, 0, null, &list);
            if (result == AccessDenied)
            {
                return new WifiScanResult([], WifiBlock.Location);
            }

            if (result != Success || list is null)
            {
                continue;
            }

            try
            {
                var count = *(uint*)list;
                for (var i = 0; i < count; i++)
                {
                    var item = (byte*)list + 8 + (i * AvailableNetworkSize);
                    var ssid = Ssid(item + 512);
                    if (ssid.Length == 0)
                    {
                        continue;
                    }

                    var signal = (int)Math.Clamp(*(uint*)(item + 604), 0, 100);
                    var secure = *(int*)(item + 608) != 0;
                    if (!found.TryGetValue(ssid, out var known) || known.SignalPercent < signal)
                    {
                        found[ssid] = new WifiNetworkInfo(ssid, signal, secure || (known?.Secure ?? false));
                    }
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }

        return new WifiScanResult([.. found.Values.OrderByDescending(n => n.SignalPercent)], WifiBlock.None);
    }
    public async Task<bool> JoinTemporaryAsync(string ssid, string? password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ssid);
        var name = ProfilePrefix + Guid.NewGuid().ToString("N")[..8];
        if (CreateAndConnect(name, ssid, password) is not { } id)
        {
            return false;
        }

        // the old connection may still read as connected for a moment: wait for the new one's profile when it can be
        // read, else (location access off) for the interface to be connected again
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(s_poll, cancellationToken);
            var (result, connected, profile) = Connection(id);
            if (result == Success && connected && profile == name)
            {
                return true;
            }

            if (result == AccessDenied && DateTime.UtcNow - start > TimeSpan.FromSeconds(2) && InterfaceConnected(id))
            {
                return true;
            }
        }

        return false;
    }

    // A temporary profile under a new name (never overwriting one: no profile of the user is touched), then connect.
    private unsafe Guid? CreateAndConnect(string name, string ssid, string? password)
    {
        using var client = Client.Open();
        if (client is null || Interfaces(client.Handle) is not [var first, ..])
        {
            return null;
        }

        var id = first;
        var xml = ProfileXml(name, ssid, password);
        uint reason;
        uint set;
        fixed (char* x = xml)
        {
            set = WlanSetProfile(client.Handle, &id, UserProfile, x, null, 0, null, &reason);
        }

        if (set != Success)
        {
            return null;
        }

        lock (_lock)
        {
            _temporary.Add((id, name));
        }

        return Connect(client.Handle, id, name) ? id : null;
    }

    private static (uint Result, bool Connected, string? Profile) Connection(Guid id)
    {
        using var client = Client.Open();
        if (client is null)
        {
            return (AccessDenied + 1, false, null);
        }

        var (result, connected, profile, _) = CurrentConnection(client.Handle, id);
        return (result, connected, profile);
    }

    private static bool InterfaceConnected(Guid id)
    {
        using var client = Client.Open();
        return client is not null && InterfaceConnected(client.Handle, id);
    }
    public async Task RestoreAsync(WifiState previous, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        try
        {
            if (previous.Profile is { } profile && Guid.TryParse(previous.InterfaceId, out var id))
            {
                ConnectProfile(id, profile);
                var start = DateTime.UtcNow;
                while (DateTime.UtcNow - start < timeout)
                {
                    await Task.Delay(s_poll, CancellationToken.None);
                    if (Connection(id) is (Success, true, var current) && current == profile)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the temporary profiles go anyway; Windows then connects to the user's networks by itself
        }
        finally
        {
            DeleteTemporaryProfiles();
        }
    }

    private static void ConnectProfile(Guid id, string profile)
    {
        using var client = Client.Open();
        if (client is not null)
        {
            Connect(client.Handle, id, profile);
        }
    }
    /// <summary>
    /// The key of the current connection's profile (<c>WlanGetProfile</c> with WLAN_PROFILE_GET_PLAINTEXT_KEY, as
    /// <c>netsh wlan show profile key=clear</c> shows it to the user without elevation); null when there is none or it
    /// comes back encrypted.
    /// </summary>
    public unsafe string? ReadSavedKey(WifiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Profile is not { } profile || !Guid.TryParse(state.InterfaceId, out var id))
        {
            return null;
        }

        using var client = Client.Open();
        if (client is null)
        {
            return null;
        }

        char* xml = null;
        var flags = PlaintextKey;
        uint access;
        uint result;
        fixed (char* name = profile)
        {
            result = WlanGetProfile(client.Handle, &id, name, null, &xml, &flags, &access);
        }

        if (result != Success || xml is null)
        {
            return null;
        }

        try
        {
            return KeyOf(new string(xml));
        }
        finally
        {
            WlanFreeMemory(xml);
        }
    }

    /// <summary>The plain key of a profile's XML (internal for the tests); null when it has none or it is protected.</summary>
    internal static string? KeyOf(string profileXml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(profileXml);
            var shared = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sharedKey");
            var isProtected = shared?.Elements().FirstOrDefault(e => e.Name.LocalName == "protected")?.Value;
            var key = shared?.Elements().FirstOrDefault(e => e.Name.LocalName == "keyMaterial")?.Value;
            return string.Equals(isProtected, "false", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(key) ? key : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The temporary profile for a network (internal for the tests): WPA2-PSK with a password, else open; manual, so Windows never connects to it by itself.</summary>
    internal static string ProfileXml(string name, string ssid, string? password)
    {
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        var security = string.IsNullOrEmpty(password)
            ? "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>"
            : "<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
              $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>";
        return "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
            $"<name>{SecurityElement.Escape(name)}</name><SSIDConfig><SSID><hex>{hex}</hex><name>{SecurityElement.Escape(ssid)}</name></SSID></SSIDConfig>" +
            $"<connectionType>ESS</connectionType><connectionMode>manual</connectionMode><MSM><security>{security}</security></MSM></WLANProfile>";
    }

    private unsafe void DeleteTemporaryProfiles()
    {
        List<(Guid Interface, string Profile)> created;
        lock (_lock)
        {
            created = [.. _temporary];
            _temporary.Clear();
        }

        using var client = Client.Open();
        if (client is null)
        {
            return;
        }

        foreach (var (id, profile) in created)
        {
            var guid = id;
            fixed (char* p = profile)
            {
                _ = WlanDeleteProfile(client.Handle, &guid, p, null);
            }
        }
    }

    private static unsafe bool Connect(nint handle, Guid id, string profile)
    {
        fixed (char* p = profile)
        {
            var parameters = new ConnectionParameters { Mode = ConnectionModeProfile, Profile = p, BssType = BssInfrastructure };
            return WlanConnect(handle, &id, &parameters, null) == Success;
        }
    }

    private static unsafe List<Guid> Interfaces(nint handle)
    {
        var result = new List<Guid>();
        void* list = null;
        if (WlanEnumInterfaces(handle, null, &list) != Success || list is null)
        {
            return result;
        }

        try
        {
            var count = *(uint*)list;
            for (var i = 0; i < count; i++)
            {
                result.Add(*(Guid*)((byte*)list + 8 + (i * InterfaceInfoSize)));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result;
    }

    private static unsafe (uint Result, bool Connected, string? Profile, string? Ssid) CurrentConnection(nint handle, Guid id)
    {
        uint size;
        void* data = null;
        int type;
        var result = WlanQueryInterface(handle, &id, OpcodeCurrentConnection, null, &size, &data, &type);
        if (result != Success || data is null)
        {
            return (result, false, null, null);
        }

        try
        {
            var bytes = (byte*)data;
            var connected = *(int*)bytes == StateConnected;
            var name = new ReadOnlySpan<char>(bytes + 8, 256);
            var end = name.IndexOf('\0');
            var profile = (end < 0 ? name : name[..end]).ToString();
            return (Success, connected, profile.Length > 0 ? profile : null, Ssid(bytes + 520) is { Length: > 0 } s ? s : null);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static unsafe bool InterfaceConnected(nint handle, Guid id)
    {
        uint size;
        void* data = null;
        int type;
        if (WlanQueryInterface(handle, &id, OpcodeInterfaceState, null, &size, &data, &type) != Success || data is null)
        {
            return false;
        }

        try
        {
            return *(int*)data == StateConnected;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    // DOT11_SSID: a length, then up to 32 bytes (UTF-8 in practice)
    private static unsafe string Ssid(byte* dot11Ssid)
    {
        var length = (int)Math.Min(*(uint*)dot11Ssid, 32);
        return Encoding.UTF8.GetString(dot11Ssid + 4, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ConnectionParameters
    {
        public int Mode;
        public char* Profile;
        public void* Ssid;
        public void* BssidList;
        public int BssType;
        public uint Flags;
    }

    /// <summary>A WLAN client handle, closed on dispose; null when the WLAN service doesn't run (no Wi-Fi adapter).</summary>
    private sealed class Client : IDisposable
    {
        private Client(nint handle) => Handle = handle;

        public nint Handle { get; }

        public static unsafe Client? Open()
        {
            uint version;
            nint handle;
            return WlanOpenHandle(ClientVersion, null, &version, &handle) == Success ? new Client(handle) : null;
        }

        public unsafe void Dispose() => _ = WlanCloseHandle(Handle, null);
    }

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanOpenHandle(uint clientVersion, void* reserved, uint* negotiatedVersion, nint* clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanCloseHandle(nint clientHandle, void* reserved);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanEnumInterfaces(nint clientHandle, void* reserved, void** interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanScan(nint clientHandle, Guid* interfaceGuid, void* ssid, void* ieData, void* reserved);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanGetAvailableNetworkList(nint clientHandle, Guid* interfaceGuid, uint flags, void* reserved, void** networkList);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanQueryInterface(nint clientHandle, Guid* interfaceGuid, int opCode, void* reserved, uint* dataSize, void** data, int* opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanSetProfile(nint clientHandle, Guid* interfaceGuid, uint flags, char* profileXml, char* allUserProfileSecurity, int overwrite, void* reserved, uint* reasonCode);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanConnect(nint clientHandle, Guid* interfaceGuid, ConnectionParameters* parameters, void* reserved);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanGetProfile(nint clientHandle, Guid* interfaceGuid, char* profileName, void* reserved, char** profileXml, uint* flags, uint* grantedAccess);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanDeleteProfile(nint clientHandle, Guid* interfaceGuid, char* profileName, void* reserved);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial void WlanFreeMemory(void* memory);
}
