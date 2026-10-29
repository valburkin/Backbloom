import { describe, expect, it } from 'vitest';
import config from '../../api/__fixtures__/config.json';
import schemaFixture from '../../api/__fixtures__/schema.json';
import type { SettingsSchema } from '../../api/types';
import { propertyAt, withDefaults } from '../../schema/schema';
import { applyIntensity, describeIntensity, detectIntensity, INTENSITY_PRESETS } from '../intensity';
import { springFor } from '../smoothing';

describe('intensity presets', () => {
  it('treats the captured config as custom', () => {
    expect(detectIntensity(config)).toBe('custom');
    expect(detectIntensity(null)).toBeNull();
  });

  it('applies each preset to smoothing and color, keeping everything else', () => {
    for (const p of INTENSITY_PRESETS) {
      const partial = applyIntensity(config, p.id);
      expect(Object.keys(partial)).toEqual(['smoothing', 'color']);
      const merged = { ...config, ...partial };
      expect(detectIntensity(merged)).toBe(p.id);
      const adj = (merged.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
      expect(adj.gamma).toBe(config.color.channelAdjustment[0].gamma);
      expect((merged.smoothing as Record<string, unknown>).type).toBe(config.smoothing.type);
    }
  });

  it('turns smoothing on and describes what changes', () => {
    const off = { ...config, smoothing: { ...config.smoothing, enable: false } };
    expect((applyIntensity(off, 'calm').smoothing as { enable: boolean }).enable).toBe(true);
    expect(describeIntensity(INTENSITY_PRESETS[3])).toEqual([
      ['Transitions', '0.15 s'],
      ['Saturation', '150%'],
      ['Color brightness', '120%'],
    ]);
  });

  it('sets the spring that matches the transition time', () => {
    for (const p of INTENSITY_PRESETS) {
      const smoothing = applyIntensity(config, p.id).smoothing as Record<string, unknown>;
      expect(smoothing).toMatchObject({ time_ms: p.smoothingMs, ...springFor(p.smoothingMs) });
    }
  });

  it('starts at Balanced with the default settings', () => {
    const schema = schemaFixture as unknown as SettingsSchema;
    const adjustment = withDefaults(propertyAt(schema.properties.color, 'channelAdjustment', '[]'), {});
    const defaults = { smoothing: withDefaults(schema.properties.smoothing, {}), color: { channelAdjustment: [adjustment] } };
    expect(detectIntensity(defaults)).toBe('balanced');
  });

  it('shows custom when the spring was changed by hand', () => {
    const merged = { ...config, ...applyIntensity(config, 'balanced') };
    expect(detectIntensity({ ...merged, smoothing: { ...(merged.smoothing as object), stiffness: 400 } })).toBe('custom');
  });
});
