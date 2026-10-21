using System.Numerics;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine;

/// <summary>
/// The zones of the running setup (<see cref="ControllerSetup.Zones"/>) as the frame loop plays them over whatever the
/// lights show: a zone with an effect runs its own <see cref="EffectSource"/>, stepped without waiting
/// (<see cref="EffectSource.TryStep"/>) and mapped with the zone's own rects; a zone with a color shows that color.
/// Their colors go through the color settings like an effect's (colors skip them, like static colors) into the zone's
/// LEDs of every frame handed to the <see cref="OutputStage"/>. Built on a settings thread, then used by the frame loop
/// only; neither <see cref="Step"/> nor <see cref="Compose"/> allocates in steady state.
/// </summary>
public sealed class ZoneLayer : IDisposable
{
    private readonly Zone[] _zones;
    private readonly Vector3[] _scratch;
    private PipelineConfig? _mappedFor;

    private ZoneLayer(Zone[] zones)
    {
        _zones = zones;
        _scratch = new Vector3[zones.Length == 0 ? 0 : zones.Max(z => z.Leds.Length)];
        HasEffects = zones.Any(z => z.Effect is not null);
    }

    /// <summary>No zones.</summary>
    public static ZoneLayer Empty { get; } = new([]);

    public int Count => _zones.Length;

    /// <summary>At least one zone runs an effect (the frame loop wakes up for its steps).</summary>
    public bool HasEffects { get; }

    /// <summary>The zones' roles, in order.</summary>
    public IReadOnlyList<ZoneRole> Roles => [.. _zones.Select(z => z.Role)];

    /// <summary>The effect instances the zones run, by effect name (for tests and diagnostics).</summary>
    public IReadOnlyDictionary<string, EffectSource> Effects =>
        _zones.Where(z => z.Effect is not null).ToDictionary(z => z.Role.Effect!, z => z.Effect!, StringComparer.Ordinal);

    /// <summary>When the next effect step is due (clock milliseconds); <see cref="long.MaxValue"/> without effects.</summary>
    public long NextDue
    {
        get
        {
            var due = long.MaxValue;
            foreach (var zone in _zones)
            {
                if (zone.Effect is { } effect && effect.NextStepAt < due)
                {
                    due = effect.NextStepAt;
                }
            }

            return due;
        }
    }

    /// <summary>
    /// The zones of <paramref name="zones"/>. Effects whose zone <paramref name="previous"/> already ran are taken over
    /// (they keep running, now with this layer's LEDs); <paramref name="retired"/> are the previous layer's effects that
    /// weren't, for the caller to dispose once the frame loop uses the new layer. An effect name that isn't in the
    /// catalog leaves its zone dark (<paramref name="unknownEffect"/> is told).
    /// </summary>
    public static ZoneLayer Create(
        IReadOnlyList<LayoutZone> zones,
        ZoneLayer? previous,
        Func<EffectDefinition, EffectSource> createEffect,
        Action<string> unknownEffect,
        out IReadOnlyList<EffectSource> retired)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(createEffect);
        ArgumentNullException.ThrowIfNull(unknownEffect);
        var running = previous?.Effects.ToDictionary(StringComparer.Ordinal) ?? [];
        var list = new Zone[zones.Count];
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            var linear = new Vector3[zone.Leds.Count];
            EffectSource? effect = null;
            if (zone.Role.Effect is { } name)
            {
                if (running.Remove(name, out var kept))
                {
                    effect = kept;
                }
                else if (EffectCatalog.Find(name) is { } definition)
                {
                    effect = createEffect(definition);
                }
                else
                {
                    unknownEffect(name);
                }
            }
            else
            {
                var lut = Srgb.U8ToLinear16;
                Array.Fill(linear, new Vector3(lut[zone.Role.R] / 65535.0f, lut[zone.Role.G] / 65535.0f, lut[zone.Role.B] / 65535.0f));
            }

            list[i] = new Zone(zone.Role, [.. zone.Leds], LedLayout.FromJson(zone.Rects), effect, linear);
        }

        retired = [.. running.Values];
        return new ZoneLayer(list);
    }

    /// <summary>
    /// Steps every zone effect that is due at <paramref name="nowMs"/> (frame loop only).
    /// </summary>
    /// <returns>True when a zone got new colors.</returns>
    public bool Step(long nowMs, PipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!ReferenceEquals(config, _mappedFor))
        {
            // the zones' pictures are sampled the way the user set (mapping type, sparse processing), as an effect's are
            _mappedFor = config;
            foreach (var zone in _zones)
            {
                zone.Mapping = new LedMappingOptions(zone.Layout, config.Mapping.MappingType, config.Mapping.SparseProcessing);
            }
        }

        var changed = false;
        foreach (var zone in _zones)
        {
            if (zone.Effect is { } effect && effect.TryStep(zone.Mapping!, zone.Linear, nowMs))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Writes the zones' colors, processed for the output, into their LEDs of <paramref name="frame"/> (frame loop only):
    /// effects through the color settings, colors past them (as static colors).
    /// </summary>
    public void Compose(Span<Vector3> frame, PipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var zone in _zones)
        {
            var colors = _scratch.AsSpan(0, zone.Leds.Length);
            zone.Linear.CopyTo(colors);
            (zone.Role.IsEffect ? config.Processor : config.Passthrough).Process(colors);
            for (var i = 0; i < colors.Length; i++)
            {
                var led = zone.Leds[i];
                if ((uint)led < (uint)frame.Length)
                {
                    frame[led] = colors[i];
                }
            }
        }
    }

    /// <summary>Ends the zones' effects (closing the sound capture of music effects).</summary>
    public void Dispose()
    {
        foreach (var zone in _zones)
        {
            zone.Effect?.Dispose();
        }
    }

    private sealed class Zone(ZoneRole role, int[] leds, LedLayout layout, EffectSource? effect, Vector3[] linear)
    {
        public ZoneRole Role { get; } = role;

        public int[] Leds { get; } = leds;

        public LedLayout Layout { get; } = layout;

        public EffectSource? Effect { get; } = effect;

        /// <summary>The zone's colors in linear light, before the color settings.</summary>
        public Vector3[] Linear { get; } = linear;

        public LedMappingOptions? Mapping { get; set; }
    }
}
