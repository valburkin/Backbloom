namespace Backbloom.Engine.Platform;

/// <summary>A monitor attached to the desktop and its physical size.</summary>
/// <param name="Name">The monitor's own name (from its EDID, e.g. "DELL U2720Q"), else the system's name for it.</param>
/// <param name="Device">The system's name for the output it is shown on (Windows: "\\.\DISPLAY1").</param>
/// <param name="DevicePath">A stable id of the monitor (Windows: the display configuration's device path; empty when unknown).</param>
/// <param name="Primary">The main display (the one at the desktop's origin).</param>
/// <param name="WidthMm">Width of the picture in millimeters, null when the monitor doesn't report it.</param>
/// <param name="HeightMm">Height of the picture in millimeters, null when the monitor doesn't report it.</param>
public sealed record DisplayInfo(string Name, string Device, string DevicePath, bool Primary, int? WidthMm, int? HeightMm);

/// <summary>
/// The monitors attached to the desktop with their physical size (the guided setup's strip plan). Platform projects
/// implement it with read-only system calls; <see cref="NoDisplayList"/> elsewhere.
/// </summary>
public interface IDisplayList
{
    IReadOnlyList<DisplayInfo> GetDisplays();
}

/// <summary>Platforms without an implementation: no monitors are known (the UI asks for the screen size).</summary>
public sealed class NoDisplayList : IDisplayList
{
    public IReadOnlyList<DisplayInfo> GetDisplays() => [];
}

/// <summary>
/// Reads the physical picture size from an EDID block (VESA E-EDID 1.3/1.4): the first detailed timing descriptor's
/// size in millimeters (bytes 66-68), else the basic display parameters' size in centimeters (bytes 21-22). Zero sizes
/// (projectors, monitors that don't report it) and sizes that make no sense are null.
/// </summary>
public static class Edid
{
    private const int BlockSize = 128;
    private const int MaxMm = 4095;

    private static ReadOnlySpan<byte> Header => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static (int? WidthMm, int? HeightMm) PhysicalSize(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < BlockSize || !edid[..8].SequenceEqual(Header))
        {
            return (null, null);
        }

        // detailed timing descriptor 1 (bytes 54-71): a pixel clock of 0 marks a display descriptor instead
        if (edid[54] != 0 || edid[55] != 0)
        {
            var width = edid[66] | ((edid[68] & 0xF0) << 4);
            var height = edid[67] | ((edid[68] & 0x0F) << 8);

            // some monitors put the aspect ratio here (16 x 9 mm) or the size in cm: only plausible values count
            if (width >= 50 && height >= 50 && width <= MaxMm && height <= MaxMm)
            {
                return (width, height);
            }
        }

        // basic display parameters: 0 means unknown (or, for one of them, an aspect ratio)
        if (edid[21] > 0 && edid[22] > 0)
        {
            return (edid[21] * 10, edid[22] * 10);
        }

        return (null, null);
    }
}
