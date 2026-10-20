using System.Text.Json.Nodes;
using Backbloom.Core.Imaging;

namespace Backbloom.Core.Borders;

/// <summary>
/// The <c>blackborderdetector</c> setting (minus <c>enable</c>, which decides whether a processor runs at all).
/// Defaults follow <c>BlackBorderProcessor::handleSettingsUpdate</c> (sources/blackborder/BlackBorderProcessor.cpp).
/// </summary>
/// <param name="ThresholdPercent">A pixel is black when all channels are below this share of full scale.</param>
/// <param name="UnknownFrameCount">Consistent "unknown" frames before the border is reset.</param>
/// <param name="BorderFrameCount">Consistent frames before a changed border is applied.</param>
/// <param name="MaxInconsistentCount">Deviating frames ignored before a new candidate border is considered.</param>
/// <param name="BlurRemoveCount">Extra rows/columns added to a detected border (blurred edges).</param>
/// <param name="Mode">Detection mode.</param>
public sealed record BlackBorderOptions(
    double ThresholdPercent = 5.0,
    int UnknownFrameCount = 600,
    int BorderFrameCount = 50,
    int MaxInconsistentCount = 10,
    int BlurRemoveCount = 1,
    BlackBorderMode Mode = BlackBorderMode.Default)
{
    public static BlackBorderOptions Default { get; } = new();

    public static BlackBorderOptions FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return Default;
        }

        return new BlackBorderOptions(
            ReadDouble(o, "threshold", 5.0),
            ReadInt(o, "unknownFrameCnt", 600),
            ReadInt(o, "borderFrameCnt", 50),
            ReadInt(o, "maxInconsistentCnt", 10),
            ReadInt(o, "blurRemoveCnt", 1),
            BlackBorderModes.Parse(o["mode"] is JsonValue v && v.TryGetValue(out string? s) ? s : null));
    }

    private static double ReadDouble(JsonObject o, string name, double fallback) =>
        o[name] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    private static int ReadInt(JsonObject o, string name, int fallback) =>
        o[name] is JsonValue v && v.TryGetValue(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : fallback;
}

/// <summary>
/// Detects the border of every frame and decides when the border the LED mapping uses changes: small deviations are
/// ignored for a few frames, a new border must be seen consistently for <see cref="BlackBorderOptions.BorderFrameCount"/>
/// frames (or <see cref="BlackBorderOptions.UnknownFrameCount"/> for "unknown"). Port of the detection part of
/// <c>backbloom::BlackBorderProcessor</c>; switching the component on and off is the caller's job (while it is off the
/// mapping uses no border, as in <c>ImageToLedManager::verifyBorder</c>).
/// </summary>
/// <remarks>Meant for the single frame-loop thread; does not allocate per frame.</remarks>
public sealed class BlackBorderProcessor
{
    private readonly BlackBorderDetector _detector;
    private BlackBorder _previousDetected = BlackBorder.Unknown;
    private int _consistentCount;
    private int _inconsistentCount = 10;

    public BlackBorderProcessor(BlackBorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _detector = new BlackBorderDetector(options.ThresholdPercent / 100.0);
    }

    public BlackBorderOptions Options { get; }

    /// <summary>The border the mapping should use; <see cref="BlackBorder.Unknown"/> until one was detected.</summary>
    public BlackBorder CurrentBorder { get; private set; } = BlackBorder.Unknown;

    /// <summary>Analyzes one frame; returns true when <see cref="CurrentBorder"/> changed.</summary>
    public bool Process(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Process(frame.ReadOnlyPixels, frame.Width, frame.Height);
    }

    /// <inheritdoc cref="Process(Frame)"/>
    public bool Process(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var detected = _detector.Process(bgra, width, height, Options.Mode);
        var horizontal = detected.HorizontalSize > 0 ? detected.HorizontalSize + Options.BlurRemoveCount : detected.HorizontalSize;
        var vertical = detected.VerticalSize > 0 ? detected.VerticalSize + Options.BlurRemoveCount : detected.VerticalSize;
        return UpdateBorder(new BlackBorder(detected.IsUnknown, horizontal, vertical));
    }

    // BlackBorderProcessor::updateBorder
    private bool UpdateBorder(BlackBorder detected)
    {
        if (detected == _previousDetected)
        {
            _consistentCount++;
            _inconsistentCount = 0;
        }
        else
        {
            _inconsistentCount++;
            if (_inconsistentCount <= Options.MaxInconsistentCount)
            {
                // a few deviating frames: keep counting for the previous border
                return false;
            }

            // the deviation lasts: give the new border a chance to prove that it is consistent
            _previousDetected = detected;
            _consistentCount = 0;
        }

        if (CurrentBorder == detected)
        {
            _inconsistentCount = 0;
            return false;
        }

        if (detected.IsUnknown)
        {
            if (_consistentCount == Options.UnknownFrameCount)
            {
                CurrentBorder = detected;
                return true;
            }
        }
        else if (CurrentBorder.IsUnknown || _consistentCount == Options.BorderFrameCount)
        {
            CurrentBorder = detected;
            return true;
        }

        return false;
    }
}
