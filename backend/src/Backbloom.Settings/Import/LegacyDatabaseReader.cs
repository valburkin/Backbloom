using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Backbloom.Settings.Storage;

namespace Backbloom.Settings.Import;

/// <summary>
/// Reads a HyperHDR database (port of the table layouts in sources/db and of LegacyMigration's
/// detection). The source is never opened: it is copied (with its journal files) and the copy is read, so a running
/// HyperHDR and its file are left alone. The copy stays in the given folder, which keeps the original settings of
/// anything this version couldn't import.
/// </summary>
public static class LegacyDatabaseReader
{
    private const string InstanceColumn = "hyperhdr_instance";

    // SQLite side files that hold committed data not yet in the main file
    private static readonly string[] s_sideFiles = ["-wal", "-journal"];

    /// <summary>Copies <paramref name="sourcePath"/> into <paramref name="copyDirectory"/> and reads the copy.</summary>
    /// <exception cref="InvalidDataException">The file isn't a HyperHDR database.</exception>
    public static LegacySnapshot Read(string sourcePath, string copyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyDirectory);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The database to import doesn't exist.", sourcePath);
        }

        Directory.CreateDirectory(copyDirectory);
        var copy = Path.Combine(copyDirectory, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(copy), sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The import copy would overwrite the source database.");
        }

        File.Copy(sourcePath, copy, overwrite: true);
        foreach (var suffix in s_sideFiles)
        {
            File.Delete(copy + suffix);
            if (File.Exists(sourcePath + suffix))
            {
                File.Copy(sourcePath + suffix, copy + suffix);
            }
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = copy,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return ReadSnapshot(connection, sourcePath);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"{sourcePath} isn't a readable SQLite database: {ex.Message}", ex);
        }
    }

    private static LegacySnapshot ReadSnapshot(SqliteConnection connection, string source)
    {
        if (Convert.ToInt64(Scalar(connection, "PRAGMA application_id"), CultureInfo.InvariantCulture) == SettingsDatabase.ApplicationId)
        {
            throw new InvalidDataException($"{source} is already a Backbloom settings database; restore a backup instead.");
        }

        var tables = Strings(connection, "SELECT name FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains("settings"))
        {
            throw new InvalidDataException($"{source} has no settings table; it isn't a HyperHDR database.");
        }

        var settingsColumns = Strings(connection, "SELECT name FROM pragma_table_info('settings')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var instanceColumn = settingsColumns.Contains(InstanceColumn) ? InstanceColumn : null;

        var settings = new List<LegacySetting>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT type, config, {instanceColumn ?? "NULL"} FROM settings";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Text(reader, 0) is { } type && Text(reader, 1) is { } config)
                {
                    settings.Add(new LegacySetting(type, reader.IsDBNull(2) ? null : reader.GetInt32(2), config));
                }
            }
        }

        var instances = new List<LegacyInstance>();
        if (tables.Contains("instances"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT instance, friendly_name, enabled FROM instances WHERE instance IS NOT NULL ORDER BY instance";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var number = reader.GetInt32(0);
                instances.Add(new LegacyInstance(number, Text(reader, 1) ?? $"LED instance {number}", !reader.IsDBNull(2) && reader.GetInt64(2) != 0));
            }
        }

        var users = new List<LegacyUser>();
        var tokens = new List<LegacyToken>();
        if (tables.Contains("auth"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT user, password, salt, token, id, comment, last_use FROM auth";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var user = Text(reader, 0);
                if (user is not null && Text(reader, 1) is { } password && Text(reader, 2) is { } salt)
                {
                    users.Add(new LegacyUser(user, password, salt));
                }
                else if (user is null && Text(reader, 4) is { } id && Text(reader, 3) is { } token)
                {
                    var lastUse = Text(reader, 6) is { } u && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
                    tokens.Add(new LegacyToken(id, Text(reader, 5) ?? string.Empty, token, lastUse));
                }
            }
        }

        var uuid = tables.Contains("meta") ? Strings(connection, "SELECT uuid FROM meta WHERE uuid IS NOT NULL AND uuid <> ''").FirstOrDefault() : null;
        return new LegacySnapshot(source, "HyperHDR database", instances, settings, users, tokens, uuid);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            if (Text(reader, 0) is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    // HyperHDR binds hashes as BLOBs holding hex text and everything else as TEXT.
    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetValue(column) switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };
}
