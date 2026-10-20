using Overglow.Core.Color;

namespace Overglow.Core.Effects;

/// <summary>
/// Fades from one color to another and back, forever. Port of <c>Animation_Fade</c>
/// (sources/effects/Animation_Fade.cpp), preset "Breath". The C++ class also had a repeat count and an end state, but
/// every preset repeated forever, so they are left out.
/// </summary>
public sealed class FadeEffect(FadeEffect.Parameters parameters) : Effect
{
    private readonly List<Rgb8> _colors = [];
    private Step _step = Step.FadeIn;
    private Rgb8 _current;
    private double _steps;
    private double _minStepTime;
    private int _incrementIn;
    private double _sleepTimeIn;
    private int _incrementOut;
    private double _sleepTimeOut;
    private int _fadeInIndex = int.MinValue;
    private double _startHeld;
    private int _fadeOutIndex = int.MinValue;
    private double _endHeld;

    private enum Step
    {
        FadeIn,
        HoldStart,
        FadeOut,
        HoldEnd,
    }

    private double FadeInTime => parameters.FadeInMs / 1000.0;

    private double FadeOutTime => parameters.FadeOutMs / 1000.0;

    public override void Init(EffectCanvas canvas, int latchTimeMs)
    {
        var start = parameters.Start;
        var end = parameters.End;
        _minStepTime = Math.Max((float)latchTimeMs / 1000.0, 0.012);
        _steps = Math.Max(Math.Abs(end.R - start.R), Math.Max(Math.Abs(end.G - start.G), Math.Abs(end.B - start.B)));
        double stepR = 0, stepG = 0, stepB = 0;
        if (_steps == 0)
        {
            _steps = 1;
        }
        else
        {
            stepR = (end.R - start.R) / _steps;
            stepG = (end.G - start.G) / _steps;
            stepB = (end.B - start.B) / _steps;
        }

        _colors.Clear();
        for (var step = 0; step < (int)_steps + 1; step++)
        {
            _colors.Add(new Rgb8(Channel(start.R, end.R, stepR, step), Channel(start.G, end.G, stepG, step), Channel(start.B, end.B, stepB, step)));
        }

        (_incrementIn, _sleepTimeIn) = Increment(FadeInTime);
        (_incrementOut, _sleepTimeOut) = Increment(FadeOutTime);
    }

    public override EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds)
    {
        var last = (int)_steps;
        if (_step == Step.FadeIn)
        {
            if (FadeInTime > 0 && _fadeInIndex == int.MinValue && _colors.Count > 0)
            {
                _fadeInIndex = 0;
                _current = _colors[0];
                SleepTimeMs = (int)(_sleepTimeIn * 1000);
            }
            else if (FadeInTime > 0 && _fadeInIndex < last + 1 && _fadeInIndex < _colors.Count)
            {
                _current = _colors[_fadeInIndex];
                _fadeInIndex += _incrementIn;
                SleepTimeMs = (int)(_sleepTimeIn * 1000);
            }
            else
            {
                _step = Step.HoldStart;
            }
        }

        if (_step == Step.HoldStart)
        {
            if (_startHeld < parameters.StartHoldMs / 1000.0 && last < _colors.Count)
            {
                _current = _colors[last];
                SleepTimeMs = (int)(_minStepTime * 1000);
                _startHeld += _minStepTime;
            }
            else
            {
                _step = Step.FadeOut;
            }
        }

        if (_step == Step.FadeOut)
        {
            if (FadeOutTime > 0 && _fadeOutIndex == int.MinValue && last < _colors.Count)
            {
                _fadeOutIndex = last;
                _current = _colors[_fadeOutIndex];
                SleepTimeMs = (int)(_sleepTimeOut * 1000);
            }
            else if (FadeOutTime > 0 && _fadeOutIndex >= 0 && _fadeOutIndex < _colors.Count)
            {
                _current = _colors[_fadeOutIndex];
                _fadeOutIndex -= _incrementOut;
                SleepTimeMs = (int)(_sleepTimeOut * 1000);
            }
            else
            {
                _step = Step.HoldEnd;
            }
        }

        if (_step == Step.HoldEnd)
        {
            if (_endHeld < parameters.EndHoldMs / 1000.0 && _colors.Count > 0)
            {
                _current = _colors[0];
                SleepTimeMs = (int)(_minStepTime * 1000);
                _endHeld += _minStepTime;
            }
            else
            {
                // the next cycle starts with the following step; this one keeps the current color
                _startHeld = _endHeld = 0;
                _fadeInIndex = _fadeOutIndex = int.MinValue;
                _step = Step.FadeIn;
            }
        }

        leds.Fill(_current);
        return EffectOutput.Leds;
    }

    private static byte Channel(byte start, byte end, double step, int index) =>
        (byte)Math.Min(Math.Max((int)Math.Round(start + (step * index), MidpointRounding.AwayFromZero), 0), start < end ? end : start);

    // Colors skipped per step and the step time (s) so that a fade takes its time but a step lasts at least 15 ms.
    private (int Increment, double SleepTime) Increment(double fadeTime)
    {
        if (fadeTime <= 0)
        {
            return (1, 1);
        }

        var increment = Math.Max(1, (int)Math.Round(_steps / (fadeTime / _minStepTime), MidpointRounding.AwayFromZero));
        var mstep = fadeTime / (_steps / increment);
        var sleepTime = mstep;
        while (sleepTime < 0.015 && mstep > 0.0001)
        {
            sleepTime += mstep;
            increment++;
        }

        return (increment, sleepTime);
    }

    /// <param name="Start">The color at the start of the fade in (and the end of the fade out).</param>
    /// <param name="End">The color at the end of the fade in.</param>
    /// <param name="StartHoldMs">How long <paramref name="End"/> is held after the fade in (C++ "colorStartTime").</param>
    /// <param name="EndHoldMs">How long <paramref name="Start"/> is held after the fade out (C++ "colorEndTime").</param>
    public sealed record Parameters(Rgb8 Start, Rgb8 End, double FadeInMs, double FadeOutMs, double StartHoldMs, double EndHoldMs);
}
