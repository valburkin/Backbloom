using Overglow.Devices.Drivers.Ddp;
using Overglow.Devices.Drivers.File;
using Overglow.Devices.Drivers.Wled;

namespace Overglow.Devices;

public static class BuiltinDrivers
{
    /// <summary>Registers every driver of this assembly.</summary>
    public static DriverRegistry RegisterAll(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DdpDriver.Descriptor);
        registry.Register(WledDriver.Descriptor);
        registry.Register(FileDriver.Descriptor);
        return registry;
    }
}
