using Backbloom.Engine.Platform;
using Backbloom.Platform.Windows;

namespace Backbloom.Engine.Tests;

/// <summary>The monitor sizes of <c>hardware screens</c>: EDID parsing and the Windows list (read-only).</summary>
public sealed class DisplayListTests
{
    private static byte[] Block(int widthCm, int heightCm, int? dtdWidthMm = null, int? dtdHeightMm = null)
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[21] = (byte)widthCm;
        edid[22] = (byte)heightCm;
        if (dtdWidthMm is { } w && dtdHeightMm is { } h)
        {
            edid[54] = 0x02;
            edid[55] = 0x3A;
            edid[66] = (byte)(w & 0xFF);
            edid[67] = (byte)(h & 0xFF);
            edid[68] = (byte)(((w >> 8) << 4) | (h >> 8));
        }

        return edid;
    }

    [Fact]
    public void Edid_PrefersTheDetailedTimingSizeInMillimeters()
    {
        Assert.Equal((597, 336), Edid.PhysicalSize(Block(60, 34, 597, 336)));
        Assert.Equal((1872, 1053), Edid.PhysicalSize(Block(187, 105, 1872, 1053)));
    }

    [Fact]
    public void Edid_FallsBackToCentimetersAndRejectsNonsense()
    {
        Assert.Equal((600, 340), Edid.PhysicalSize(Block(60, 34)));

        // an aspect ratio (16 x 9) where the size belongs: the basic size is used
        Assert.Equal((600, 340), Edid.PhysicalSize(Block(60, 34, 16, 9)));

        // a projector reports no size
        Assert.Equal((null, null), Edid.PhysicalSize(Block(0, 0)));
        Assert.Equal((null, null), Edid.PhysicalSize(new byte[64]));
        var broken = Block(60, 34);
        broken[0] = 1;
        Assert.Equal((null, null), Edid.PhysicalSize(broken));
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#DEL41A4#5&1b6b3b4&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"DEL41A4\5&1b6b3b4&0&UID4353")]
    [InlineData("edid:10AC:41A4:0", null)]
    [InlineData("", null)]
    public void Windows_RegistryKeyOfAMonitor(string devicePath, string? key)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(key, WindowsDisplayList.RegistryKeyOf(devicePath));
        }
    }

    [Fact]
    public void Windows_APortraitMonitorSwapsWidthAndHeight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            return;
        }

        Assert.Equal((336, 597), WindowsDisplayList.Oriented(597, 336, TerraFX.Interop.Windows.DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90));
        Assert.Equal((336, 597), WindowsDisplayList.Oriented(597, 336, TerraFX.Interop.Windows.DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE270));
        Assert.Equal((597, 336), WindowsDisplayList.Oriented(597, 336, TerraFX.Interop.Windows.DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE180));
        Assert.Equal((597, 336), WindowsDisplayList.Oriented(597, 336, TerraFX.Interop.Windows.DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY));
        Assert.Equal((null, 597), WindowsDisplayList.Oriented(597, null, TerraFX.Interop.Windows.DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90));
    }

    /// <summary>The temporary Wi-Fi profile's XML only (no Wi-Fi call: the tests never touch this PC's Wi-Fi).</summary>
    [Fact]
    public void Windows_TemporaryWifiProfiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var secure = WindowsWifi.ProfileXml("Backbloom setup 1a2b3c4d", "WLED-AP", "wled<1&2>");
        Assert.Contains("<name>Backbloom setup 1a2b3c4d</name>", secure, StringComparison.Ordinal);
        Assert.Contains("<hex>574C45442D4150</hex><name>WLED-AP</name>", secure, StringComparison.Ordinal);
        Assert.Contains("<connectionMode>manual</connectionMode>", secure, StringComparison.Ordinal);
        Assert.Contains("<authentication>WPA2PSK</authentication><encryption>AES</encryption>", secure, StringComparison.Ordinal);
        Assert.Contains("<keyMaterial>wled&lt;1&amp;2&gt;</keyMaterial>", secure, StringComparison.Ordinal);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(secure).Root);

        var open = WindowsWifi.ProfileXml("Backbloom setup 1a2b3c4d", "Café & Co", null);
        Assert.Contains("<authentication>open</authentication><encryption>none</encryption>", open, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedKey", open, StringComparison.Ordinal);
        Assert.Contains("<name>Café &amp; Co</name>", open, StringComparison.Ordinal);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(open).Root);
    }

    [Fact]
    public void Windows_TheKeyOfASavedProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string Profile = """
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>Home</name>
              <MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption></authEncryption>
              <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>p&amp;ss</keyMaterial></sharedKey></security></MSM></WLANProfile>
            """;
        Assert.Equal("p&ss", WindowsWifi.KeyOf(Profile));
        Assert.Null(WindowsWifi.KeyOf(Profile.Replace("<protected>false</protected>", "<protected>true</protected>", StringComparison.Ordinal)));
        Assert.Null(WindowsWifi.KeyOf(WindowsWifi.ProfileXml("Open", "Cafe", null)));
        Assert.Null(WindowsWifi.KeyOf("not xml"));
    }

    [Fact]
    public void Windows_ListsTheMonitorsWithoutFailing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // a build machine may have no monitor at all; every one listed has a name and plausible sizes
        foreach (var display in new WindowsDisplayList().GetDisplays())
        {
            Assert.NotEmpty(display.Name);
            Assert.True(display.WidthMm is null or > 0);
            Assert.True(display.HeightMm is null or > 0);
        }

        Assert.Empty(new NoDisplayList().GetDisplays());
    }
}
