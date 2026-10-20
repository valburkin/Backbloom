using Overglow.Devices.Drivers.ArtNet;
using Overglow.Devices.Drivers.Ddp;
using Overglow.Devices.Drivers.E131;
using Overglow.Devices.Drivers.File;
using Overglow.Devices.Drivers.Hid;
using Overglow.Devices.Drivers.HomeAssistant;
using Overglow.Devices.Drivers.Lifx;
using Overglow.Devices.Drivers.Nanoleaf;
using Overglow.Devices.Drivers.PhilipsHue;
using Overglow.Devices.Drivers.Serial;
using Overglow.Devices.Drivers.Wiz;
using Overglow.Devices.Drivers.Wled;
using Overglow.Devices.Drivers.Yeelight;
using Overglow.Devices.Drivers.Zigbee2Mqtt;

namespace Overglow.Devices;

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
