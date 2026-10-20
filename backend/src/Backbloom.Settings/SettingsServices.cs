using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Backbloom.Settings.Auth;
using Backbloom.Settings.Import;
using Backbloom.Settings.Storage;

namespace Backbloom.Settings;

/// <summary>Where the settings live and where a first start looks for data to import.</summary>
/// <param name="DataDirectory">This product's data directory (<c>settings.db</c>, backups, import copies).</param>
/// <param name="LegacyDataDirectory">HyperHDR's data directory to import from on the first start; null: don't look.</param>
public sealed record SettingsOptions(string DataDirectory, string? LegacyDataDirectory);

/// <summary>Outcome of an import or restore: what was converted and whether it was stored.</summary>
public sealed record ImportOutcome(ImportResult Result, SettingsUpdateResult Stored, string? BackupPath);

/// <summary>
/// The settings of one process: database, settings store, credential store, and the import / restore operations
/// that replace their content. <see cref="Open"/> runs the first-start import.
/// </summary>
public sealed partial class SettingsServices : IDisposable
{
    private const string InitializedKey = "initialized_at";

    private readonly SettingsImporter _importer;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private SettingsServices(SettingsDatabase database, SettingsSchema schema, ILoggerFactory loggerFactory, TimeProvider time)
    {
        Database = database;
        Store = new SqliteSettingsStore(database, schema, loggerFactory.CreateLogger<SqliteSettingsStore>(), time);
        Auth = new SqliteAuthStore(database, time);
        Setup = new SqliteSetupState(database, time);
        _importer = new SettingsImporter(schema);
        _logger = loggerFactory.CreateLogger<SettingsServices>();
        _time = time;
    }

    public SettingsDatabase Database { get; }

    public SqliteSettingsStore Store { get; }

    public SqliteAuthStore Auth { get; }

    /// <summary>First-run state: whether the lighting was started once, what was imported.</summary>
    public SqliteSetupState Setup { get; }

    /// <summary>
    /// Opens <c>settings.db</c> in the data directory. On the first start (no settings yet) it imports HyperHDR's
    /// database (<c>db/hyperhdr.db</c> in <see cref="SettingsOptions.LegacyDataDirectory"/>), which is only copied,
    /// never modified. When that fails, it starts with defaults and doesn't try again (<c>backbloom --import</c> can).
    /// </summary>
    public static SettingsServices Open(SettingsOptions options, SettingsSchema schema, ILoggerFactory loggerFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var database = SettingsDatabase.Open(Path.Combine(options.DataDirectory, SettingsDatabase.FileName));
        try
        {
            var services = new SettingsServices(database, schema, loggerFactory, time ?? TimeProvider.System);
            if (database.GetMeta(InitializedKey) is null)
            {
                services.FirstStart(options);
            }

            return services;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Imports a HyperHDR database: settings, controllers, admin password and API tokens replace the
    /// current ones (which are saved to <c>backups/</c> first). The source is copied to <c>imported/</c> and only the
    /// copy is read.
    /// </summary>
    public ImportOutcome ImportDatabase(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var snapshot = LegacyDatabaseReader.Read(sourcePath, Path.Combine(Database.DirectoryPath, "imported"));
        LogImporting(snapshot.Format, snapshot.Source);
        return Apply(snapshot, importCredentials: true);
    }

    /// <summary>Restores a <c>save-db</c> backup (or a HyperHDR one) while running; credentials are kept.</summary>
    /// <exception cref="InvalidDataException">It isn't a supported backup.</exception>
    public ImportOutcome RestoreBackup(JsonObject backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var snapshot = SettingsBackup.Parse(backup);
        LogRestoring(snapshot.Format);
        return Apply(snapshot, importCredentials: false);
    }

    public void Dispose() => Database.Dispose();

    private void FirstStart(SettingsOptions options)
    {
        var source = options.LegacyDataDirectory is { } legacy ? Path.Combine(legacy, DataDirectories.LegacyDatabase) : null;
        if (source is not null && File.Exists(source))
        {
            try
            {
                ImportDatabase(source);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or SqliteException)
            {
                LogFirstImportFailed(ex, source);
            }
        }

        Database.SetMeta(InitializedKey, SettingsDatabase.Timestamp(_time.GetUtcNow()));
    }

    private ImportOutcome Apply(LegacySnapshot snapshot, bool importCredentials)
    {
        var result = _importer.Convert(snapshot);
        foreach (var change in result.Changes)
        {
            LogImportChange(change);
        }

        // nothing to keep while the store still holds only defaults
        var backupPath = Database.GetMeta(SqliteSettingsStore.ModifiedKey) is null ? null : SettingsBackup.WriteLocal(Database.DirectoryPath, Store.Export(), _time.GetUtcNow());
        var stored = Store.Replace(result.Content);
        if (!stored.Success)
        {
            LogImportRejected(string.Join("; ", stored.Errors));
            return new ImportOutcome(result, stored, backupPath);
        }

        if (importCredentials)
        {
            if (result.Auth is { } auth)
            {
                Auth.Save(auth);
            }

            if (result.InstanceId is { } id)
            {
                Database.SetMeta("instance_id", id);
            }

            Database.SetMeta(SqliteSetupState.ImportedFromKey, snapshot.Source);
        }

        LogImported(result.Content.Sections.Count, result.Content.Controllers.Count, result.Changes.Count);
        return new ImportOutcome(result, stored, backupPath);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Importing the {Format} {Source} (the file itself is only copied, never changed)")]
    private partial void LogImporting(string format, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restoring a settings backup ({Format})")]
    private partial void LogRestoring(string format);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import: {Change}")]
    private partial void LogImportChange(string change);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Sections} settings sections and {Controllers} LED controller(s) ({Changes} change(s) listed above)")]
    private partial void LogImported(int sections, int controllers, int changes);

    [LoggerMessage(Level = LogLevel.Error, Message = "The imported settings were rejected: {Errors}")]
    private partial void LogImportRejected(string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Importing {Source} failed; starting with a fresh configuration (run 'backbloom --import <file>' to try again)")]
    private partial void LogFirstImportFailed(Exception exception, string source);
}
