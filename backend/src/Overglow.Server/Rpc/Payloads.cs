using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Overglow.Capture.Hdr;
using Overglow.Core.Mapping;
using Overglow.Devices;
using Overglow.Engine;
using Overglow.Settings;

namespace Overglow.Server.Rpc;

/// <summary>Builds the <c>serverinfo</c> and <c>sysinfo</c> payloads and the event bodies that repeat their parts.</summary>
public sealed class Payloads(LightingEngine engine, ISettingsStore settings, DriverRegistry drivers, ServerOptions options)
{
    public JsonObject ServerInfo()
    {
        var color = settings.GetSection("color") as JsonObject;
        var info = PrioritiesInfo();
        info["adjustment"] = color?["channelAdjustment"]?.DeepClone() ?? new JsonArray();
        info["effects"] = Effects();
        info["activeEffects"] = ActiveEffects();
        info["activeLedColor"] = ActiveLedColor();
        if (Sound() is { } sound)
        {
            info["sound"] = sound;
        }

        info["components"] = Components();
        info["leds"] = settings.GetSection("leds") ?? new JsonArray();
        info["controllers"] = Controllers();
        info["imageToLedMappingType"] = engine.MappingType.ToSettingString();

        var available = new JsonArray();
        foreach (var d in drivers.Drivers.OrderBy(d => d.Group, StringComparer.Ordinal).ThenBy(d => d.Type, StringComparer.Ordinal))
        {
            available.Add((JsonNode)new JsonObject { ["name"] = d.Type, ["group"] = d.Group });
        }

        info["ledDevices"] = new JsonObject { ["available"] = available };
        info["systemGrabbers"] = SystemGrabbers();
        info["sessions"] = new JsonArray();
        info["instance"] = Instances();
        info["currentInstance"] = 0;
        info["hostname"] = Dns.GetHostName();
        info["lastError"] = string.Empty;
        return info;
    }

    /// <summary>
    /// <c>systemGrabbers</c> (also the <c>capture-update</c> event): <c>{ device, modes }</c> as in HyperHDR plus, for
    /// screen capture, <c>hdr: { mode, capturing, monitorHdr, toneMapping }</c>: the <c>hdrToneMapping</c> setting,
    /// whether a monitor is captured, whether Windows drives it in HDR mode and whether its colors are tone mapped.
    /// </summary>
    public JsonObject SystemGrabbers()
    {
        var grabbers = new JsonObject { ["device"] = engine.SourceName, ["modes"] = new JsonArray(engine.SourceDevice) };
        if (engine.CaptureHdr is { } hdr)
        {
            grabbers["hdr"] = new JsonObject
            {
                ["mode"] = hdr.Mode.ToSettingString(),
                ["capturing"] = hdr.Capturing,
                ["monitorHdr"] = hdr.MonitorHdr,
                ["toneMapping"] = hdr.ToneMapped,
            };
        }

        return grabbers;
    }

    /// <summary>The part of serverinfo that <c>priorities-update</c> repeats (LightingInstance::getJsonInfo).</summary>
    public JsonObject PrioritiesInfo()
    {
        var priorities = new JsonArray();
        foreach (var p in engine.Priorities)
        {
            var item = new JsonObject { ["priority"] = p.Priority };
            if (p.DurationMs is { } duration)
            {
                item["duration_ms"] = duration;
            }

            if (p.Owner is not null)
            {
                item["owner"] = p.Owner;
            }

            item["componentId"] = p.ComponentId;
            item["origin"] = p.Origin;
            item["active"] = p.Active;
            item["visible"] = p.Visible;
            if (p.Color is { } color)
            {
                item["value"] = new JsonObject { ["RGB"] = color.ToJson(), ["HSL"] = Hsl(color) };
            }

            priorities.Add((JsonNode)item);
        }

        return new JsonObject { ["priorities"] = priorities, ["priorities_autoselect"] = engine.Sources.AutoSelect };
    }

    /// <summary>
    /// <c>controllers</c> (an addition, also the <c>controllers-update</c> event): every LED controller in LED order,
    /// <c>[{ id, name, type, enabled, running, state, status, start, ledCount, strips: [{ id, name, start, length, brightness }] }]</c>.
    /// <c>start</c> is the controller's first LED in <c>leds</c> (and in the live LED stream); a strip's <c>start</c>
    /// counts from the controller's first LED. <c>state</c>: off, opening, ready, waitingToRetry, failed, closing.
    /// </summary>
    public JsonArray Controllers()
    {
        var setup = engine.Setup;
        var devices = engine.Devices;
        var list = new JsonArray();
        foreach (var controller in setup.Controllers)
        {
            var device = devices.FirstOrDefault(d => d.Id == controller.Id);
            var strips = new JsonArray();
            foreach (var strip in controller.Strips)
            {
                strips.Add((JsonNode)new JsonObject
                {
                    ["id"] = strip.Id,
                    ["name"] = strip.Name,
                    ["start"] = strip.Start,
                    ["length"] = strip.Length,
                    ["brightness"] = strip.Brightness,
                });
            }

            list.Add((JsonNode)new JsonObject
            {
                ["id"] = controller.Id,
                ["name"] = controller.Name,
                ["type"] = device?.Type ?? DeviceConfig.GetString(controller.Device, "type", string.Empty),
                ["enabled"] = controller.Enabled,
                ["running"] = device?.Running ?? false,
                ["state"] = StateId(device?.State ?? DeviceState.Off),
                ["status"] = device?.Status,
                ["start"] = device?.Start ?? 0,
                ["ledCount"] = controller.LedCount,
                ["strips"] = strips,
            });
        }

        return list;
    }

    private static string StateId(DeviceState state) => state switch
    {
        DeviceState.Opening => "opening",
        DeviceState.Ready => "ready",
        DeviceState.WaitingToRetry => "waitingToRetry",
        DeviceState.Failed => "failed",
        DeviceState.Closing => "closing",
        _ => "off",
    };

    /// <summary><c>effects</c>: every effect that can be run, <c>[{ name }]</c>.</summary>
    public static JsonArray Effects()
    {
        var list = new JsonArray();
        foreach (var effect in EffectEngine.Available)
        {
            list.Add((JsonNode)new JsonObject { ["name"] = effect.Name });
        }

        return list;
    }

    /// <summary><c>activeEffects</c>: the running effects except the background one, <c>[{ name, priority, timeout }]</c>.</summary>
    public JsonArray ActiveEffects()
    {
        var list = new JsonArray();
        foreach (var effect in engine.Effects.Active.Where(e => e.Priority != EffectEngine.BackgroundPriority))
        {
            list.Add((JsonNode)new JsonObject { ["name"] = effect.Name, ["priority"] = effect.Priority, ["timeout"] = effect.TimeoutMs });
        }

        return list;
    }

    /// <summary>
    /// <c>sound</c>: <c>{ active, device, sound_available: [id], sound_devices: [{ id, name, kind }] }</c>.
    /// HyperHDR listed device names in <c>sound_available</c>; here they are the ids the setting stores, with names for
    /// display in <c>sound_devices</c> (an addition). <c>running</c> (also an addition) is true while sound arrives.
    /// </summary>
    public JsonObject? Sound()
    {
        if (engine.Sound?.GetInfo() is not { } sound)
        {
            return null;
        }

        var ids = new JsonArray();
        var devices = new JsonArray();
        foreach (var device in sound.Devices)
        {
            ids.Add(device.Id);
            devices.Add((JsonNode)new JsonObject
            {
                ["id"] = device.Id,
                ["name"] = device.Name,
                ["kind"] = device.Kind == Audio.AudioDeviceKind.Loopback ? "loopback" : "microphone",
            });
        }

        return new JsonObject
        {
            ["active"] = sound.Active,
            ["running"] = sound.Running,
            ["device"] = sound.Device,
            ["sound_available"] = ids,
            ["sound_devices"] = devices,
        };
    }

    /// <summary>The visible static color unless it is black (as HyperHDR listed it).</summary>
    public JsonArray ActiveLedColor()
    {
        var visible = engine.Priorities.FirstOrDefault(p => p.Visible);
        if (visible?.Color is not { } color || color == Core.Color.Rgb8.Black)
        {
            return [];
        }

        return [new JsonObject { ["RGB Value"] = color.ToJson(), ["HSL Value"] = Hsl(color) }];
    }

    public JsonArray Components()
    {
        var list = new JsonArray();
        foreach (var c in Engine.Components.All)
        {
            list.Add((JsonNode)new JsonObject { ["name"] = c.ToId(), ["enabled"] = engine.Components.IsOn(c) });
        }

        return list;
    }

    /// <summary>[hue 0..359, saturation, luminance] as <c>ColorRgb::rgb2hsl</c> computes them.</summary>
    public static JsonArray Hsl(Core.Color.Rgb8 color)
    {
        var r = color.R / 255.0f;
        var g = color.G / 255.0f;
        var b = color.B / 255.0f;
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        var diff = max - min;
        var luminance = (min + max) / 2.0f;
        if (diff == 0.0f)
        {
            return [0, 0.0f, luminance];
        }

        var saturation = luminance < 0.5f ? diff / (min + max) : diff / (2.0f - min - max);
        int hue;
        if (max == r)
        {
            hue = (ushort)(360 + (60 * (g - b) / diff));
            if (hue > 359)
            {
                hue -= 360;
            }
        }
        else if (max == g)
        {
            hue = (ushort)(120 + (60 * (b - r) / diff));
        }
        else
        {
            hue = (ushort)(240 + (60 * (r - g) / diff));
        }

        return [hue, saturation, luminance];
    }

    public JsonArray Instances()
    {
        var name = settings.GetSection("general")?["name"]?.GetValue<string>() ?? "Overglow";
        return [new JsonObject { ["friendly_name"] = name, ["instance"] = 0, ["running"] = true }];
    }

    public JsonObject SysInfo()
    {
        var system = new JsonObject
        {
            ["kernelType"] = OperatingSystem.IsWindows() ? "winnt" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
            ["kernelVersion"] = Environment.OSVersion.Version.ToString(),
            ["architecture"] = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x86_64",
                Architecture.Arm64 => "arm64",
                var a => a.ToString().ToLowerInvariant(),
            },
            ["cpuModelName"] = string.Empty,
            ["wordSize"] = (IntPtr.Size * 8).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["productType"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["productVersion"] = Environment.OSVersion.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["prettyName"] = RuntimeInformation.OSDescription,
            ["hostName"] = Dns.GetHostName(),
            ["domainName"] = string.Empty,
            ["runtimeVersion"] = RuntimeInformation.FrameworkDescription,
        };
        var product = new JsonObject
        {
            ["version"] = options.Version,
            ["build"] = options.Build,
            ["supportUrl"] = string.Empty,
            ["time"] = options.BuildTime,
            ["id"] = options.InstanceId,
            ["readOnlyMode"] = false,
        };
        return new JsonObject { ["system"] = system, ["overglow"] = product };
    }
}
