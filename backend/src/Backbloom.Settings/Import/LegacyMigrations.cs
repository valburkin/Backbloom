using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backbloom.Settings.Import;

/// <summary>
/// Content upgrades of settings written by HyperHDR (or restored from a backup), applied to the settings of one
/// instance (type name -> value) before they are validated. Ported from InstanceConfig::upgradeDB,
/// InstanceConfig::removeUnsupportedFeatures and SettingsMigration (sources/base). Every rule is idempotent and
/// describes each change it makes (never with secret values).
/// </summary>
public static class LegacyMigrations
{
    /// <summary>The factory name HyperHDR gives its configuration; replaced by this product's.</summary>
    public const string LegacyFactoryName = "My HyperHDR Config";

    public const string FactoryName = "My Backbloom Config";

    /// <summary>
    /// Settings types of features this product doesn't have: protobuf/flatbuffers servers, forwarder, raw UDP server,
    /// boblight, TCP JSON server and the capture card stack (video grabber, its instance control, signal detection
    /// calibration, automatic tone mapping); plus the pre-v17 V4L2 grabber.
    /// </summary>
    public static readonly IReadOnlySet<string> RemovedSettingsTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "protoServer", "flatbufServer", "forwarder", "rawUdpServer", "boblightServer", "jsonServer",
        "videoGrabber", "videoControl", "videoDetection", "automaticToneMapping", "grabberV4L2",
    };

    /// <summary>
    /// Types HyperHDR stored once for all instances (SettingsTable::isSettingGlobal of HyperHDR 24 plus the Backbloom
    /// C++ <c>layout</c>); every other type was stored per instance.
    /// </summary>
    public static readonly IReadOnlySet<string> GlobalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "jsonServer", "protoServer", "flatbufServer", "network", "forwarder", "webConfig", "videoGrabber", "systemGrabber",
        "logger", "general", "soundEffect", "videoDetection", "mqtt", "automaticToneMapping", "layout", "grabberV4L2",
    };

    /// <summary>
    /// HyperHDR's access options in <c>network</c>, which this version doesn't have: this computer always has full
    /// access and other devices always need the password or a token (see Backbloom.Server's LocalAccess). Imports remove
    /// them; <c>setconfig</c> ignores them, so HyperHDR clients that send them back still work.
    /// </summary>
    public static readonly IReadOnlyList<string> RemovedNetworkOptions =
        ["apiAuth", "localApiAuth", "localAdminAuth", "internetAccessAPI", "restirctedInternetAccessAPI", "ipWhitelist"];

    private const string DefaultSoundDevice = "default";
    private const string RainbowSwirl = "Rainbow swirl";
    private const string FullColorMoodBlobs = "Full color mood blobs";
    private const string WarmMoodBlobs = "Warm mood blobs";
    private const string Breath = "Breath";
    private const string Candle = "Candle";
    private const string MusicPulse = "Music: fullscreen pulse (MULTI COLOR)";

    // Effects of older versions that were folded into one of the kept effects.
    private static readonly Dictionary<string, string> s_removedEffects = new(StringComparer.Ordinal)
    {
        ["Atomic swirl"] = RainbowSwirl,
        ["Double swirl"] = RainbowSwirl,
        ["Rainbow waves"] = RainbowSwirl,
        ["Plasma"] = RainbowSwirl,
        ["Sea waves"] = RainbowSwirl,
        ["Waves with Color"] = RainbowSwirl,
        ["Sparks"] = RainbowSwirl,
        ["Blue mood blobs"] = FullColorMoodBlobs,
        ["Cold mood blobs"] = FullColorMoodBlobs,
        ["Green mood blobs"] = FullColorMoodBlobs,
        ["Red mood blobs"] = WarmMoodBlobs,
        ["Cinema brighten lights"] = Candle,
        ["Cinema dim lights"] = Candle,
        ["Notify blue"] = Breath,
        ["Strobe red"] = Breath,
        ["Strobe white"] = Breath,
        ["Police Lights Single"] = Breath,
        ["Police Lights Solid"] = Breath,
        ["Knight rider"] = Breath,
        ["System Shutdown"] = Breath,
        ["Music: equalizer test (turn on video preview)"] = MusicPulse,
    };

    // The color/speed variants of the music effects were folded into the multi color one of each kind.
    private static readonly (string Prefix, string Replacement)[] s_musicEffectKinds =
    [
        ("Music: fullscreen pulse (", MusicPulse),
        ("Music: quatro for LED strip (", "Music: quatro for LED strip (MULTI COLOR)"),
        ("Music: stereo for LED strip (", "Music: stereo for LED strip (MULTI COLOR)"),
        ("Music: pulse waves for LED strip (", "Music: pulse waves for LED strip (MULTI COLOR)"),
    ];

    // Options of removed features: CEC and the signal detection of the system grabber (meant for capture cards).
    private static readonly (string Type, string Feature, string[] Fields)[] s_obsoleteFields =
    [
        ("systemControl", "CEC", ["cecControl"]),
        ("systemGrabber", "signal detection", ["signalDetection", "redSignalThreshold", "greenSignalThreshold", "blueSignalThreshold",
            "noSignalCounterThreshold", "sDVOffsetMin", "sDVOffsetMax", "sDHOffsetMin", "sDHOffsetMax"]),
    ];

    /// <summary>The kept effect that replaces <paramref name="effectName"/>, or the name itself when it still exists.</summary>
    public static string ReplacementEffect(string effectName)
    {
        ArgumentNullException.ThrowIfNull(effectName);
        if (s_removedEffects.TryGetValue(effectName, out var replacement))
        {
            return replacement;
        }

        foreach (var (prefix, kind) in s_musicEffectKinds)
        {
            if (effectName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return effectName;
    }

    /// <summary>Applies every rule to the settings of the first instance (which also holds the global types).</summary>
    public static void Apply(JsonObject config, Func<string, bool> isDeviceAvailable, List<string> changes)
    {
        UpgradeFormat(config, changes);
        RemoveUnsupportedFeatures(config, changes);
        ReplaceUnavailableDevice(config, isDeviceAvailable, changes);
        RenameFactoryName(config, changes);
        DropEmptyLayout(config, changes);
        RemoveAccessOptions(config, changes);
        UseDefaultSoundDevice(config, changes);

        if (config["systemGrabber"] is JsonObject capture)
        {
            ConvertToneMapping(capture, changes);
        }
    }

    /// <summary>The format upgrades HyperHDR applied by <c>general.version</c> (InstanceConfig::upgradeDB, up to version 6).</summary>
    public static void UpgradeFormat(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        var general = config["general"] as JsonObject;
        if (general is null || Number(general["version"]) is not { } number)
        {
            return;
        }

        var version = (int)number;
        var original = version;

        // v2 (HyperHDR 20): backlightThreshold changed from 0..255 counts to one step lower
        if (version < 2 && Adjustments(config) is { } v2)
        {
            foreach (var adjustment in v2)
            {
                if (Number(adjustment["backlightThreshold"]) is { } threshold && (int)threshold > 1)
                {
                    adjustment["backlightThreshold"] = (int)threshold - 1;
                }
            }

            version = 2;
        }

        // v3 removed the boblight server and v4 renamed an SPI driver: both features are gone (see RemovedSettingsTypes
        // and ReplaceUnavailableDevice)
        version = Math.Max(version, 4);

        // v5: backlightThreshold became a 0..1 fraction; any threshold means the smallest step
        if (version < 5 && Adjustments(config) is { } v5)
        {
            foreach (var adjustment in v5)
            {
                adjustment["backlightThreshold"] = Number(adjustment["backlightThreshold"]) is > 0 ? 0.0039 : 0.0;
            }

            version = 5;
        }

        if (version < 6)
        {
            if (general["disableOnLocked"] is not JsonValue locked || !locked.TryGetValue(out bool on) || !on)
            {
                general["disableOnLocked"] = true;
                changes.Add("Enabled 'pause while the PC is locked' (general.disableOnLocked) for the software screen capture");
            }

            version = 6;
        }

        if (version != original)
        {
            general["version"] = version;
            changes.Add($"Upgraded the settings format from version {original} to {version}");
        }
    }

    /// <summary>Removes options of removed features and maps removed boot/background effects to kept ones.</summary>
    public static void RemoveUnsupportedFeatures(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var (type, feature, fields) in s_obsoleteFields)
        {
            if (config[type] is not JsonObject section)
            {
                continue;
            }

            var removed = false;
            foreach (var field in fields)
            {
                removed |= section.Remove(field);
            }

            if (removed)
            {
                changes.Add($"Removed the {feature} options from the '{type}' settings: the feature is not part of this version");
            }
        }

        foreach (var type in new[] { "foregroundEffect", "backgroundEffect" })
        {
            if (config[type] is JsonObject section && section["effect"] is JsonValue e && e.TryGetValue(out string? effect) && effect.Length > 0)
            {
                var replacement = ReplacementEffect(effect);
                if (replacement != effect)
                {
                    section["effect"] = replacement;
                    changes.Add($"The '{effect}' effect of the '{type}' settings is not part of this version, replaced with '{replacement}'");
                }
            }
        }
    }

    /// <summary>
    /// Switches the LED output to the <c>file</c> driver (no physical output) when the configured driver isn't
    /// available (removed from the product, or not ported to this version yet). Keeps color order and refresh time.
    /// </summary>
    public static void ReplaceUnavailableDevice(JsonObject config, Func<string, bool> isDeviceAvailable, List<string> changes, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(isDeviceAvailable);
        ArgumentNullException.ThrowIfNull(changes);
        var device = config["device"] as JsonObject ?? [];
        var stored = device["type"] is JsonValue t && t.TryGetValue(out string? s) ? s : "file";
        var type = stored.ToLowerInvariant();
        if (isDeviceAvailable(type))
        {
            if (type != stored)
            {
                device["type"] = type;
                config["device"] = device;
            }

            return;
        }

        var fallback = new JsonObject { ["type"] = "file", ["output"] = "/dev/null", ["printTimeStamp"] = false };
        foreach (var key in new[] { "colorOrder", "refreshTime" })
        {
            if (device[key] is { } value)
            {
                fallback[key] = value.DeepClone();
            }
        }

        config["device"] = fallback;
        changes.Add($"{(owner is null ? string.Empty : owner + ": ")}The LED device type '{type}' is not supported by this version. The LED output has been switched to the 'file' device (no physical output); select a supported LED device in the LED hardware settings");
    }

    /// <summary>Removes <see cref="RemovedNetworkOptions"/> from the <c>network</c> section.</summary>
    public static void RemoveAccessOptions(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        if (config["network"] is not JsonObject network)
        {
            return;
        }

        var removed = RemovedNetworkOptions.Where(network.Remove).ToList();
        if (removed.Count > 0)
        {
            changes.Add($"Removed the network access options ({string.Join(", ", removed)}): this computer always has full access, other devices always need the password");
        }
    }

    /// <summary>
    /// HyperHDR could only capture recording devices (waveIn), so <c>soundEffect.device</c> holds a microphone name
    /// (or none) because nothing else was offered. Music on a PC plays through its output, so the device becomes
    /// <c>default</c>: what the current default output plays, following the switch between speakers and headphones.
    /// Ids this version stores (<c>loopback:</c>, <c>input:</c>) are kept.
    /// </summary>
    public static void UseDefaultSoundDevice(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        if (config["soundEffect"] is not JsonObject sound)
        {
            return;
        }

        var device = sound["device"] is JsonValue d && d.TryGetValue(out string? s) ? s.Trim() : string.Empty;
        if (device is DefaultSoundDevice || device.StartsWith("loopback:", StringComparison.Ordinal) || device.StartsWith("input:", StringComparison.Ordinal))
        {
            return;
        }

        sound["device"] = DefaultSoundDevice;
        changes.Add(device.Length > 0
            ? $"The music effects listened to the recording device '{device}'; they now listen to what this PC plays (the default output). Pick a microphone in the music settings to react to the room instead"
            : "The music effects now listen to what this PC plays (the default output)");
    }

    /// <summary>HyperHDR's factory configuration name becomes this product's (a name the user chose is kept).</summary>
    public static void RenameFactoryName(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        if (config["general"] is JsonObject general && general["name"] is JsonValue n && n.TryGetValue(out string? name) && name == LegacyFactoryName)
        {
            general["name"] = FactoryName;
            changes.Add($"Renamed the configuration from '{LegacyFactoryName}' to '{FactoryName}'");
        }
    }

    /// <summary>
    /// An empty <c>layout</c> record (<c>{}</c> or no strips) next to existing <c>leds</c> isn't a layout, so the
    /// section is left absent and the UI derives one from <c>leds</c> (backend bug 7).
    /// </summary>
    public static void DropEmptyLayout(JsonObject config, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);
        if (config["layout"] is { } layout && (layout is not JsonObject o || o["strips"] is not JsonArray { Count: > 0 }))
        {
            config.Remove("layout");
            changes.Add("Ignored the empty 'layout' settings (the layout is derived from the LED positions)");
        }
    }

    /// <summary>
    /// <c>systemGrabber.hdrToneMapping</c> was a switch in HyperHDR and is a mode here:
    /// on becomes "auto" and off becomes "off". On applied the tone mapping table to every 8-bit capture, also of SDR
    /// monitors, where it oversaturates normal content and shifts it towards red and magenta; users turned it on for
    /// their HDR monitor, and "auto" keeps it there (an HDR monitor is handled exactly as before) without applying it
    /// to a monitor in SDR mode. "on" keeps the old behavior for those who need it, and is chosen in the UI.
    /// </summary>
    public static void ConvertToneMapping(JsonObject systemGrabber, List<string> changes)
    {
        ArgumentNullException.ThrowIfNull(systemGrabber);
        ArgumentNullException.ThrowIfNull(changes);
        if (systemGrabber["hdrToneMapping"] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            var on = v.GetValueKind() == JsonValueKind.True;
            systemGrabber["hdrToneMapping"] = on ? "auto" : "off";
            changes.Add(on
                ? "HDR tone mapping was on; it is now 'auto' (applied only while the captured monitor is in HDR mode, so SDR colors aren't oversaturated)"
                : "HDR tone mapping was off; it is now 'off'");
        }
    }

    private static List<JsonObject>? Adjustments(JsonObject config) =>
        (config["color"] as JsonObject)?["channelAdjustment"] is JsonArray list ? [.. list.OfType<JsonObject>()] : null;

    // Through the JSON text: values set by an earlier rule only convert to their own CLR type.
    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;
}
