using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
        info["effects"] = new JsonArray();
        info["activeEffects"] = new JsonArray();
        info["activeLedColor"] = ActiveLedColor();
        info["components"] = Components();
        info["leds"] = settings.GetSection("leds") ?? new JsonArray();
        info["imageToLedMappingType"] = engine.MappingType.ToSettingString();

        var available = new JsonArray();
        foreach (var d in drivers.Drivers.OrderBy(d => d.Group, StringComparer.Ordinal).ThenBy(d => d.Type, StringComparer.Ordinal))
        {
            available.Add((JsonNode)new JsonObject { ["name"] = d.Type, ["group"] = d.Group });
        }

        info["ledDevices"] = new JsonObject { ["available"] = available };
        info["systemGrabbers"] = new JsonObject { ["device"] = engine.SourceName, ["modes"] = new JsonArray(engine.SourceDevice) };
        info["sessions"] = new JsonArray();
        info["instance"] = Instances();
        info["currentInstance"] = 0;
        info["hostname"] = Dns.GetHostName();
        info["lastError"] = string.Empty;
        return info;
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
