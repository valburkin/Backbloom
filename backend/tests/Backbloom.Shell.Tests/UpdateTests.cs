using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.App;
using Backbloom.App.Updates;

namespace Backbloom.Shell.Tests;

/// <summary>Automatic updates: settings, the check schedule (with a fake client: no network) and the installed layout.</summary>
public sealed class UpdateTests
{
    private static readonly Uri s_feed = new("https://updates.example.com/backbloom");

    private static UpdateSettings Settings(params (string Key, string Value)[] values) =>
        UpdateSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Settings_DefaultToTheGitHubReleases_AndNoneTurnsUpdatesOff()
    {
        var defaults = Settings();
        Assert.True(defaults.Enabled);
        Assert.True(defaults.IsGitHubRepository);
        Assert.Equal(new Uri(UpdateSettings.DefaultFeedUrl), defaults.FeedUrl);
        Assert.Null(defaults.Channel);
        Assert.False(Settings(("update-url", "none")).Enabled);
        Assert.False(Settings(("update-url", "NONE")).Enabled);
        Assert.False(Settings(("update-url", "not a url")).Enabled);
        Assert.False(Settings(("update-url", s_feed.AbsoluteUri)).IsGitHubRepository);
        Assert.False(Settings(("update-url", "http://github.com/someone/fork")).IsGitHubRepository);
        Assert.True(Settings(("update-url", "https://github.com/someone/fork/")).IsGitHubRepository);
        // a release download folder on GitHub is a plain feed, not a repository
        Assert.False(Settings(("update-url", "https://github.com/someone/fork/releases/download/v1.0.0")).IsGitHubRepository);
        Assert.False(Settings(("update-url", "https://github.com/someone")).IsGitHubRepository);
    }

    [Fact]
    public void Settings_FromOptionsOrEnvironment()
    {
        var options = Settings(("update-url", s_feed.AbsoluteUri), ("update-channel", "beta"));
        Assert.Equal(new UpdateSettings(s_feed, "beta"), options);

        // BACKBLOOM_UPDATE_URL / BACKBLOOM_UPDATE_CHANNEL arrive without the prefix
        var environment = Settings(("UPDATE_URL", "file:///C:/feed"), ("UPDATE_CHANNEL", "stable"));
        Assert.True(environment.Enabled);
        Assert.True(environment.FeedUrl!.IsFile);
        Assert.Equal("stable", environment.Channel);
    }

    [Fact]
    public void CommandLine_AcceptsTheUpdateOptions()
    {
        var (line, error) = CommandLine.Parse(["--update-url", s_feed.AbsoluteUri, "--update-channel=beta"]);
        Assert.Null(error);
        Assert.Equal(["--update-url", s_feed.AbsoluteUri, "--update-channel", "beta"], line!.ConfigurationArgs);
        Assert.Contains("absolute", CommandLine.Parse(["--update-url", "updates"]).Error, StringComparison.Ordinal);
        Assert.Null(CommandLine.Parse(["--update-url", "none"]).Error);
        Assert.Contains("--update-url", CommandLine.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_DoesNothing_WithoutAFeedOrWhenNotInstalled()
    {
        var notInstalled = new FakeClient { Installed = false };
        await RunAsync(new UpdateSettings(s_feed, null), notInstalled, advance: TimeSpan.FromDays(3));
        Assert.Equal(0, notInstalled.Checks);

        var noFeed = new FakeClient();
        await RunAsync(Settings(("update-url", "none")), noFeed, advance: TimeSpan.FromDays(3));
        Assert.Equal(0, noFeed.Checks);
    }

    [Fact]
    public async Task Service_ChecksAfterTheStartThenDaily_AndRetriesFailuresHourly()
    {
        var client = new FakeClient { Failures = 1, Release = "0.2.0" };
        var time = new WaitableTimeProvider();
        using var service = new UpdateService(new UpdateSettings(s_feed, null), client, time, NullLogger<UpdateService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await time.AdvanceWhenWaitingAsync(UpdateService.FirstCheckDelay - TimeSpan.FromSeconds(1), waits: 1);
        Assert.Equal(0, client.Checks);
        await time.AdvanceWhenWaitingAsync(TimeSpan.FromSeconds(1), waits: 1);
        await time.AdvanceWhenWaitingAsync(TimeSpan.Zero, waits: 2);
        Assert.Equal(1, client.Checks); // failed

        await time.AdvanceWhenWaitingAsync(UpdateService.RetryDelay, waits: 2);
        await time.AdvanceWhenWaitingAsync(TimeSpan.Zero, waits: 3);
        Assert.Equal(2, client.Checks); // downloaded

        await time.AdvanceWhenWaitingAsync(UpdateService.CheckInterval - TimeSpan.FromMinutes(1), waits: 3);
        Assert.Equal(2, client.Checks);
        await time.AdvanceWhenWaitingAsync(TimeSpan.FromMinutes(1), waits: 3);
        await time.AdvanceWhenWaitingAsync(TimeSpan.Zero, waits: 4);
        Assert.Equal(3, client.Checks);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void InstalledLayout_NeedsUpdateExeAboveAndTheManifest()
    {
        var root = Directory.CreateTempSubdirectory("backbloom-layout-");
        try
        {
            var current = root.CreateSubdirectory("current");
            Assert.False(InstalledLayout.IsInstalled(current.FullName));
            File.WriteAllText(Path.Combine(current.FullName, "sq.version"), "<package />");
            Assert.False(InstalledLayout.IsInstalled(current.FullName));
            File.WriteAllBytes(Path.Combine(root.FullName, "Update.exe"), []);
            Assert.True(InstalledLayout.IsInstalled(current.FullName));
            Assert.False(InstalledLayout.IsInstalled(AppContext.BaseDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunAsync(UpdateSettings settings, FakeClient client, TimeSpan advance)
    {
        var time = new WaitableTimeProvider();
        using var service = new UpdateService(settings, client, time, NullLogger<UpdateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(advance);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, time.Waits);
    }

    /// <summary>A fake clock that counts the waits started on it, so a test can advance it once the service waits.</summary>
    private sealed class WaitableTimeProvider : FakeTimeProvider
    {
        private int _waits;

        public int Waits => Volatile.Read(ref _waits);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // counted once the timer is scheduled, so an Advance after the count reaches it
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _waits);
            return timer;
        }

        /// <summary>Waits until <paramref name="waits"/> waits were started, then advances by <paramref name="by"/>.</summary>
        public async Task AdvanceWhenWaitingAsync(TimeSpan by, int waits)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Waits < waits)
            {
                Assert.True(DateTime.UtcNow < deadline, $"The service didn't start wait {waits} ({Waits} so far).");
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }

            Advance(by);
        }
    }

    private sealed class FakeClient : IUpdateClient
    {
        public bool Installed { get; init; } = true;

        public int Failures { get; set; }

        public string? Release { get; init; }

        public int Checks { get; private set; }

        public bool IsInstalled => Installed;

        public Task<string?> DownloadNewerReleaseAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (Failures > 0)
            {
                Failures--;
                throw new HttpRequestException("feed unreachable");
            }

            return Task.FromResult(Release);
        }
    }
}
