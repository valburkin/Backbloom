using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Overglow.Settings.Auth;

namespace Overglow.Settings.Import;

/// <summary>What an import produced: valid settings, credentials (null: keep the current ones) and what was changed.</summary>
public sealed record ImportResult(SettingsContent Content, AuthData? Auth, string? InstanceId, IReadOnlyList<string> Changes);

/// <summary>
/// Turns a HyperHDR / Overglow C++ database or backup into this version's settings. HyperHDR ran one complete
/// pipeline per instance; this version runs one pipeline that feeds many controllers:
/// <list type="bullet">
/// <item>global types and the per-instance settings of the first instance (lowest number) become the pipeline's sections;</item>
/// <item>the <c>device</c> of each instance, in instance order, becomes a controller (ids 0, 1, ...; name and enabled state from the instances table);</item>
/// <item>the other per-instance settings of further instances (their own LED layout, color, smoothing, effects) aren't imported, which is reported.</item>
/// </list>
/// Then the <see cref="LegacyMigrations"/> run and every section is repaired against its schema (each fix is
/// reported). The conversion is a pure function of its input, so importing the same file twice gives the same result.
/// </summary>
public sealed class SettingsImporter(SettingsSchema schema)
{
    /// <summary>HyperHDR's and the Overglow C++ build's factory passwords: an admin password still set to one of them isn't imported (this version has no factory password).</summary>
    private static readonly string[] s_factoryPasswords = ["hyperhdr", "overglow"];

    private static readonly string[] s_adminUsers = ["admin", "Hyperhdr"];

    public ImportResult Convert(LegacySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changes = new List<string>();
        var instances = snapshot.Instances.Count > 0 ? snapshot.Instances : [new LegacyInstance(0, "LED controller", true)];
        var globals = snapshot.Settings
            .Where(s => LegacyMigrations.GlobalTypes.Contains(s.Type))
            .OrderBy(s => s.Instance.HasValue)
            .ThenBy(s => s.Instance)
            .DistinctBy(s => s.Type)
            .ToList();

        ReportDroppedTypes(snapshot, changes);

        // per-instance rows without an instance (hand-made backups) belong to the first instance
        var unowned = snapshot.Settings.Where(s => s.Instance is null && !LegacyMigrations.GlobalTypes.Contains(s.Type)
            && !snapshot.Settings.Any(o => o.Type == s.Type && o.Instance == instances[0].Instance));
        var primary = Compose(globals.Concat(unowned), snapshot, instances[0].Instance, changes);
        LegacyMigrations.Apply(primary, schema.HasDeviceType, changes);
        var sections = new JsonObject();
        foreach (var section in SettingsSchema.BuiltinSections.Where(s => s != SettingsSchema.ControllerSection && schema.HasSection(s)))
        {
            if (Section(section, primary[section], changes) is { } value)
            {
                sections[section] = value;
            }
        }

        var controllers = new List<ControllerSettings>();
        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var config = i == 0 ? primary : Compose([], snapshot, instance.Instance, changes);
            if (i > 0)
            {
                LegacyMigrations.ReplaceUnavailableDevice(config, schema.HasDeviceType, changes, $"Instance '{instance.Name}'");
                var skipped = config.Select(kv => kv.Key).Where(k => k != SettingsSchema.ControllerSection).Order(StringComparer.Ordinal).ToList();
                if (skipped.Count > 0)
                {
                    changes.Add($"Instance '{instance.Name}' became LED controller {i}; its own {string.Join(", ", skipped)} settings were not imported (Overglow runs one pipeline, configured from the first instance)");
                }
            }

            var device = Section(SettingsSchema.ControllerSection, config[SettingsSchema.ControllerSection], changes)!.AsObject();
            controllers.Add(new ControllerSettings(i, instance.Name, i == 0 || instance.Enabled, device));
        }

        var auth = ConvertAuth(snapshot, changes);
        var instanceId = Guid.TryParse(snapshot.InstanceId, out var id) ? id.ToString() : null;
        return new ImportResult(new SettingsContent(sections, controllers), auth, instanceId, changes);
    }

    /// <summary>HyperHDR's password hash: hex SHA-512 of the UTF-8 password followed by the salt text.</summary>
    public static string LegacyHash(string secret, string salt)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(salt);
        return System.Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(secret + salt)));
    }

    private static void ReportDroppedTypes(LegacySnapshot snapshot, List<string> changes)
    {
        foreach (var type in snapshot.Settings.Select(s => s.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (LegacyMigrations.RemovedSettingsTypes.Contains(type))
            {
                changes.Add($"Removed the '{type}' settings: the feature is not part of this version");
            }
            else if (!SettingsSchema.BuiltinSections.Contains(type))
            {
                changes.Add($"Removed the unknown '{type}' settings");
            }
        }
    }

    /// <summary>The settings of one instance as HyperHDR loaded them: the global types plus the instance's own rows.</summary>
    private static JsonObject Compose(IEnumerable<LegacySetting> globals, LegacySnapshot snapshot, int instance, List<string> changes)
    {
        var config = new JsonObject();
        var rows = globals.Concat(snapshot.Settings.Where(s => s.Instance == instance && !LegacyMigrations.GlobalTypes.Contains(s.Type)));
        foreach (var row in rows)
        {
            if (!SettingsSchema.BuiltinSections.Contains(row.Type))
            {
                continue;
            }

            try
            {
                config[row.Type] = JsonNode.Parse(row.Config);
            }
            catch (JsonException)
            {
                changes.Add($"The '{row.Type}' settings couldn't be read (not valid JSON); defaults are used");
            }
        }

        return config;
    }

    private JsonNode? Section(string section, JsonNode? value, List<string> changes)
    {
        if (value is null)
        {
            return schema.CreateDefault(section);
        }

        var fixes = new List<SettingsError>();
        var repaired = schema.Repair(section, value, fixes);
        foreach (var fix in fixes)
        {
            changes.Add($"Fixed {fix.Path}: {fix.Message}");
        }

        if (repaired is null)
        {
            changes.Add($"The '{section}' settings couldn't be repaired; defaults are used");
            return schema.CreateDefault(section);
        }

        return repaired;
    }

    private static AuthData? ConvertAuth(LegacySnapshot snapshot, List<string> changes)
    {
        var admin = s_adminUsers.Select(name => snapshot.Users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(u => u is not null) ?? (snapshot.Users.Count > 0 ? snapshot.Users[0] : null);
        var tokens = snapshot.Tokens
            .Where(t => IsHex(t.TokenHash, 128))
            .Select(t => new ApiToken(t.Id, t.Comment, new SecretHash(System.Convert.FromHexString(t.TokenHash), [], 0, SecretAlgorithm.LegacySha512), t.LastUse))
            .ToList();
        if (tokens.Count > 0)
        {
            changes.Add($"Imported {tokens.Count} API token{(tokens.Count == 1 ? string.Empty : "s")}");
        }

        if (admin is null || !IsHex(admin.PasswordHash, 128))
        {
            return tokens.Count == 0 ? null : AuthData.Empty with { Tokens = tokens };
        }

        if (s_factoryPasswords.Any(p => string.Equals(LegacyHash(p, admin.Salt), admin.PasswordHash, StringComparison.OrdinalIgnoreCase)))
        {
            changes.Add("The factory default password was still in use and wasn't imported; set a password before turning on remote access");
            return new AuthData(null, tokens);
        }

        changes.Add("Imported the admin password (it keeps working)");
        var password = new SecretHash(System.Convert.FromHexString(admin.PasswordHash), Encoding.UTF8.GetBytes(admin.Salt), 0, SecretAlgorithm.LegacySha512);
        return new AuthData(password, tokens);
    }

    private static bool IsHex(string text, int length) => text.Length == length && text.All(char.IsAsciiHexDigit);
}
