import { describe, expect, it } from 'vitest';
import config from '../../api/__fixtures__/config.json';
import { applyIntensity, describeIntensity, detectIntensity, INTENSITY_PRESETS } from '../intensity';

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
    expect((applyIntensity(off, 'subtle').smoothing as { enable: boolean }).enable).toBe(true);
    expect(describeIntensity(INTENSITY_PRESETS[3])).toEqual([
      ['Smoothing', '80 ms transitions'],
      ['Saturation', '150%'],
      ['Luminance', '120%'],
    ]);
  });
});
