using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Overglow.Settings.Storage;

/// <summary>One step of the database schema: applied once, in its own transaction, to databases below its version.</summary>
internal sealed record SchemaMigration(int Version, string Description, Action<SqliteConnection, SqliteTransaction> Apply);

/// <summary>
/// The SQLite file that holds the settings, the controllers and the credentials (<c>settings.db</c> in the data
/// directory). The schema version is the database's <c>user_version</c>; <see cref="Open"/> applies the missing
/// forward migrations and refuses files written by a newer version. One connection serves the process; every use is
/// serialized, and writes run in a transaction that is rolled back when anything fails.
/// </summary>
public sealed class SettingsDatabase : IDisposable
{
    public const string FileName = "settings.db";

    /// <summary><c>PRAGMA application_id</c> of Overglow settings files ("OGLW"), to tell them from HyperHDR databases.</summary>
    public const int ApplicationId = 0x4F474C57;

    internal static readonly IReadOnlyList<SchemaMigration> Migrations =
    [
        new(1, "settings, controllers, credentials and metadata", (c, t) => Execute(c, t, """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE settings (section TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE controllers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, enabled INTEGER NOT NULL);
            CREATE TABLE controller_settings (
                controller_id INTEGER NOT NULL REFERENCES controllers (id) ON DELETE CASCADE,
                section TEXT NOT NULL,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (controller_id, section)) WITHOUT ROWID;
            CREATE TABLE admin_password (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                algorithm TEXT NOT NULL,
                hash BLOB NOT NULL,
                salt BLOB NOT NULL,
                iterations INTEGER NOT NULL,
                is_default INTEGER NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE api_tokens (
                id TEXT PRIMARY KEY,
                comment TEXT NOT NULL,
                algorithm TEXT NOT NULL,
                hash BLOB NOT NULL,
                salt BLOB NOT NULL,
                iterations INTEGER NOT NULL,
                last_use TEXT);
            """)),

        // The switch became a mode; see LegacyMigrations.ConvertToneMapping for why on becomes "auto".
        new(2, "systemGrabber.hdrToneMapping: switch to mode", (c, t) => Execute(c, t, """
            UPDATE settings
            SET value = json_set(value, '$.hdrToneMapping', CASE json_type(value, '$.hdrToneMapping') WHEN 'true' THEN 'auto' ELSE 'off' END)
            WHERE section = 'systemGrabber' AND json_valid(value) AND json_type(value, '$.hdrToneMapping') IN ('true', 'false');
            """)),

        // The first-run welcome holds the lights until the user starts them. A database that was already set up
        // (it has its first-start marker) counts as started, so updating never leaves the lights off; a new one
        // (no meta rows yet while it is created) doesn't. See SqliteSetupState.
        new(3, "first run: databases already in use count as started", (c, t) => Execute(c, t, """
            INSERT INTO meta (key, value)
            SELECT 'first_run_completed', 'upgrade' WHERE EXISTS (SELECT 1 FROM meta WHERE key = 'initialized_at');
            """)),

        // This computer always has full access and other devices always sign in, so HyperHDR's access options are
        // gone (LegacyMigrations.RemovedNetworkOptions; imports drop them too), and so is the factory password: until
        // the user sets one there is none, and remote access stays closed.
        new(4, "network: access options removed; factory password removed", (c, t) => Execute(c, t, """
            UPDATE settings
            SET value = json_remove(value, '$.apiAuth', '$.localApiAuth', '$.localAdminAuth', '$.internetAccessAPI', '$.restirctedInternetAccessAPI', '$.ipWhitelist')
            WHERE section = 'network' AND json_valid(value);
            DELETE FROM admin_password WHERE is_default <> 0;
            """)),
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    private SettingsDatabase(SqliteConnection connection, string path, bool created, int version)
    {
        _connection = connection;
        FilePath = path;
        Created = created;
        Version = version;
    }

    public static int CurrentVersion => Migrations[^1].Version;

    public string FilePath { get; }

    public string DirectoryPath => Path.GetDirectoryName(FilePath)!;

    /// <summary>Whether <see cref="Open"/> created the file.</summary>
    public bool Created { get; }

    /// <summary>Schema version after opening.</summary>
    public int Version { get; }

    /// <summary>Opens (or creates) the database and brings its schema up to <see cref="CurrentVersion"/>.</summary>
    /// <exception cref="InvalidDataException">The file isn't an Overglow settings database or is newer than this version.</exception>
    public static SettingsDatabase Open(string path) => Open(path, Migrations);

    internal static SettingsDatabase Open(string path, IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = !File.Exists(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        try
        {
            connection.Open();
            var version = Migrate(connection, path, migrations);
            return new SettingsDatabase(connection, path, created, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="read"/> with the connection (serialized with every other use).</summary>
    public T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
        {
            return read(_connection);
        }
    }

    /// <summary>Runs <paramref name="write"/> in a transaction: committed when it returns, rolled back when it throws.</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var result = write(_connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Write<bool>((c, t) =>
        {
            write(c, t);
            return true;
        });
    }

    public string? GetMeta(string key) => Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    });

    public void SetMeta(string key, string value) => Write((c, t) => SetMeta(c, t, key, value));

    internal static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
        Execute(connection, transaction, "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    /// <summary>The installation id reported by <c>sysinfo</c> (imported from HyperHDR or created on first use).</summary>
    public string GetOrCreateInstanceId()
    {
        lock (_lock)
        {
            if (GetMeta("instance_id") is { } id && Guid.TryParse(id, out _))
            {
                return id;
            }

            var created = Guid.NewGuid().ToString();
            SetMeta("instance_id", created);
            return created;
        }
    }

    public void Dispose() => _connection.Dispose();

    internal static string Timestamp(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int Migrate(SqliteConnection connection, string path, IReadOnlyList<SchemaMigration> migrations)
    {
        var applicationId = Scalar(connection, "PRAGMA application_id");
        var version = (int)Scalar(connection, "PRAGMA user_version");
        var hasTables = Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type = 'table'") > 0;
        if (hasTables && applicationId != ApplicationId)
        {
            throw new InvalidDataException($"{path} isn't an Overglow settings database (HyperHDR databases are imported, not opened).");
        }

        var latest = migrations[^1].Version;
        if (version > latest)
        {
            throw new InvalidDataException($"{path} was written by a newer Overglow (settings format {version}; this version reads up to {latest}).");
        }

        foreach (var migration in migrations.Where(m => m.Version > version).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            Execute(connection, transaction, $"PRAGMA application_id = {ApplicationId.ToString(CultureInfo.InvariantCulture)}");
            Execute(connection, transaction, $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)}");
            transaction.Commit();
            version = migration.Version;
        }

        return version;
    }
}
