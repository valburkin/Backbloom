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
