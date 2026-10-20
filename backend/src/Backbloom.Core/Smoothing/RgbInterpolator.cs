using System.Numerics;

namespace Backbloom.Core.Smoothing;

/// <summary>
/// Moves each RGB color a share of the remaining distance per tick; the share follows the elapsed part of the
/// transition time, bent upwards for small differences so they finish sooner. Port of
/// <c>InfiniteRgbInterpolator</c>; with <c>smoothingFactor</c> &gt; 0 every new target is also blended with the
/// previous one.
/// </summary>
public class RgbInterpolator : Interpolator
{
    // 16, 32 and 60 of 255: differences below them use the steeper curves.
    private const float LimitMin = 16.0f / 255.0f;
    private const float LimitMid = 32.0f / 255.0f;
    private const float LimitMax = 60.0f / 255.0f;

    private Vector3[] _current = [];
    private Vector3[] _target = [];
    private float _smoothingFactor;

    public override void SetSmoothingFactor(float factor) => _smoothingFactor = CppMath.Max(0.0f, CppMath.Min(factor, 1.0f));

    public override void SetTargetColors(ReadOnlySpan<Vector3> targets, long startTimeMs)
    {
        if (targets.IsEmpty)
        {
            return;
        }

        startTimeMs = AdjustStart(startTimeMs);
        if (Count != targets.Length)
        {
            LastUpdate = startTimeMs;
            EnsureCapacity(ref _current, targets.Length);
            EnsureCapacity(ref _target, targets.Length);
            targets.CopyTo(_current);
            targets.CopyTo(_target);
            Count = targets.Length;
            IsAnimationComplete = true;
        }
        else
        {
            if (_smoothingFactor > 0.0f)
            {
                var inv = 1.0f - _smoothingFactor;
                for (var i = 0; i < targets.Length; i++)
                {
                    _target[i] = (_target[i] * _smoothingFactor) + (targets[i] * inv);
                }
            }
            else
            {
                targets.CopyTo(_target);
            }

            IsAnimationComplete = false;
        }

        StartAnimation(startTimeMs);
    }

    public override void UpdateCurrentColors(long currentTimeMs, float minBrightness)
    {
        if (IsAnimationComplete)
        {
            LastUpdate = currentTimeMs;
            return;
        }

        var kOrg = Progress(currentTimeMs);
        LastUpdate = currentTimeMs;

        var kMin = CppMath.Min(MathF.Pow(kOrg, 1.0f), 1.0f);
        var kMid = CppMath.Min(MathF.Pow(kOrg, 0.9f), 1.0f);
        var kAbove = CppMath.Min(MathF.Pow(kOrg, 0.75f), 1.0f);
        var kMax = CppMath.Min(MathF.Pow(kOrg, 0.6f), 1.0f);

        var complete = true;
        for (var i = 0; i < Count; i++)
        {
            var cur = _current[i];
            var diff = _target[i] - cur;
            var val = CppMath.MaxAbs(diff);
            if (val < FinishComponentThreshold)
            {
                _current[i] = cur + diff;
                continue;
            }

            var k = val < LimitMin ? kMax : val < LimitMid ? kAbove : val < LimitMax ? kMid : kMin;
            _current[i] = new Vector3(
                CppMath.Clamp(cur.X + (k * diff.X), 0.0f, 1.0f),
                CppMath.Clamp(cur.Y + (k * diff.Y), 0.0f, 1.0f),
                CppMath.Clamp(cur.Z + (k * diff.Z), 0.0f, 1.0f));
            complete = false;
        }

        IsAnimationComplete = complete;
    }

    public override int GetCurrentColors(Span<Vector3> destination, float minBrightness)
    {
        _current.AsSpan(0, Count).CopyTo(destination);
        return Count;
    }

    /// <summary>Start time of a new transition (the C++ catch-up rule; the exponential variant has none).</summary>
    protected virtual long AdjustStart(long startTimeMs) => CatchUpStart(startTimeMs);

    /// <summary>The share of the remaining distance to cover in this tick, before the curve bending.</summary>
    protected virtual float Progress(long currentTimeMs)
    {
        var deltaTime = (float)(TargetTime - currentTimeMs);
        var totalTime = (float)(TargetTime - StartAnimationTime);
        return CppMath.Min(CppMath.Max(1.0f - (deltaTime / totalTime), 0.0001f), 1.0f);
    }
}

/// <summary>
/// The default C++ interpolator (<c>InfiniteStepperInterpolator</c>). It is the <see cref="RgbInterpolator"/> without
/// target blending: the C++ classes differ only in that.
/// </summary>
public sealed class StepperInterpolator : RgbInterpolator
{
    public override void SetSmoothingFactor(float factor)
    {
    }
}

/// <summary>
/// The share per tick is <c>1 - exp(-dt / time)</c> of the time since the last tick, so the colors approach the target
/// exponentially whatever the frame rate. Port of <c>InfiniteExponentialInterpolator</c>.
/// </summary>
public sealed class ExponentialInterpolator : RgbInterpolator
{
    public override void SetSmoothingFactor(float factor)
    {
    }

    protected override long AdjustStart(long startTimeMs) => startTimeMs;

    protected override float Progress(long currentTimeMs)
    {
        var dt = (float)(currentTimeMs - LastUpdate);
        return CppMath.Min(CppMath.Max(1.0f - MathF.Exp(-dt / InitialDuration), 0.0001f), 1.0f);
    }
}
