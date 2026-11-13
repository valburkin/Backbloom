using System.Diagnostics.CodeAnalysis;
using Velopack;
using Velopack.Sources;

namespace Backbloom.App.Updates;

/// <summary>
/// <see cref="IUpdateClient"/> with Velopack (MIT). Velopack is only touched in an installed copy: its locator writes
/// a log file to the user's profile as soon as it is created, which a development or test run must not do.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Velopack writes a log to the user's profile as soon as it is used and downloads from the release feed; UpdateService is tested with a fake IUpdateClient.")]
internal sealed class VelopackUpdateClient(UpdateSettings settings) : IUpdateClient
{
    private UpdateManager? _manager;

    public bool IsInstalled => InstalledLayout.IsInstalled(AppContext.BaseDirectory);

    public async Task<string?> DownloadNewerReleaseAsync(CancellationToken cancellationToken)
    {
        var manager = _manager ??= CreateManager();
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        var target = update.TargetFullRelease;
        if (manager.UpdatePendingRestart?.Version != target.Version)
        {
            await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken).ConfigureAwait(false);
        }

        return target.Version.ToString();
    }

    private UpdateManager CreateManager()
    {
        var feed = settings.FeedUrl ?? throw new InvalidOperationException("No update feed is configured.");
        IUpdateSource source = feed.IsFile ? new SimpleFileSource(new DirectoryInfo(feed.LocalPath))
            : settings.IsGitHubRepository ? new GithubSource(feed.AbsoluteUri.TrimEnd('/'), accessToken: null, prerelease: false)
            : new SimpleWebSource(feed);
        return new UpdateManager(source, new UpdateOptions { ExplicitChannel = settings.Channel });
    }
}

/// <summary>The folder layout the Velopack installer creates: <c>&lt;root&gt;\Update.exe</c> and <c>&lt;root&gt;\current\</c> with the app and <c>sq.version</c>.</summary>
public static class InstalledLayout
{
    public static bool IsInstalled(string appDirectory) =>
        File.Exists(Path.Combine(appDirectory, "sq.version")) && File.Exists(Path.Combine(appDirectory, "..", "Update.exe"));
}
