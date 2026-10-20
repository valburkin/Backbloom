using Backbloom.Settings.Storage;

namespace Backbloom.Settings;

/// <summary>
/// What the first-run welcome needs to remember about this data directory: whether the user started the lighting
/// once, what was imported, and which explanations (network discovery) were shown. Kept in the database's metadata,
/// not in a settings section, so backups, restores and imports never change it.
/// </summary>
public interface ISetupState
{
    /// <summary>The user started the lighting once (or the database was in use before this flag existed).</summary>
    bool FirstRunCompleted { get; }

    /// <summary>The database file the settings were imported from at the first start (HyperHDR's), or null.</summary>
    string? ImportedFrom { get; }

    void CompleteFirstRun();

    /// <summary>The user has read the explanation about <paramref name="topic"/> (e.g. <see cref="SetupTopics.Discovery"/>).</summary>
    bool IsAcknowledged(string topic);

    void Acknowledge(string topic);
}

/// <summary>Explanations the UI shows once before the first use.</summary>
public static class SetupTopics
{
    /// <summary>Searching the local network for LED controllers.</summary>
    public const string Discovery = "discovery";

    public static bool IsKnown(string? topic) => topic == Discovery;
}

/// <summary><see cref="ISetupState"/> in the <c>meta</c> table of <c>settings.db</c>.</summary>
public sealed class SqliteSetupState(SettingsDatabase database, TimeProvider? time = null) : ISetupState
{
    /// <summary>Meta key of the first-run flag.</summary>
    public const string FirstRunKey = "first_run_completed";

    /// <summary>Meta key of the imported database's path (written by the import).</summary>
    public const string ImportedFromKey = "imported_from";

    private const string AcknowledgedPrefix = "acknowledged:";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool FirstRunCompleted => database.GetMeta(FirstRunKey) is not null;

    public string? ImportedFrom => database.GetMeta(ImportedFromKey);

    public void CompleteFirstRun()
    {
        if (!FirstRunCompleted)
        {
            database.SetMeta(FirstRunKey, SettingsDatabase.Timestamp(_time.GetUtcNow()));
        }
    }

    public bool IsAcknowledged(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return database.GetMeta(AcknowledgedPrefix + topic) is not null;
    }

    public void Acknowledge(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (!IsAcknowledged(topic))
        {
            database.SetMeta(AcknowledgedPrefix + topic, SettingsDatabase.Timestamp(_time.GetUtcNow()));
        }
    }
}
