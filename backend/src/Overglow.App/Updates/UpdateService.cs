using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Overglow.App.Updates;

/// <summary>Checks a release feed and downloads a newer release (implemented with Velopack).</summary>
public interface IUpdateClient
{
    /// <summary>This copy was installed by the installer (a copy run from a build or publish folder can't update itself).</summary>
    bool IsInstalled { get; }

    /// <summary>Downloads the newest release if it is newer than this one.</summary>
    /// <returns>The version now waiting to be installed, or null when this one is the newest.</returns>
    Task<string?> DownloadNewerReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Automatic updates of an installed Overglow: checks the feed shortly after the start and then once a day, and
/// downloads a newer release in the background. Velopack installs it at the next start of the app (before the
/// lights come on), so a running session is never interrupted.
/// </summary>
public sealed partial class UpdateService(UpdateSettings settings, IUpdateClient client, TimeProvider time, ILogger<UpdateService> logger) : BackgroundService
{
    /// <summary>Delay after the start, so the update check doesn't compete with starting the lights.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Retry delay after a failed check (no network, feed unreachable).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            LogNotConfigured(logger);
            return;
        }

        if (!client.IsInstalled)
        {
            LogNotInstalled(logger);
            return;
        }

        var delay = FirstCheckDelay;
        while (true)
        {
            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = await CheckOnceAsync(stoppingToken).ConfigureAwait(false) ? CheckInterval : RetryDelay;
        }
    }

    /// <returns>False when the check failed and should be retried soon.</returns>
    private async Task<bool> CheckOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var version = await client.DownloadNewerReleaseAsync(stoppingToken).ConfigureAwait(false);
            if (version is not null)
            {
                LogDownloaded(logger, version);
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCheckFailed(logger, settings.FeedUrl!, ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Automatic updates are off: no update feed is configured")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Automatic updates are off: this copy wasn't installed by the installer")]
    private static partial void LogNotInstalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Overglow {Version} was downloaded; it is installed at the next start")]
    private static partial void LogDownloaded(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking {Feed} for updates failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, Uri feed, string reason);
}
