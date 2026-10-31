import { describe, expect, it } from 'vitest';
import config from '../../api/__fixtures__/config.json';
import schemaFixture from '../../api/__fixtures__/schema.json';
import type { SettingsSchema } from '../../api/types';
import { propertyAt, withDefaults } from '../../schema/schema';
import { applyGameIntensity, applyMood, describePreset, detectGameIntensity, detectMood, GAME_PRESETS, MOOD_PRESETS } from '../intensity';
import { springFor } from '../smoothing';

const schema = schemaFixture as unknown as SettingsSchema;

describe('Ambient’s Mood', () => {
  it('treats the captured config as custom', () => {
    expect(detectMood(config)).toBe('custom');
    expect(detectMood(null)).toBeNull();
  });

  it('applies each mood to smoothing and color, keeping everything else', () => {
    for (const p of MOOD_PRESETS) {
      const partial = applyMood(config, p.id);
      expect(Object.keys(partial)).toEqual(['smoothing', 'color']);
      const merged = { ...config, ...partial };
      expect(detectMood(merged)).toBe(p.id);
      const adj = (merged.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
      expect(adj.gamma).toBe(config.color.channelAdjustment[0].gamma);
      expect((merged.smoothing as Record<string, unknown>).type).toBe(config.smoothing.type);
      expect(merged.smoothing).toMatchObject({ enable: true, time_ms: p.smoothingMs, ...springFor(p.smoothingMs) });
    }
  });

  it('starts at Natural with the default settings', () => {
    const adjustment = withDefaults(propertyAt(schema.properties.color, 'channelAdjustment', '[]'), {});
    const defaults = { smoothing: withDefaults(schema.properties.smoothing, {}), color: { channelAdjustment: [adjustment] } };
    expect(detectMood(defaults)).toBe('natural');
  });

  it('shows custom when the spring was changed by hand, or transitions are off', () => {
    const merged = { ...config, ...applyMood(config, 'natural') };
    expect(detectMood({ ...merged, smoothing: { ...(merged.smoothing as object), stiffness: 400 } })).toBe('custom');
    expect(detectMood({ ...merged, smoothing: { ...(merged.smoothing as object), enable: false } })).toBe('custom');
  });
});

describe('Game’s Intensity', () => {
  it('starts at Intense, the backend’s default (the game section)', () => {
    expect(withDefaults(schema.properties.game, {})).toEqual({ time_ms: 150, ...springFor(150), saturationGain: 1.5, luminanceGain: 1.2, fps: 60 });
    expect(detectGameIntensity(config)).toBe('intense');
    // Ambient's stays as saved
    expect(detectMood(config)).toBe('custom');
  });

  it('applies each preset to the game section only, keeping its other values', () => {
    for (const p of GAME_PRESETS) {
      const partial = applyGameIntensity(config, p.id);
      expect(Object.keys(partial)).toEqual(['game']);
      const merged = { ...config, ...partial };
      expect(detectGameIntensity(merged)).toBe(p.id);
      expect(merged.game).toMatchObject({ fps: 60 });
      expect(merged.smoothing).toBe(config.smoothing);
    }
  });

  it('keeps transitions quick and describes what each preset sets', () => {
    expect(GAME_PRESETS.every((p) => p.smoothingMs <= 250)).toBe(true);
    expect(MOOD_PRESETS.every((p) => p.smoothingMs >= 300)).toBe(true);
    expect(describePreset(GAME_PRESETS[2])).toEqual([
      ['Reaction time', '0.15 s'],
      ['Saturation', '150%'],
      ['Color brightness', '120%'],
    ]);
    expect(describePreset(MOOD_PRESETS[1])[0]).toEqual(['Transitions', '0.35 s']);
  });
});
