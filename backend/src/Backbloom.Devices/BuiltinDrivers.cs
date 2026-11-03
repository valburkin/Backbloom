using Backbloom.Devices.Drivers.ArtNet;
using Backbloom.Devices.Drivers.Ddp;
using Backbloom.Devices.Drivers.E131;
using Backbloom.Devices.Drivers.File;
using Backbloom.Devices.Drivers.Govee;
using Backbloom.Devices.Drivers.Hid;
using Backbloom.Devices.Drivers.HomeAssistant;
using Backbloom.Devices.Drivers.Lifx;
using Backbloom.Devices.Drivers.Nanoleaf;
using Backbloom.Devices.Drivers.PhilipsHue;
using Backbloom.Devices.Drivers.Serial;
using Backbloom.Devices.Drivers.Wiz;
using Backbloom.Devices.Drivers.Wled;
using Backbloom.Devices.Drivers.Yeelight;
using Backbloom.Devices.Drivers.Zigbee2Mqtt;

namespace Backbloom.Devices;

public static class BuiltinDrivers
{
    /// <summary>Registers every driver of this assembly.</summary>
    public static DriverRegistry RegisterAll(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DdpDriver.Descriptor);
        registry.Register(E131Driver.Descriptor);
        registry.Register(ArtNetDriver.Descriptor);
        registry.Register(WledDriver.Descriptor);
        registry.Register(PhilipsHueDriver.Descriptor);
        registry.Register(NanoleafDriver.Descriptor);
        registry.Register(LifxDriver.Descriptor);
        registry.Register(WizDriver.Descriptor);
        registry.Register(GoveeDriver.Descriptor);
        registry.Register(YeelightDriver.Descriptor);
        registry.Register(HomeAssistantDriver.Descriptor);
        registry.Register(Zigbee2MqttDriver.Descriptor);
        registry.Register(AdalightDriver.Descriptor);
        registry.Register(SkydimoDriver.Descriptor);
        registry.Register(Sp616eDriver.Descriptor);
        registry.Register(SyncLightDriver.Descriptor);
        registry.Register(FileDriver.Descriptor);
        return registry;
    }
}
