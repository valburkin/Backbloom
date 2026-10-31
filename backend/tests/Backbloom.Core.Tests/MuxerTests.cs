using Backbloom.Core.Color;
using Backbloom.Core.Priorities;

namespace Backbloom.Core.Tests;

public sealed class MuxerTests
{
    private static readonly Rgb8 s_red = new(255, 0, 0);

    [Fact]
    public void Nothing_IsTheBlackLowestPriority()
    {
        var muxer = new Muxer();
        Assert.Equal(Muxer.LowestPriority, muxer.CurrentPriority);
        Assert.Equal(SourceComponent.Color, muxer.Visible.Component);
        Assert.Equal(Rgb8.Black, muxer.Visible.StaticColor);
        Assert.Null(muxer.Clear(Muxer.LowestPriority, 0));
    }

    [Fact]
    public void RegisteredSources_AreInactiveUntilTheyDeliver_AndTheLowestNumberWins()
    {
        var muxer = new Muxer();
        muxer.Register(245, SourceComponent.SystemGrabber, "System", "DirectX11", Rgb8.Black, 0);
        Assert.Equal(Muxer.LowestPriority, muxer.CurrentPriority);
        Assert.False(muxer.Get(245).Active);

        var changes = muxer.SetInput(245, Muxer.NoTimeout, 0);
        Assert.Equal(MuxerChanges.Priorities | MuxerChanges.Visible, changes);
        Assert.Equal(245, muxer.CurrentPriority);

        muxer.Register(1, SourceComponent.Color, "UI", null, s_red, 0);
        muxer.SetInput(1, Muxer.NoTimeout, 0);
        Assert.Equal(1, muxer.CurrentPriority);
        Assert.Equal(245, muxer.PreviousPriority);

        Assert.True((muxer.SetInactive(1, 10)! & MuxerChanges.Visible) != 0);
        Assert.Equal(245, muxer.CurrentPriority);
        Assert.Null(muxer.SetInput(7, Muxer.NoTimeout, 0));
    }

    [Fact]
    public void TimedSources_Expire()
    {
        var muxer = new Muxer();
        muxer.Register(1, SourceComponent.Color, "UI", null, s_red, 0);
        muxer.SetInput(1, 500, 1000);
        Assert.Equal(1500, muxer.Get(1).TimeoutMs);
        Assert.True(muxer.HasTimedInputs(1200));
        Assert.Equal(MuxerChanges.None, muxer.Update(1499));
        Assert.Equal(MuxerChanges.Priorities | MuxerChanges.Visible, muxer.Update(1500));
        Assert.False(muxer.Has(1));
        Assert.Equal(Muxer.LowestPriority, muxer.CurrentPriority);
    }

    [Fact]
    public void ManualSelection_WinsUntilTheSourceIsGone()
    {
        var muxer = new Muxer();
        muxer.Register(1, SourceComponent.Color, "UI", null, s_red, 0);
        muxer.SetInput(1, Muxer.NoTimeout, 0);
        muxer.Register(245, SourceComponent.SystemGrabber, "System", null, Rgb8.Black, 0);
        muxer.SetInput(245, Muxer.NoTimeout, 0);

        Assert.True(muxer.Select(245, 0, out _));
        Assert.False(muxer.AutoSelect);
        Assert.Equal(245, muxer.CurrentPriority);
        Assert.False(muxer.Select(3, 0, out _));

        muxer.Clear(245, 0);
        Assert.True(muxer.AutoSelect);
        Assert.Equal(1, muxer.CurrentPriority);

        // switching to manual needs a selected source that still exists
        Assert.False(muxer.SetAutoSelect(false, 0, out _));
    }

    [Fact]
    public void ClearAll_RemovesColorsAndEffectsButNotTheScreen()
    {
        var muxer = new Muxer();
        muxer.Register(1, SourceComponent.Color, "UI", null, s_red, 0);
        muxer.Register(2, SourceComponent.Effect, "UI", "Rainbow swirl", Rgb8.Black, 3);
        muxer.Register(245, SourceComponent.SystemGrabber, "System", null, Rgb8.Black, 0);
        muxer.SetInput(1, Muxer.NoTimeout, 0);
        muxer.SetInput(245, Muxer.NoTimeout, 0);

        muxer.ClearAll(0);
        Assert.Equal([245, 255], muxer.Inputs.Select(i => i.Priority));
        Assert.Equal(245, muxer.CurrentPriority);

        muxer.ClearAll(0, force: true);
        Assert.Equal([255], muxer.Inputs.Select(i => i.Priority));
    }

    [Fact]
    public void ScreenOnly_HoldsColorsAndEffects_UntilTurnedOff()
    {
        var muxer = new Muxer();
        muxer.Register(245, SourceComponent.SystemGrabber, "System", "DirectX11", Rgb8.Black, 0);
        muxer.SetInput(245, Muxer.NoTimeout, 0);
        muxer.Register(1, SourceComponent.Color, "UI", null, s_red, 0);
        muxer.SetInput(1, Muxer.NoTimeout, 0);
        muxer.Register(64, SourceComponent.Effect, "UI", "Rainbow swirl", Rgb8.Black, 0);
        muxer.SetInput(64, 5000, 0);
        Assert.True(muxer.Select(64, 0, out _));
        Assert.Equal(64, muxer.CurrentPriority);

        Assert.Equal(MuxerChanges.Priorities | MuxerChanges.Visible, muxer.SetScreenOnly(true, 10));
        Assert.Equal(245, muxer.CurrentPriority);
        Assert.Equal(MuxerChanges.None, muxer.SetScreenOnly(true, 10));
        Assert.False(muxer.AutoSelect);

        // held sources still expire
        muxer.Update(6000);
        Assert.False(muxer.Has(64));
        muxer.SetScreenOnly(false, 6000);
        Assert.Equal(1, muxer.CurrentPriority);
    }
}
