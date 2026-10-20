using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.CLSCTX;
using static TerraFX.Interop.Windows.COINIT;
using static TerraFX.Interop.Windows.EDataFlow;
using static TerraFX.Interop.Windows.ERole;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Audio.Windows;

/// <summary>A failed Core Audio call; <see cref="Exception.HResult"/> holds the HRESULT.</summary>
internal sealed class AudioException : Exception
{
    public AudioException(string message)
        : base(message)
    {
    }

    public AudioException(string operation, int hresult)
        : base($"{operation} failed (0x{hresult:X8})")
    {
        HResult = hresult;
    }
}

/// <summary>
/// The Core Audio endpoints (MMDevice API): listing them with stable ids and resolving a <c>soundEffect.device</c>
/// value to one. Ids are <c>loopback:&lt;endpoint id&gt;</c> for output devices (captured in loopback mode) and
/// <c>input:&lt;endpoint id&gt;</c> for recording devices; the endpoint ids survive restarts and renaming.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WasapiDevices
{
    public const string LoopbackPrefix = "loopback:";
    public const string InputPrefix = "input:";

    // PKEY_Device_FriendlyName (functiondiscoverykeys_devpkey.h)
    private static readonly PROPERTYKEY s_friendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    /// <summary>Joins the multithreaded apartment for the calling thread (balanced by <see cref="LeaveCom"/>).</summary>
    public static bool EnterCom()
    {
        var hr = CoInitializeEx(null, (uint)COINIT_MULTITHREADED);
        return hr.SUCCEEDED;
    }

    public static void LeaveCom(bool entered)
    {
        if (entered)
        {
            CoUninitialize();
        }
    }

    public static IMMDeviceEnumerator* CreateEnumerator()
    {
        IMMDeviceEnumerator* enumerator = null;
        var clsid = CLSID.CLSID_MMDeviceEnumerator;
        var iid = IID.IID_IMMDeviceEnumerator;
        Check(CoCreateInstance(&clsid, null, (uint)CLSCTX_ALL, &iid, (void**)&enumerator), "CoCreateInstance(MMDeviceEnumerator)");
        return enumerator;
    }

    public static List<AudioDevice> List()
    {
        var entered = EnterCom();
        IMMDeviceEnumerator* enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            var devices = new List<AudioDevice> { new(SoundSettings.DefaultDevice, "Default output (what you hear)", AudioDeviceKind.Loopback) };
            AddEndpoints(enumerator, eRender, devices);
            AddEndpoints(enumerator, eCapture, devices);
            return devices;
        }
        finally
        {
            Release(ref enumerator);
            LeaveCom(entered);
        }
    }

    /// <summary>
    /// The endpoint for a setting value: <see cref="SoundSettings.DefaultDevice"/> (the default output, loopback), an
    /// id from <see cref="List"/>, or a HyperHDR device name (a waveIn name: a recording device's name, cut to 31
    /// characters).
    /// </summary>
    /// <returns>The device (the caller releases it); throws when there is none.</returns>
    public static IMMDevice* Resolve(IMMDeviceEnumerator* enumerator, string deviceId, out bool loopback, out string name)
    {
        IMMDevice* device = null;
        if (deviceId == SoundSettings.DefaultDevice)
        {
            Check(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device), "GetDefaultAudioEndpoint");
            loopback = true;
        }
        else if (deviceId.StartsWith(LoopbackPrefix, StringComparison.Ordinal) || deviceId.StartsWith(InputPrefix, StringComparison.Ordinal))
        {
            loopback = deviceId.StartsWith(LoopbackPrefix, StringComparison.Ordinal);
            var endpoint = deviceId[(loopback ? LoopbackPrefix : InputPrefix).Length..];
            fixed (char* id = endpoint)
            {
                Check(enumerator->GetDevice(id, &device), "GetDevice");
            }
        }
        else
        {
            device = FindByName(enumerator, eCapture, deviceId);
            if (device is null)
            {
                throw new AudioException($"There is no recording device named '{deviceId}'");
            }

            loopback = false;
        }

        name = FriendlyName(device) ?? deviceId;
        return device;
    }

    /// <summary>True when <paramref name="endpointId"/> is still the default output device (allocation-free).</summary>
    public static bool IsDefaultOutput(IMMDeviceEnumerator* enumerator, string endpointId)
    {
        IMMDevice* device = null;
        char* id = null;
        try
        {
            if (enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device).FAILED || device->GetId(&id).FAILED)
            {
                return false;
            }

            return System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(id).SequenceEqual(endpointId);
        }
        finally
        {
            CoTaskMemFree(id);
            Release(ref device);
        }
    }

    public static string? EndpointId(IMMDevice* device)
    {
        char* id = null;
        try
        {
            return device->GetId(&id).SUCCEEDED ? new string(id) : null;
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    public static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            throw new AudioException(operation, hr);
        }
    }

    public static void Release<T>(ref T* pointer)
        where T : unmanaged, IUnknown.Interface
    {
        var p = pointer;
        pointer = null;
        if (p is not null)
        {
            p->Release();
        }
    }

    private static void AddEndpoints(IMMDeviceEnumerator* enumerator, EDataFlow flow, List<AudioDevice> devices)
    {
        IMMDeviceCollection* collection = null;
        try
        {
            Check(enumerator->EnumAudioEndpoints(flow, DEVICE.DEVICE_STATE_ACTIVE, &collection), "EnumAudioEndpoints");
            uint count;
            Check(collection->GetCount(&count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                try
                {
                    if (collection->Item(i, &device).FAILED || EndpointId(device) is not { } id)
                    {
                        continue;
                    }

                    var name = FriendlyName(device) ?? id;
                    devices.Add(flow == eRender
                        ? new AudioDevice(LoopbackPrefix + id, $"{name} (what you hear)", AudioDeviceKind.Loopback)
                        : new AudioDevice(InputPrefix + id, name, AudioDeviceKind.Microphone));
                }
                finally
                {
                    Release(ref device);
                }
            }
        }
        finally
        {
            Release(ref collection);
        }
    }

    private static IMMDevice* FindByName(IMMDeviceEnumerator* enumerator, EDataFlow flow, string name)
    {
        IMMDeviceCollection* collection = null;
        try
        {
            Check(enumerator->EnumAudioEndpoints(flow, DEVICE.DEVICE_STATE_ACTIVE, &collection), "EnumAudioEndpoints");
            uint count;
            Check(collection->GetCount(&count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                if (collection->Item(i, &device).FAILED)
                {
                    continue;
                }

                var friendly = FriendlyName(device);
                if (friendly is not null && (friendly == name || (name.Length >= 31 && friendly.StartsWith(name, StringComparison.Ordinal))))
                {
                    return device;
                }

                Release(ref device);
            }

            return null;
        }
        finally
        {
            Release(ref collection);
        }
    }

    private static string? FriendlyName(IMMDevice* device)
    {
        IPropertyStore* store = null;
        PROPVARIANT value = default;
        try
        {
            if (device->OpenPropertyStore(STGM.STGM_READ, &store).FAILED)
            {
                return null;
            }

            var key = s_friendlyName;
            if (store->GetValue(&key, &value).FAILED || value.vt != (ushort)VARENUM.VT_LPWSTR || value.pwszVal is null)
            {
                return null;
            }

            return new string(value.pwszVal);
        }
        finally
        {
            _ = PropVariantClear(&value);
            Release(ref store);
        }
    }
}
