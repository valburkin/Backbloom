using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Core.Color;

namespace Backbloom.Devices.Tests;

public sealed class LedDeviceRunnerTests
{
    private static readonly RetryPolicy s_fastRetry = new(5, TimeSpan.FromMilliseconds(10));

    private static (LedDeviceRunner Runner, ConcurrentQueue<DeviceState> States) Create(
        FakeDriver driver, int leds = 2, ColorOrder order = ColorOrder.Rgb, TimeSpan? refresh = null)
    {
        var runner = new LedDeviceRunner(driver, new LedDeviceRunnerOptions("test", leds, order, refresh), NullLogger.Instance);
        var states = new ConcurrentQueue<DeviceState>();
        runner.StateChanged += (_, s) => states.Enqueue(s);
        runner.Start();
        return (runner, states);
    }

    [Fact]
    public void StateMachine_AllowsOnlyTheDocumentedTransitions()
    {
        Assert.True(DeviceStateMachine.IsAllowed(DeviceState.Off, DeviceState.Opening));
        Assert.True(DeviceStateMachine.IsAllowed(DeviceState.Opening, DeviceState.Ready));
        Assert.True(DeviceStateMachine.IsAllowed(DeviceState.Ready, DeviceState.Closing));
        Assert.True(DeviceStateMachine.IsAllowed(DeviceState.Closing, DeviceState.Off));
        Assert.False(DeviceStateMachine.IsAllowed(DeviceState.Off, DeviceState.Ready));
        Assert.False(DeviceStateMachine.IsAllowed(DeviceState.Ready, DeviceState.Off));
        Assert.False(DeviceStateMachine.IsAllowed(DeviceState.Failed, DeviceState.Ready));

        var machine = new DeviceStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.MoveTo(DeviceState.Closing));
        Assert.Equal(DeviceState.Off, machine.State);
    }

    [Fact]
    public void RetryPolicy_GivesUpAfterMaxRetries()
    {
        var policy = new RetryPolicy(2, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(2));
        Assert.Null(policy.NextDelay(3));
        Assert.Null(RetryPolicy.None.NextDelay(1));
    }

    [Fact]
    public void BlinkPattern_IsRedGreenBlueThenOver()
    {
        Assert.Equal(new Vector3(1, 0, 0), BlinkPattern.ColorAt(TimeSpan.Zero));
        Assert.Equal(new Vector3(1, 0, 0), BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(1499)));
        Assert.Equal(new Vector3(0, 1, 0), BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal(new Vector3(0, 0, 1), BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(4499)));
        Assert.Null(BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(4500)));

        // held: the cycle repeats until the timeout
        Assert.Equal(new Vector3(1, 0, 0), BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(4500), hold: true));
        Assert.Equal(new Vector3(0, 1, 0), BlinkPattern.ColorAt(TimeSpan.FromMilliseconds(7400), hold: true));
        Assert.Equal(new Vector3(0, 1, 0), BlinkPattern.ColorAt(TimeSpan.FromSeconds(120) - TimeSpan.FromTicks(1), hold: true));
        Assert.Null(BlinkPattern.ColorAt(TimeSpan.FromSeconds(120), hold: true));
    }

    [Fact]
    public void Runner_RetriesUntilTheDeviceOpens()
    {
        var driver = new FakeDriver(s_fastRetry, failOpens: 2);
        var (runner, states) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
        }

        Assert.Equal(3, driver.Opens);
        Assert.Equal(
        [
            DeviceState.Opening, DeviceState.WaitingToRetry, DeviceState.Opening, DeviceState.WaitingToRetry,
            DeviceState.Opening, DeviceState.Ready, DeviceState.Closing, DeviceState.Off,
        ], states);
        Assert.True(driver.Disposed);
    }

    [Fact]
    public void Runner_FailsAfterTheRetryBudgetAndTriesAgainWhenReenabled()
    {
        var driver = new FakeDriver(new RetryPolicy(1, TimeSpan.FromMilliseconds(5)), failOpens: 2);
        var (runner, _) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Failed);
            Assert.Equal(2, driver.Opens);

            runner.SetEnabled(false);
            Wait.Until(() => runner.State == DeviceState.Off);
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            Assert.Equal(3, driver.Opens);
        }
    }

    [Fact]
    public void RetryPolicy_KeepsTryingSlowlyAfterTheQuickRetries()
    {
        var policy = new RetryPolicy(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay(1000));
        Assert.True(policy.SlowsDownAfter(3));
        Assert.False(policy.SlowsDownAfter(4));
        Assert.Equal(RetryPolicy.KeepTrying, RetryPolicy.Default.NextDelay(61));
    }

    [Fact]
    public void Runner_KeepsTryingAfterTheQuickRetries()
    {
        var driver = new FakeDriver(new RetryPolicy(1, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)), failOpens: 4);
        var (runner, states) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
        }

        Assert.Equal(5, driver.Opens);
        Assert.DoesNotContain(DeviceState.Failed, states);
    }

    [Fact]
    public void Runner_SwitchedOffWithADelay_StaysConnectedShowingBlack_AndStreamsAgainWhenSwitchedOn()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([new Vector3(1, 0, 0), new Vector3(1, 0, 0)]);
            Wait.Until(() => !driver.Frames.IsEmpty);

            runner.SetEnabled(false, TimeSpan.FromSeconds(30));
            Wait.Until(() => driver.Frames.Count == 2);
            Assert.Equal(new byte[6], driver.Frames.Last());

            runner.SetEnabled(true);
            runner.Submit([new Vector3(0, 1, 0), new Vector3(0, 1, 0)]);
            Wait.Until(() => driver.Frames.Count == 3);
            Assert.Equal(new byte[] { 0, 255, 0, 0, 255, 0 }, driver.Frames.Last());
            Assert.Equal(DeviceState.Ready, runner.State);
            Assert.Equal((1, 0), (driver.Opens, driver.Closes));

            // without a delay it closes at once, also while waiting for one
            runner.SetEnabled(false, TimeSpan.FromSeconds(30));
            runner.SetEnabled(false);
            Wait.Until(() => runner.State == DeviceState.Off);
            Assert.Equal(1, driver.Closes);
        }
    }

    [Fact]
    public void Runner_SwitchedOffWithADelay_ClosesOnceTheDelayIsOver()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, states) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.SetEnabled(false, TimeSpan.FromMilliseconds(100));
            Assert.Equal(DeviceState.Ready, runner.State);

            // a second switch-off with a delay doesn't move the end of the first one
            runner.SetEnabled(false, TimeSpan.FromMinutes(5));
            Wait.Until(() => runner.State == DeviceState.Off);
        }

        Assert.Equal(1, driver.Closes);
        Assert.Equal([DeviceState.Opening, DeviceState.Ready, DeviceState.Closing, DeviceState.Off], states);
    }

    [Fact]
    public void Runner_SendsFramesInTheControllerOrderAndBlackBeforeClosing()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver, order: ColorOrder.Grb);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([new Vector3(1, 0, 0), new Vector3(0, 0, 1)]);
            Wait.Until(() => !driver.Frames.IsEmpty);

            runner.SetEnabled(false);
            Wait.Until(() => runner.State == DeviceState.Off);
        }

        var frames = driver.Frames.ToArray();
        Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255 }, frames[0]);
        Assert.Equal(new byte[6], frames[^1]);
        Assert.Equal(1, driver.Closes);
        Assert.Equal(1, runner.Stats.Submitted);
    }

    [Fact]
    public void Runner_CountsDropsOnlyWhileReady()
    {
        var driver = new FakeDriver(new RetryPolicy(5, TimeSpan.FromSeconds(30)), failOpens: 1);
        var (runner, _) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.WaitingToRetry);
            for (var i = 0; i < 20; i++)
            {
                runner.Submit([Vector3.One, Vector3.One]);
            }

            // nothing is taken while it waits to retry: that isn't falling behind
            Assert.Equal((20, 0), (runner.Stats.Submitted, runner.Stats.Dropped));
        }
    }

    [Fact]
    public void Runner_MeasuresTheLoadOfEachSentFrame_AverageAndPeak()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);

            // full white on one LED, black on the other: half of full white; then a quarter, red only on one LED
            runner.Submit([Vector3.One, Vector3.Zero]);
            Wait.Until(() => runner.Stats.Written == 1);
            runner.Submit([new Vector3(0.75f, 0, 0), new Vector3(0, 0, 0.75f)]);
            Wait.Until(() => runner.Stats.Written == 2);

            var stats = runner.Stats.Snapshot();
            Assert.Equal(2, stats.LoadFrames);
            Assert.Equal(0.375, stats.AverageLoadSince(default)!.Value, 6);
            var rgb = stats.AverageChannelsSince(default)!.Value;
            Assert.Equal((0.4375f, 0.25f, 0.4375f), (rgb.X, rgb.Y, rgb.Z));
            Assert.Equal(2, stats.WriteCalls);
            Assert.True(stats.AverageWriteMsSince(default) >= 0);
            var peaks = runner.Stats.TakePeaks();
            Assert.Equal((0.5, 1.0), (Math.Round(peaks.Load, 6), Math.Round(peaks.Led, 6)));
            Assert.True(peaks.WriteMs >= 0);
            Assert.Equal(default, runner.Stats.TakePeaks());
            Assert.Null(stats.AverageLoadSince(stats));

            // colors over 1 count as full
            runner.Submit([new Vector3(2), new Vector3(2)]);
            Wait.Until(() => runner.Stats.Written == 3);
            Assert.Equal(1, runner.Stats.Snapshot().AverageLoadSince(stats)!.Value, 6);
        }
    }

    [Fact]
    public void Runner_IdentifyBlinksOneLedOverTheFrame()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver, leds: 3);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([Vector3.One, Vector3.One, Vector3.One]);
            runner.Identify(1);
            Wait.Until(() => driver.Frames.Count >= 3);
        }

        Assert.Contains(driver.Frames, f => f.SequenceEqual(new byte[] { 0, 0, 0, 255, 0, 0, 0, 0, 0 }));
    }

    private static readonly byte[] s_white = [255, 255, 255, 255, 255, 255, 255, 255, 255];

    // LED `index` of 3 in `color` (r, g, b of 255), the others black
    private static byte[] Blink(int index, int r, int g, int b)
    {
        var frame = new byte[9];
        (frame[index * 3], frame[(index * 3) + 1], frame[(index * 3) + 2]) = ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        return frame;
    }

    private static void WaitForFrame(FakeDriver driver, byte[] expected) =>
        Wait.Until(() => driver.Frames.LastOrDefault() is { } last && last.SequenceEqual(expected));

    private static (LedDeviceRunner Runner, ManualTime Time) CreateTimed(FakeDriver driver)
    {
        var time = new ManualTime();
        var runner = new LedDeviceRunner(driver, new LedDeviceRunnerOptions("test", 3), NullLogger.Instance, time);
        runner.Start();
        return (runner, time);
    }

    [Fact]
    public void Runner_IdentifyWhileSwitchedOff_OpensTheDeviceForTheBlinkAndClosesItAfter()
    {
        // "Find an LED" while the lights are off (switched off, held output, nothing visible) used to be accepted
        // but never shown: the runner only streamed while enabled
        var driver = new FakeDriver(s_fastRetry);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.Identify(1);
            WaitForFrame(driver, Blink(1, 1, 0, 0));
            Assert.Equal(DeviceState.Ready, runner.State);

            time.Advance(BlinkPattern.TotalDuration);
            Wait.Until(() => runner.State == DeviceState.Off);
            Assert.Equal(new byte[9], driver.Frames.Last());
            Assert.Equal(1, driver.Closes);
            Assert.False(runner.Blinking);
        }
    }

    [Fact]
    public void Runner_IdentifyWhileSwitchedOff_StopsTryingToOpenOnceTheBlinkIsOver()
    {
        // the blink only ended when a frame was drawn, so a device that couldn't open kept being retried (and then
        // stayed Failed) while switched off, long after the blink would have ended
        var driver = new FakeDriver(new RetryPolicy(1000, TimeSpan.FromMilliseconds(5)), failOpens: int.MaxValue);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.Identify(1);
            Wait.Until(() => driver.Opens >= 2);

            time.Advance(BlinkPattern.TotalDuration);
            Wait.Until(() => runner.State == DeviceState.Off);
            Assert.False(runner.Blinking);
        }
    }

    [Fact]
    public void Runner_IdentifyWhileSwitchedOff_LeavesFailedOnceTheBlinkIsOver()
    {
        var driver = new FakeDriver(RetryPolicy.None, failOpens: int.MaxValue);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.Identify(1);
            Wait.Until(() => runner.State == DeviceState.Failed);

            time.Advance(BlinkPattern.TotalDuration);
            Wait.Until(() => runner.State == DeviceState.Off);
            Assert.Equal(1, driver.Opens);
        }
    }

    [Fact]
    public void Runner_TheSameLedBlinksAgain_AfterItsBlinkEndedAndWhileItRuns()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([Vector3.One, Vector3.One, Vector3.One]);
            runner.Identify(1);
            WaitForFrame(driver, Blink(1, 1, 0, 0));
            time.Advance(BlinkPattern.TotalDuration);
            WaitForFrame(driver, s_white);

            // again, after the first blink is over: the frames stopped (nothing new was submitted)
            runner.Identify(1);
            WaitForFrame(driver, Blink(1, 1, 0, 0));

            // and again while it is green: it starts over at red
            time.Advance(BlinkPattern.PhaseDuration);
            WaitForFrame(driver, Blink(1, 0, 1, 0));
            runner.Identify(1);
            WaitForFrame(driver, Blink(1, 1, 0, 0));
        }
    }

    [Fact]
    public void Runner_AHeldBlinkCyclesUntilStopped_ThenTheFrameShowsAgain()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([Vector3.One, Vector3.One, Vector3.One]);
            runner.Identify(2, hold: true);
            WaitForFrame(driver, Blink(2, 1, 0, 0));
            time.Advance(BlinkPattern.PhaseDuration);
            WaitForFrame(driver, Blink(2, 0, 1, 0));
            time.Advance(BlinkPattern.PhaseDuration);
            WaitForFrame(driver, Blink(2, 0, 0, 1));

            // where an ordinary blink ends, a held one starts over
            time.Advance(BlinkPattern.PhaseDuration);
            WaitForFrame(driver, Blink(2, 1, 0, 0));

            // stopped: the last frame shows again although no new one arrives
            runner.StopIdentify();
            WaitForFrame(driver, s_white);
            Assert.False(runner.Blinking);
        }
    }

    [Fact]
    public void Runner_AHeldBlinkEndsAfterTheTimeout_OrWhenAnotherLedBlinks()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, time) = CreateTimed(driver);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([Vector3.One, Vector3.One, Vector3.One]);
            runner.Identify(0, hold: true);
            WaitForFrame(driver, Blink(0, 1, 0, 0));
            time.Advance(BlinkPattern.HoldTimeout - TimeSpan.FromSeconds(1));
            WaitForFrame(driver, Blink(0, 0, 1, 0));
            time.Advance(TimeSpan.FromSeconds(1));
            WaitForFrame(driver, s_white);
            Assert.False(runner.Blinking);

            // another blink (not held) replaces a held one, and ends as usual
            runner.Identify(0, hold: true);
            WaitForFrame(driver, Blink(0, 1, 0, 0));
            runner.Identify(1);
            WaitForFrame(driver, Blink(1, 1, 0, 0));
            time.Advance(BlinkPattern.TotalDuration);
            WaitForFrame(driver, s_white);
        }
    }

    [Fact]
    public void Runner_AHeldBlinkDoesNotAllocatePerFrame()
    {
        var driver = new AllocationProbeDriver();
        var runner = new LedDeviceRunner(driver, new LedDeviceRunnerOptions("probe", 168), NullLogger.Instance);
        runner.Start();
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Identify(5, hold: true);

            // each write records the device thread's allocated bytes; the blink ticks every 40 ms
            Wait.Until(() => driver.Writes >= AllocationProbeDriver.Samples, 10_000);
        }

        Assert.Equal(0, driver.Allocated[^1] - driver.Allocated[AllocationProbeDriver.Samples / 2]);
    }

    [Fact]
    public void Runner_RefreshResendsTheLastFrame()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver, refresh: TimeSpan.FromMilliseconds(10));
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([new Vector3(0.5f), Vector3.Zero]);
            Wait.Until(() => runner.Stats.Written >= 4);
        }

        Assert.Equal(1, runner.Stats.Submitted);
    }

    [Fact]
    public void Runner_SubmitDoesNotAllocate()
    {
        var driver = new FakeDriver(s_fastRetry);
        var (runner, _) = Create(driver, leds: 168);
        using (runner)
        {
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            var colors = new Vector3[168];
            runner.Submit(colors);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                runner.Submit(colors);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
