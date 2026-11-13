using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Settings.Import;
using Backbloom.Settings.Storage;

namespace Backbloom.Settings.Tests;

/// <summary>A temporary folder deleted with everything in it.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"backbloom-settings-{Guid.NewGuid():N}");

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>An open settings database and store.</summary>
internal sealed class OpenStore(SettingsDatabase database, SqliteSettingsStore store) : IDisposable
{
    public SettingsDatabase Database { get; } = database;

    public SqliteSettingsStore Store { get; } = store;

    public static OpenStore In(string directory, SettingsSchema? schema = null)
    {
        var database = SettingsDatabase.Open(Path.Combine(directory, SettingsDatabase.FileName));
        return new OpenStore(database, new SqliteSettingsStore(database, schema ?? TestSchema.Create(), NullLogger<SqliteSettingsStore>.Instance));
    }

    public void Dispose() => Database.Dispose();
}

internal static class TestSchema
{
    /// <summary>The built-in sections with small stand-ins for the file and WLED driver schemas.</summary>
    public static SettingsSchema Create() => SettingsSchema.CreateBuiltin(new Dictionary<string, JsonObject>
    {
        ["file"] = JsonNode.Parse("""
            { "type": "object", "properties": {
                "output": { "type": "string", "default": "/dev/null" },
                "printTimeStamp": { "type": "boolean", "default": true, "required": true } },
              "additionalProperties": true }
            """)!.AsObject(),
        ["wled"] = JsonNode.Parse("""
            { "type": "object", "properties": {
                "host": { "type": "string" },
                "brightnessMax": { "type": "boolean", "default": true },
                "brightnessMaxLevel": { "type": "integer", "minimum": 1, "maximum": 255, "default": 255, "required": true },
                "maxRetry": { "type": "integer", "minimum": 0, "maximum": 300, "default": 60, "required": true } },
              "additionalProperties": true }
            """)!.AsObject(),
    });

    public static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}

internal static class Fixtures
{
    /// <summary>The reference setup's HyperHDR v22 database, sanitized (see the README section on settings).</summary>
    public static string ReferenceDatabase => Path.Combine(AppContext.BaseDirectory, "fixtures", "settings", "reference-hyperhdr-v22.db");

    /// <summary>The fixture's admin password ((the original one was replaced)).</summary>
    public const string FixturePassword = "fixture-password";
}

/// <summary>Writes databases in the table layout HyperHDR created.</summary>
internal sealed class LegacyDatabaseBuilder
{
    private readonly List<(string Type, int? Instance, string Config)> _settings = [];
    private readonly List<(int Instance, string Name, bool Enabled)> _instances = [];
    private readonly List<(string? User, string? Password, string? Salt, string? Token, string? Id, string? Comment)> _auth = [];

    public LegacyDatabaseBuilder Instance(int instance, string name, bool enabled = true)
    {
        _instances.Add((instance, name, enabled));
        return this;
    }

    public LegacyDatabaseBuilder Setting(string type, int? instance, string config)
    {
        _settings.Add((type, instance, config));
        return this;
    }

    public LegacyDatabaseBuilder User(string user, string password, string salt)
    {
        _auth.Add((user, SettingsImporter.LegacyHash(password, salt), salt, null, null, null));
        return this;
    }

    public LegacyDatabaseBuilder Token(string id, string comment, string token)
    {
        _auth.Add((null, null, null, SettingsImporter.LegacyHash(token, string.Empty), id, comment));
        return this;
    }

    public string Write(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, $"CREATE TABLE settings ( type TEXT , config TEXT, hyperhdr_instance INTEGER, updated_at TEXT)");
        Execute(connection, "CREATE TABLE instances ( instance INTEGER , friendly_name TEXT, enabled INTEGER DEFAULT 0, last_use TEXT)");
        Execute(connection, "CREATE TABLE auth ( user TEXT , password BLOB, token BLOB, salt BLOB, comment TEXT, portal_token TEXT, id TEXT, created_at TEXT, last_use TEXT)");
        Execute(connection, "CREATE TABLE meta ( uuid TEXT , created_at TEXT)");
        Execute(connection, "INSERT INTO meta (uuid) VALUES ('2f0b1c55-1111-5222-8333-444455556666')");
        foreach (var (instance, name, enabled) in _instances)
        {
            Execute(connection, "INSERT INTO instances (instance, friendly_name, enabled) VALUES ($i, $n, $e)", ("$i", instance), ("$n", name), ("$e", enabled ? 1 : 0));
        }

        foreach (var (type, instance, config) in _settings)
        {
            Execute(connection, $"INSERT INTO settings (type, config, hyperhdr_instance) VALUES ($t, $c, $i)", ("$t", type), ("$c", config), ("$i", instance));
        }

        foreach (var (user, password, salt, token, id, comment) in _auth)
        {
            Execute(connection, "INSERT INTO auth (user, password, salt, token, id, comment) VALUES ($u, $p, $s, $t, $id, $c)",
                ("$u", user), ("$p", Blob(password)), ("$s", Blob(salt)), ("$t", Blob(token)), ("$id", id), ("$c", comment));
        }

        return path;
    }

    private static byte[]? Blob(string? text) => text is null ? null : System.Text.Encoding.ASCII.GetBytes(text);

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
