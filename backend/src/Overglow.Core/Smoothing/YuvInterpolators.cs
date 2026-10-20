using System.Numerics;

namespace Overglow.Core.Smoothing;

/// <summary>
/// Interpolates in BT.709 Y'CbCr: the share per tick follows the elapsed part of the transition time and the
/// brightness (Y) may change at most <c>y_limit</c> per tick, so dark-to-bright jumps fade in without hue shifts.
/// Port of <c>InfiniteYuvInterpolator</c>; <c>smoothingFactor</c> blends every new target with the previous one.
/// </summary>
public sealed class YuvInterpolator : Interpolator
{
    private Vector3[] _targetRgb = [];
    private Vector3[] _currentYuv = [];
    private Vector3[] _targetYuv = [];
    private float _maxLuminanceChangePerStep = 0.02f;
    private float _smoothingFactor;

    public override void SetMaxLuminanceChangePerFrame(float maxChangePerStep) => _maxLuminanceChangePerStep = CppMath.Max(0.0f, maxChangePerStep);

    public override void SetSmoothingFactor(float factor) => _smoothingFactor = CppMath.Max(0.0f, CppMath.Min(factor, 1.0f));

    public override void SetTargetColors(ReadOnlySpan<Vector3> targets, long startTimeMs)
    {
        if (targets.IsEmpty)
        {
            return;
        }

        startTimeMs = CatchUpStart(startTimeMs);
        var n = targets.Length;
        var sameSize = Count == n;
        EnsureCapacity(ref _targetRgb, n);
        EnsureCapacity(ref _currentYuv, n);
        EnsureCapacity(ref _targetYuv, n);

        // blend with the previous target, then convert to YUV
        if (_smoothingFactor > 0.0f && sameSize)
        {
            var inv = 1.0f - _smoothingFactor;
            for (var i = 0; i < n; i++)
            {
                _targetRgb[i] = (_targetRgb[i] * _smoothingFactor) + (targets[i] * inv);
            }
        }
        else
        {
            targets.CopyTo(_targetRgb);
        }

        for (var i = 0; i < n; i++)
        {
            _targetYuv[i] = Bt709.FromRgb(_targetRgb[i]);
        }

        if (!sameSize)
        {
            LastUpdate = startTimeMs;
            _targetYuv.AsSpan(0, n).CopyTo(_currentYuv);
            Count = n;
            IsAnimationComplete = true;
        }
        else
        {
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

        var deltaTime = (float)(TargetTime - currentTimeMs);
        var totalTime = (float)(TargetTime - StartAnimationTime);
        var kOrg = CppMath.Min(CppMath.Max(1.0f - (deltaTime / totalTime), 0.0001f), 1.0f);
        LastUpdate = currentTimeMs;

        var complete = true;
        for (var i = 0; i < Count; i++)
        {
            var diff = _targetYuv[i] - _currentYuv[i];
            if (CppMath.MaxAbs(diff) < FinishComponentThreshold)
            {
                _currentYuv[i] += diff;
                continue;
            }

            var step = kOrg * diff;
            if (_maxLuminanceChangePerStep > 0.0f && MathF.Abs(step.X) > _maxLuminanceChangePerStep)
            {
                step *= _maxLuminanceChangePerStep / MathF.Abs(step.X);
            }

            _currentYuv[i] += step;
            complete = false;
        }

        IsAnimationComplete = complete;
    }

    public override int GetCurrentColors(Span<Vector3> destination, float minBrightness) =>
        YuvOutput.Write(destination, Count, IsAnimationComplete, _targetRgb, _currentYuv, minBrightness);
}

/// <summary>
/// A spring (stiffness, damping) pulls each color towards its target in Y'CbCr, with the brightness change per tick
/// limited to <c>y_limit</c>. Port of <c>InfiniteHybridInterpolator</c>.
/// </summary>
public sealed class HybridInterpolator : Interpolator
{
    private const float VelocityThreshold = 0.0005f;

    private Vector3[] _targetRgb = [];
    private Vector3[] _currentYuv = [];
    private Vector3[] _targetYuv = [];
    private Vector3[] _velocities = [];
    private float _stiffness = 150.0f;
    private float _damping = 26.0f;
    private float _maxLuminanceChangePerStep = 0.02f;

    public override void SetSpringiness(float stiffness, float damping)
    {
        _stiffness = CppMath.Max(0.1f, stiffness);
        _damping = CppMath.Max(0.1f, damping);
    }

    public override void SetMaxLuminanceChangePerFrame(float maxChangePerStep) => _maxLuminanceChangePerStep = maxChangePerStep;

    public override void SetTargetColors(ReadOnlySpan<Vector3> targets, long startTimeMs)
    {
        if (targets.IsEmpty)
        {
            return;
        }

        startTimeMs = CatchUpStart(startTimeMs);
        var n = targets.Length;
        EnsureCapacity(ref _targetRgb, n);
        EnsureCapacity(ref _currentYuv, n);
        EnsureCapacity(ref _targetYuv, n);
        EnsureCapacity(ref _velocities, n);
        targets.CopyTo(_targetRgb);
        for (var i = 0; i < n; i++)
        {
            _targetYuv[i] = Bt709.FromRgb(targets[i]);
        }

        if (Count != n)
        {
            LastUpdate = startTimeMs;
            _targetYuv.AsSpan(0, n).CopyTo(_currentYuv);
            _velocities.AsSpan(0, n).Clear();
            Count = n;
            IsAnimationComplete = true;
        }
        else
        {
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

        var dt = CppMath.Clamp(currentTimeMs - LastUpdate, 0.001f, 100.0f);
        LastUpdate = currentTimeMs;
        var seconds = dt * 0.001f;
        var minLimits = new Vector3(minBrightness, -0.5f, -0.5f);
        var maxLimits = new Vector3(1.0f, 0.5f, 0.5f);

        var complete = true;
        for (var i = 0; i < Count; i++)
        {
            var cur = _currentYuv[i];
            var vel = _velocities[i];
            var diff = _targetYuv[i] - cur;
            if (SpringMath.HasArrived(_targetYuv[i], cur, vel, minLimits, maxLimits, VelocityThreshold))
            {
                _currentYuv[i] = cur + diff;
                _velocities[i] = Vector3.Zero;
                continue;
            }

            // semi-implicit Euler
            var acc = (_stiffness * diff) - (_damping * vel);
            vel += acc * seconds;
            var step = vel * seconds;
            if (_maxLuminanceChangePerStep > 0.0f && MathF.Abs(step.X) > _maxLuminanceChangePerStep)
            {
                var scale = _maxLuminanceChangePerStep / MathF.Abs(step.X);
                step *= scale;
                vel *= scale;
            }

            cur += step;
            SpringMath.Bound(ref cur, ref vel, minLimits, maxLimits);
            _currentYuv[i] = cur;
            _velocities[i] = vel;
            complete = false;
        }

        IsAnimationComplete = complete;
    }

    public override int GetCurrentColors(Span<Vector3> destination, float minBrightness) =>
        YuvOutput.Write(destination, Count, IsAnimationComplete, _targetRgb, _currentYuv, minBrightness);
}

/// <summary>
/// A spring (stiffness, damping) pulls each RGB channel towards its target; <c>smoothingFactor</c> blends every new
/// target with the previous one. Port of <c>InfiniteHybridRgbInterpolator</c> (the C++ default).
/// </summary>
public sealed class HybridRgbInterpolator : Interpolator
{
    private const float VelocityThreshold = 0.0005f;

    private Vector3[] _target = [];
    private Vector3[] _current = [];
    private Vector3[] _velocities = [];
    private float _stiffness = 150.0f;
    private float _damping = 26.0f;
    private float _smoothingFactor;

    public override void SetSpringiness(float stiffness, float damping)
    {
        _stiffness = CppMath.Max(0.1f, stiffness);
        _damping = CppMath.Max(0.1f, damping);
    }

    public override void SetSmoothingFactor(float factor) => _smoothingFactor = CppMath.Max(0.0f, CppMath.Min(factor, 1.0f));

    public override void SetTargetColors(ReadOnlySpan<Vector3> targets, long startTimeMs)
    {
        if (targets.IsEmpty)
        {
            return;
        }

        startTimeMs = CatchUpStart(startTimeMs);
        var n = targets.Length;
        if (Count != n)
        {
            LastUpdate = startTimeMs;
            EnsureCapacity(ref _target, n);
            EnsureCapacity(ref _current, n);
            EnsureCapacity(ref _velocities, n);
            targets.CopyTo(_current);
            targets.CopyTo(_target);
            _velocities.AsSpan(0, n).Clear();
            Count = n;
            IsAnimationComplete = true;
        }
        else
        {
            if (_smoothingFactor > 0.0f)
            {
                var inv = 1.0f - _smoothingFactor;
                for (var i = 0; i < n; i++)
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

        var dt = CppMath.Clamp(currentTimeMs - LastUpdate, 0.001f, 100.0f);
        LastUpdate = currentTimeMs;
        var seconds = dt * 0.001f;
        var minLimits = new Vector3(minBrightness);
        var maxLimits = Vector3.One;

        var complete = true;
        for (var i = 0; i < Count; i++)
        {
            var cur = _current[i];
            var vel = _velocities[i];
            var tgt = _target[i];
            var diff = tgt - cur;
            if (SpringMath.HasArrived(tgt, cur, vel, minLimits, maxLimits, VelocityThreshold))
            {
                _current[i] = tgt;
                _velocities[i] = Vector3.Zero;
                continue;
            }

            var acc = (_stiffness * diff) - (_damping * vel);
            vel += acc * seconds;
            cur += vel * seconds;
            SpringMath.Bound(ref cur, ref vel, minLimits, maxLimits);
            _current[i] = cur;
            _velocities[i] = vel;
            complete = false;
        }

        IsAnimationComplete = complete;
    }

    public override int GetCurrentColors(Span<Vector3> destination, float minBrightness)
    {
        for (var i = 0; i < Count; i++)
        {
            destination[i] = CppMath.ClampLinalg(_current[i], minBrightness, 1.0f);
        }

        return Count;
    }
}

internal static class SpringMath
{
    /// <summary>
    /// A spring color has arrived when it is still and every channel is at its target, or at the edge of its range
    /// when the target lies beyond it (<see cref="Bound"/> keeps such a channel there for good).
    /// </summary>
    /// <remarks>
    /// Deliberate deviation from HyperHDR, which only compared with the target: a channel whose target is below the
    /// minimal backlight (black, or the zero channels of a saturated color) never arrived, so the transition never
    /// finished and smoothing kept sending at its full rate.
    /// </remarks>
    public static bool HasArrived(Vector3 target, Vector3 current, Vector3 velocity, Vector3 min, Vector3 max, float velocityThreshold)
    {
        if (CppMath.MaxAbs(velocity) >= velocityThreshold)
        {
            return false;
        }

        var gap = Vector3.Min(Vector3.Abs(target - current), Vector3.Abs(Vector3.Clamp(target, min, max) - current));
        return CppMath.MaxAbs(gap) < Interpolator.FinishComponentThreshold;
    }

    /// <summary>A channel that left its range is put back on the edge and stops (its velocity becomes 0).</summary>
    public static void Bound(ref Vector3 cur, ref Vector3 vel, Vector3 min, Vector3 max)
    {
        if (cur.X < min.X || cur.X > max.X)
        {
            vel.X = 0.0f;
            cur.X = CppMath.Clamp(cur.X, min.X, max.X);
        }

        if (cur.Y < min.Y || cur.Y > max.Y)
        {
            vel.Y = 0.0f;
            cur.Y = CppMath.Clamp(cur.Y, min.Y, max.Y);
        }

        if (cur.Z < min.Z || cur.Z > max.Z)
        {
            vel.Z = 0.0f;
            cur.Z = CppMath.Clamp(cur.Z, min.Z, max.Z);
        }
    }
}

internal static class YuvOutput
{
    /// <summary>Finished: the RGB targets as they came in; otherwise the YUV colors converted back and clamped.</summary>
    public static int Write(Span<Vector3> destination, int count, bool complete, Vector3[] targetRgb, Vector3[] currentYuv, float minBrightness)
    {
        if (complete)
        {
            targetRgb.AsSpan(0, count).CopyTo(destination);
            return count;
        }

        for (var i = 0; i < count; i++)
        {
            destination[i] = CppMath.ClampLinalg(Bt709.ToRgb(currentYuv[i]), minBrightness, 1.0f);
        }

        return count;
    }
}
