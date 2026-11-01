using Backbloom.App;
using Microsoft.Extensions.Configuration;

namespace Backbloom.Shell.Tests;

public sealed class CaptureOptionTests
{
    private static AppOptions Options(params (string Key, string Value)[] values) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build());

    [Fact]
    public void ScreenCapture_IsTheDefault()
    {
        var options = Options();
        Assert.Equal(CaptureKind.Screen, options.Capture);
        Assert.Null(options.CaptureImage);
    }

    [Fact]
    public void Synthetic_IsChosenWithCapture()
    {
        Assert.Equal(CaptureKind.Synthetic, Options(("capture", "synthetic")).Capture);
    }

    [Fact]
    public void APicture_IsTheFrameSource_EvenWithCaptureGiven()
    {
        var options = Options(("capture", "synthetic"), ("capture-image", "room.ppm"));
        Assert.Equal(CaptureKind.Image, options.Capture);
        Assert.Equal(Path.GetFullPath("room.ppm"), options.CaptureImage);
    }

    [Fact]
    public void CaptureImage_CantBeChosenWithoutAPicture()
    {
        Assert.Equal(CaptureKind.Screen, Options(("capture", "image")).Capture);
    }
}
