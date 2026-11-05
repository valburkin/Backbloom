import { describe, expect, it } from 'vitest';
import config from '../../api/__fixtures__/config.json';
import schemaFixture from '../../api/__fixtures__/schema.json';
import type { SettingsSchema } from '../../api/types';
import { propertyAt, withDefaults } from '../../schema/schema';
import { applyLook, DEFAULT_MIN_GLOW, describePreset, detectLook, detectProfileLook, LOOKS, profileLook, savedLook } from '../intensity';
import { springFor } from '../smoothing';

const schema = schemaFixture as unknown as SettingsSchema;

describe('Looks', () => {
  it('lists the five from calm to quick', () => {
    expect(LOOKS.map((l) => l.id)).toEqual(['relaxed', 'natural', 'rich', 'vivid', 'intense']);
    expect(LOOKS.map((l) => l.smoothingMs)).toEqual([550, 350, 300, 200, 150]);
    expect(LOOKS.filter((l) => l.minGlow === 0).map((l) => l.id)).toEqual(['vivid', 'intense']);
  });

  it('treats the captured config as custom', () => {
    expect(detectLook(config)).toBe('custom');
    expect(detectLook(null)).toBeNull();
  });

  it('applies each Look to smoothing and color, keeping everything else', () => {
    for (const p of LOOKS) {
      const partial = applyLook(config, p.id);
      expect(Object.keys(partial)).toEqual(['smoothing', 'color']);
      const merged = { ...config, ...partial };
      expect(detectLook(merged)).toBe(p.id);
      const adj = (merged.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
      expect(adj.gamma).toBe(config.color.channelAdjustment[0].gamma);
      expect(adj.backlightThreshold).toBe(p.minGlow);
      expect((merged.smoothing as Record<string, unknown>).type).toBe(config.smoothing.type);
      expect(merged.smoothing).toMatchObject({ enable: true, time_ms: p.smoothingMs, ...springFor(p.smoothingMs) });
    }
  });

  it('starts at Natural with the default settings', () => {
    const adjustment = withDefaults(propertyAt(schema.properties.color, 'channelAdjustment', '[]'), {}) as Record<string, unknown>;
    expect(adjustment.backlightThreshold).toBe(DEFAULT_MIN_GLOW);
    const defaults = { smoothing: withDefaults(schema.properties.smoothing, {}), color: { channelAdjustment: [adjustment] } };
    expect(detectLook(defaults)).toBe('natural');
  });

  it('shows custom when the spring or the minimum glow was changed by hand, or transitions are off', () => {
    const merged = { ...config, ...applyLook(config, 'natural') };
    expect(detectLook({ ...merged, smoothing: { ...(merged.smoothing as object), stiffness: 400 } })).toBe('custom');
    expect(detectLook({ ...merged, smoothing: { ...(merged.smoothing as object), enable: false } })).toBe('custom');
    const adj = (merged.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
    expect(detectLook({ ...merged, color: { channelAdjustment: [{ ...adj, backlightThreshold: 0.05 }] } })).toBe('custom');
  });

  it('stores a Look in a preset and reads it back', () => {
    for (const p of LOOKS) expect(detectProfileLook(profileLook(p.id))).toBe(p.id);
    expect(detectProfileLook(undefined)).toBeNull();
    expect(detectProfileLook({ ...profileLook('rich'), saturationGain: 2 })).toBe('custom');
  });

  it('takes the saved values for a new preset: the Look they match, or the values as they are', () => {
    expect(savedLook(null)).toBeNull();
    expect(savedLook({ ...config, ...applyLook(config, 'vivid') })).toEqual(profileLook('vivid'));
    expect(savedLook(config)).toEqual({ time_ms: 150, stiffness: 150, damping: 26, saturationGain: 1, luminanceGain: 1, backlightThreshold: 0.0039 });
  });

  it('describes what each Look sets', () => {
    expect(describePreset(LOOKS[4])).toEqual([
      ['Transitions', '0.15 s'],
      ['Saturation', '150%'],
      ['Color brightness', '120%'],
      ['Dark scenes', 'Stay dark'],
    ]);
    expect(describePreset(LOOKS[1])[3]).toEqual(['Dark scenes', 'Faint glow']);
  });
});
