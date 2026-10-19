using System.Text.Json.Nodes;
using Overglow.Settings;
using Overglow.Settings.Import;

namespace Overglow.Server.Rpc;

/// <summary><c>save-db</c>: the settings backup (port of HyperAPI::handleSaveDB; credentials are never included).</summary>
public sealed class SaveDbCommand(ISettingsStore settings) : IRpcCommand
{
    public string Name => "save-db";

    public RpcAccess Access => RpcAccess.Admin;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Reply(SettingsBackup.Create(settings.Export()));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>load-db</c>: restores a backup of this version, the Overglow C++ build or HyperHDR while running. The settings
/// are migrated and repaired like an imported database, the current ones are saved to <c>backups/</c> first, and the
/// pipeline and controllers pick the new values up through the usual <c>settings-update</c>. HyperHDR quit the
/// process instead (and on Windows didn't start again: backend bug 6). The reply lists what the migration changed.
/// </summary>
public sealed class LoadDbCommand(SettingsServices services) : IRpcCommand
{
    public string Name => "load-db";

    public RpcAccess Access => RpcAccess.Admin;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Message["config"] is not JsonObject backup)
        {
            context.Fail("The config parameter must be a settings backup");
            return ValueTask.CompletedTask;
        }

        ImportOutcome outcome;
        try
        {
            outcome = services.RestoreBackup(backup);
        }
        catch (InvalidDataException ex)
        {
            context.Fail($"Couldn't restore the backup: {ex.Message}");
            return ValueTask.CompletedTask;
        }

        if (outcome.Stored.Success)
        {
            context.Reply(new JsonObject { ["changes"] = new JsonArray([.. outcome.Result.Changes.Select(c => (JsonNode)c)]) });
        }
        else
        {
            context.Fail($"Couldn't restore the backup: {ConfigCommand.Describe(outcome.Stored.Errors)}", errors: outcome.Stored.Errors);
        }

        return ValueTask.CompletedTask;
    }
}
