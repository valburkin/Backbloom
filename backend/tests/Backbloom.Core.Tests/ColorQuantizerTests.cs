using System.Numerics;
using Backbloom.Core.Color;

namespace Backbloom.Core.Tests;

public sealed class ColorQuantizerTests
{
    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(1.0f, 255)]
    [InlineData(0.5f, 128)]
    [InlineData(-0.2f, 0)]
    [InlineData(1.7f, 255)]
    [InlineData(1.0f / 255.0f, 1)]
    public void ToByte_RoundsHalfAwayFromZeroAndClamps(float value, byte expected) => Assert.Equal(expected, ColorQuantizer.ToByte(value));

    [Theory]
    [InlineData(ColorOrder.Rgb, new byte[] { 10, 20, 30 })]
    [InlineData(ColorOrder.Bgr, new byte[] { 30, 20, 10 })]
    [InlineData(ColorOrder.Rbg, new byte[] { 10, 30, 20 })]
    [InlineData(ColorOrder.Grb, new byte[] { 20, 10, 30 })]
    [InlineData(ColorOrder.Gbr, new byte[] { 20, 30, 10 })]
    [InlineData(ColorOrder.Brg, new byte[] { 30, 10, 20 })]
    public void ToBytes_WritesTheControllersOrder(ColorOrder order, byte[] expected)
    {
        var colors = new[] { new Vector3(10, 20, 30) / 255.0f, Vector3.One };
        var bytes = new byte[6];

        ColorQuantizer.ToBytes(colors, order, bytes);

        Assert.Equal(expected, bytes[..3]);
        Assert.Equal(new byte[] { 255, 255, 255 }, bytes[3..]);
    }

    [Theory]
    [InlineData("rgb", ColorOrder.Rgb)]
    [InlineData("grb", ColorOrder.Grb)]
    [InlineData("bgr", ColorOrder.Bgr)]
    [InlineData("nonsense", ColorOrder.Rgb)]
    [InlineData(null, ColorOrder.Rgb)]
    public void ColorOrders_ParseLikeTheCppCode(string? value, ColorOrder expected) => Assert.Equal(expected, ColorOrders.Parse(value));
}
