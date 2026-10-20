using System.Numerics;

namespace Backbloom.Core.Smoothing;

/// <summary>
/// Moves the LED colors towards the latest captured colors over time. Port of HyperHDR's <c>InfiniteInterpolator</c>
/// family (sources/infinite-color-engine/Infinite*Interpolator.cpp, MIT, awawa-dev). Colors are nonlinear RGB 0..1.
/// </summary>
/// <remarks>
/// <para>Times are milliseconds of a monotonic clock, passed in by the caller, so the interpolators are deterministic.
/// The C++ code rounded them through <c>float</c> on the way (exact only up to 2^24 ms, 4.6 hours of uptime); here they
/// stay integers, which gives the same results while the C++ ones were exact and keeps millisecond precision after.</para>
/// <para>Buffers grow when the LED count does and are reused otherwise: no allocations per frame or tick. Not
/// thread-safe; <see cref="Smoother"/> is used under one lock.</para>
/// </remarks>
public abstract class Interpolator
{
    /// <summary>A color closer than this (in every channel) to its target has arrived.</summary>
    internal const float FinishComponentThreshold = 0.0013732906f / 10.0f;

    private float _initialDuration = 150.0f;

    /// <summary>LEDs currently held (0 until the first target arrives or after <see cref="ResetState"/>).</summary>
    public int Count { get; protected set; }

    public bool IsAnimationComplete { get; protected set; } = true;

    protected long LastUpdate { get; set; }

    protected long StartAnimationTime { get; set; }

    protected long TargetTime { get; set; }

    protected float InitialDuration => _initialDuration;

    /// <summary>Creates the interpolator of <paramref name="config"/>.Type with its parameters set.</summary>
    public static Interpolator Create(SmoothingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Interpolator interpolator = config.Type switch
        {
            SmoothingType.RgbInterpolator => new RgbInterpolator(),
            SmoothingType.YuvInterpolator => new YuvInterpolator(),
            SmoothingType.HybridInterpolator => new HybridInterpolator(),
            SmoothingType.HybridRgbInterpolator => new HybridRgbInterpolator(),
            SmoothingType.ExponentialInterpolator => new ExponentialInterpolator(),
            _ => new StepperInterpolator(),
        };
        interpolator.SetTransitionDuration(config.SettlingTimeMs);
        interpolator.SetSmoothingFactor(config.SmoothingFactor);
        interpolator.SetSpringiness(config.Stiffness, config.Damping);
        interpolator.SetMaxLuminanceChangePerFrame(config.YLimit);
        return interpolator;
    }

    /// <summary>A new frame arrived: its colors become the target, the transition restarts at <paramref name="startTimeMs"/>.</summary>
    public abstract void SetTargetColors(ReadOnlySpan<Vector3> targets, long startTimeMs);

    /// <summary>Advances the current colors to <paramref name="currentTimeMs"/> (one smoothing tick).</summary>
    public abstract void UpdateCurrentColors(long currentTimeMs, float minBrightness);

    /// <summary>Writes the current colors (<see cref="Count"/> of them) and returns how many were written.</summary>
    public abstract int GetCurrentColors(Span<Vector3> destination, float minBrightness);

    /// <summary>Forgets every color: the next target is shown at once.</summary>
    public virtual void ResetState()
    {
        IsAnimationComplete = true;
        LastUpdate = 0;
        Count = 0;
    }

    public virtual void SetTransitionDuration(float durationMs) => _initialDuration = CppMath.Max(1.0f, durationMs);

    public virtual void SetSmoothingFactor(float factor)
    {
    }

    public virtual void SetSpringiness(float stiffness, float damping)
    {
    }

    public virtual void SetMaxLuminanceChangePerFrame(float maxChangePerStep)
    {
    }

    /// <summary>
    /// A frame that arrives during a transition starts the new one where the last tick left off (at most 100 ms
    /// earlier), so a steady frame rate doesn't keep restarting the curve from zero.
    /// </summary>
    protected long CatchUpStart(long startTimeMs)
    {
        var delta = !IsAnimationComplete ? CppMath.Clamp(startTimeMs - LastUpdate, 0.0f, 100.0f) : 0.0f;
        return startTimeMs - (long)delta;
    }

    protected void StartAnimation(long startTimeMs)
    {
        StartAnimationTime = startTimeMs;
        TargetTime = startTimeMs + (long)_initialDuration;
    }

    /// <summary>Makes sure every buffer can hold <paramref name="count"/> colors (allocates only when it grows).</summary>
    protected static void EnsureCapacity(ref Vector3[] buffer, int count)
    {
        if (buffer.Length < count)
        {
            buffer = new Vector3[count];
        }
    }
}

/// <summary>Float helpers with the exact semantics of the C++ functions they replace (std::min/max/clamp, linalg).</summary>
internal static class CppMath
{
    /// <summary>std::max: <c>a &lt; b ? b : a</c>.</summary>
    public static float Max(float a, float b) => a < b ? b : a;

    /// <summary>std::min: <c>b &lt; a ? b : a</c>.</summary>
    public static float Min(float a, float b) => b < a ? b : a;

    /// <summary>std::clamp.</summary>
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : hi < v ? hi : v;

    /// <summary>linalg::maxelem(linalg::abs(v)).</summary>
    public static float MaxAbs(Vector3 v) => Max(Max(MathF.Abs(v.X), MathF.Abs(v.Y)), MathF.Abs(v.Z));

    /// <summary>linalg::clamp of a vector: <c>x &lt; lo ? lo : x &lt; hi ? x : hi</c> per channel.</summary>
    public static Vector3 ClampLinalg(Vector3 v, float lo, float hi) => new(ClampLinalg(v.X, lo, hi), ClampLinalg(v.Y, lo, hi), ClampLinalg(v.Z, lo, hi));

    private static float ClampLinalg(float x, float lo, float hi) => x < lo ? lo : x < hi ? x : hi;
}

/// <summary>
/// BT.709 RGB to Y'CbCr and back, as <c>ColorSpaceMath::rgb_to_bt709</c> / <c>bt709_to_rgb</c>: the inverse matrix is
/// computed in float the way linalg's <c>inverse</c> does (adjugate divided by the determinant), so it is bit-identical.
/// </summary>
internal static class Bt709
{
    // Rows of the forward matrix.
    private static readonly Vector3 s_r0 = new(0.2126f, 0.7152f, 0.0722f);
    private static readonly Vector3 s_r1 = new(-0.1146f, -0.3854f, 0.5000f);
    private static readonly Vector3 s_r2 = new(0.5000f, -0.4542f, -0.0458f);

    // Rows of the inverse.
    private static readonly (Vector3 R0, Vector3 R1, Vector3 R2) s_inverse = Invert();

    public static Vector3 FromRgb(Vector3 rgb) => Mul(s_r0, s_r1, s_r2, rgb);

    public static Vector3 ToRgb(Vector3 yuv) => Mul(s_inverse.R0, s_inverse.R1, s_inverse.R2, yuv);

    private static (Vector3, Vector3, Vector3) Invert()
    {
        // linalg stores columns: a.c.r is column c, row r.
        float xx = s_r0.X, xy = s_r1.X, xz = s_r2.X;
        float yx = s_r0.Y, yy = s_r1.Y, yz = s_r2.Y;
        float zx = s_r0.Z, zy = s_r1.Z, zz = s_r2.Z;

        // adjugate, column by column
        float axx = (yy * zz) - (zy * yz), axy = (zy * xz) - (xy * zz), axz = (xy * yz) - (yy * xz);
        float ayx = (yz * zx) - (zz * yx), ayy = (zz * xx) - (xz * zx), ayz = (xz * yx) - (yz * xx);
        float azx = (yx * zy) - (zx * yy), azy = (zx * xy) - (xx * zy), azz = (xx * yy) - (yx * xy);
        var det = (xx * ((yy * zz) - (zy * yz))) + (xy * ((yz * zx) - (zz * yx))) + (xz * ((yx * zy) - (zx * yy)));

        // inverse = adjugate / determinant; row r of the result is (column x row r, column y row r, column z row r)
        return (new Vector3(axx / det, ayx / det, azx / det), new Vector3(axy / det, ayy / det, azy / det), new Vector3(axz / det, ayz / det, azz / det));
    }

    // linalg::mul(mat, vec) = col0 * v.x + col1 * v.y + col2 * v.z, summed left to right.
    private static Vector3 Mul(Vector3 r0, Vector3 r1, Vector3 r2, Vector3 v) => new(
        (r0.X * v.X) + (r0.Y * v.Y) + (r0.Z * v.Z),
        (r1.X * v.X) + (r1.Y * v.Y) + (r1.Z * v.Z),
        (r2.X * v.X) + (r2.Y * v.Y) + (r2.Z * v.Z));
}
