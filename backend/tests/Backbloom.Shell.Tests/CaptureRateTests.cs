using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.App;
using Backbloom.Capture;
using Backbloom.Core.Smoothing;
using Backbloom.Settings;
using Backbloom.Settings.Storage;

namespace Backbloom.Shell.Tests;

/// <summary>The screen capture's rate follows the smoothing that applies (<see cref="CaptureRateFollower"/>).</summary>
public sealed class CaptureRateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-capture-rate-{Guid.NewGuid():N}");
    private readonly SettingsDatabase _database;
    private readonly SqliteSettingsStore _store;

    public CaptureRateTests()
    {
        Directory.CreateDirectory(_dir);
        _database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _store = new SqliteSettingsStore(_database, SettingsSchema.CreateBuiltin(new Dictionary<string, JsonObject>()), NullLogger<SqliteSettingsStore>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SmoothingSettings Smoothing(bool enable, int timeMs) =>
        SmoothingSettings.FromJson(new JsonObject { ["enable"] = enable, ["time_ms"] = (double)timeMs });

    private void SaveFps(int fps)
    {
        var grabber = _store.GetSection(CaptureSettings.SectionName)!.AsObject();
        grabber["fps"] = fps;
        Assert.True(_store.Update(new JsonObject { [CaptureSettings.SectionName] = grabber }).Success);
    }

    private void SaveSmoothing(bool enable, int timeMs)
    {
        var smoothing = _store.GetSection("smoothing")!.AsObject();
        smoothing["enable"] = enable;
        smoothing["time_ms"] = timeMs;
        Assert.True(_store.Update(new JsonObject { ["smoothing"] = smoothing }).Success);
    }

    [Fact]
    public void TheCapture_ReadsAtLeast60TimesASecond_WhileTheAppliedTransitionsAreFast()
    {
        SaveFps(30);
        var follower = new CaptureRateFollower();
        var configured = new List<int>();
        follower.Follow(_store, c => configured.Add(c.FramesPerSecond));
        Assert.Equal(30, follower.SettingsFrom(_store).FramesPerSecond);

        // a profile's fast Look (Intense: 150 ms), then the saved Natural (350 ms) again
        follower.SetSmoothing(Smoothing(true, 150));
        follower.SetSmoothing(Smoothing(true, 150));
        follower.SetSmoothing(Smoothing(true, 299));
        follower.SetSmoothing(Smoothing(true, 300));
        Assert.Equal([60, 30], configured);

        // transitions off: the saved rate, however short the time
        follower.SetSmoothing(Smoothing(false, 100));
        Assert.Equal([60, 30], configured);

        // a faster saved rate stays; a changed rate applies at once
        follower.SetSmoothing(Smoothing(true, 150));
        SaveFps(90);
        SaveFps(45);
        follower.SetSmoothing(Smoothing(true, 350));
        Assert.Equal([60, 30, 60, 90, 60, 45], configured);
    }

    [Fact]
    public void BeforeTheEngineReports_TheSavedSmoothingDecides()
    {
        SaveFps(30);
        SaveSmoothing(enable: true, timeMs: 200);
        Assert.Equal(60, new CaptureRateFollower().SettingsFrom(_store).FramesPerSecond);

        SaveSmoothing(enable: false, timeMs: 200);
        Assert.Equal(30, new CaptureRateFollower().SettingsFrom(_store).FramesPerSecond);
    }
}
