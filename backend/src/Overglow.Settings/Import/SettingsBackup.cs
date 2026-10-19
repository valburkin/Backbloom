using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overglow.Settings.Import;

/// <summary>
/// The backup file of <c>save-db</c> / <c>load-db</c> (port of DBManager::getBackup / restoreBackup): the C++ export
/// format, so backups move freely between this version, the Overglow C++ build and HyperHDR (whose backups are
/// accepted as well). Credentials are never part of a backup.
/// </summary>
/// <remarks>
/// <c>{ version, instances: [{ instance, friendly_name, enabled }], settings: [{ type, config: "JSON text", instance? }] }</c>;
/// global types have no instance, HyperHDR's per-instance types have instance 0 (the pipeline) and the <c>device</c>
/// of controller N has instance N.
/// </remarks>
public static class SettingsBackup
{
    public const string Version = "Overglow_export_format_v1";

    private static readonly string[] s_acceptedVersions = [Version, "HyperHDR_export_format_v17", "HyperHDR_export_format_v20"];

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    public static JsonObject Create(SettingsContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var instances = new JsonArray();
        var settings = new JsonArray();
        foreach (var (section, value) in content.Sections)
        {
            var row = new JsonObject { ["type"] = section, ["config"] = value?.ToJsonString() };
            if (!LegacyMigrations.GlobalTypes.Contains(section))
            {
                // HyperHDR's per-instance types belong to the first instance, whose settings configure the pipeline
                row["instance"] = Storage.SqliteSettingsStore.PrimaryController;
            }

            settings.Add((JsonNode)row);
        }

        foreach (var controller in content.Controllers)
        {
            instances.Add((JsonNode)new JsonObject { ["instance"] = controller.Id, ["friendly_name"] = controller.Name, ["enabled"] = controller.Enabled ? 1 : 0 });
            settings.Add((JsonNode)new JsonObject
            {
                ["type"] = SettingsSchema.ControllerSection,
                ["config"] = controller.Device.ToJsonString(),
                ["instance"] = controller.Id,
            });
        }

        return new JsonObject { ["version"] = Version, ["instances"] = instances, ["settings"] = settings };
    }

    /// <summary>Reads a backup (as sent by <c>load-db</c> or read from a file).</summary>
    /// <exception cref="InvalidDataException">It isn't a backup of a supported format.</exception>
    public static LegacySnapshot Parse(JsonObject backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var version = backup["version"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        if (version is null || !s_acceptedVersions.Contains(version))
        {
            throw new InvalidDataException($"This isn't a settings backup this version can restore (format \"{version ?? "missing"}\"; supported: {string.Join(", ", s_acceptedVersions)}).");
        }

        if (backup["settings"] is not JsonArray rows)
        {
            throw new InvalidDataException("The backup has no settings list.");
        }

        var settings = new List<LegacySetting>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var type = row["type"] is JsonValue t && t.TryGetValue(out string? ts) ? ts : null;
            var config = row["config"] switch
            {
                JsonValue c when c.TryGetValue(out string? text) => text,
                JsonObject or JsonArray => row["config"]!.ToJsonString(),
                _ => null,
            };
            if (type is not null && config is not null)
            {
                // legacy HyperHDR backups use a different name for the instance column
                settings.Add(new LegacySetting(type, Int(row["instance"]) ?? Int(row["hyperhdr_instance"]), config));
            }
        }

        var instances = (backup["instances"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => Int(i["instance"]) is not null)
            .Select(i => new LegacyInstance(
                Int(i["instance"])!.Value,
                i["friendly_name"] is JsonValue n && n.TryGetValue(out string? name) ? name : $"LED instance {Int(i["instance"])}",
                IsEnabled(i["enabled"])))
            .OrderBy(i => i.Instance)
            .ToList();

        return new LegacySnapshot("backup", version, instances, settings, [], [], null);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <c>backups/backup_&lt;time&gt;.json</c> under <paramref name="dataDirectory"/>
    /// (done before every restore or import replaces the settings, as DBManager::createLocalBackup did); returns the path.
    /// </summary>
    public static string WriteLocal(string dataDirectory, SettingsContent content, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"backup_{now.ToLocalTime().ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture)}.json");
        File.WriteAllText(path, Create(content).ToJsonString(s_indented));
        return path;
    }

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? (int)d : null;

    // HyperHDR stored the flag as 0/1; accept true/false too.
    private static bool IsEnabled(JsonNode? node) => node is JsonValue v && (v.TryGetValue(out bool b) ? b : Int(v) is { } n && n != 0);
}
