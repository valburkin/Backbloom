import { describe, expect, it } from 'vitest';
import { compileLayout } from './compile';
import { validateLayout } from './validate';
import type { LayoutSpec, LedRect } from './types';
import legacy from './__fixtures__/my-layout.json';
import specJson from './__fixtures__/my-layout.spec.json';

const spec = specJson as LayoutSpec;
const expected = legacy as LedRect[];
const KEYS = ['hmin', 'hmax', 'vmin', 'vmax'] as const;

describe('ground-truth fixture (hand-made 168-LED layout)', () => {
  it('compiles to the hand-made rects within 0.0006 on every coordinate', () => {
    const [compiled] = compileLayout(spec);
    expect(compiled.leds).toHaveLength(expected.length);
    const mismatches: string[] = [];
    compiled.leds.forEach((led, i) => {
      expect(led.disabled ?? false).toBe(false);
      for (const k of KEYS) {
        const diff = Math.abs(led[k] - expected[i][k]);
        if (diff > 0.0006) mismatches.push(`LED ${i} ${k}: got ${led[k]}, want ${expected[i][k]}`);
      }
    });
    expect(mismatches).toEqual([]);
  });

  it('is a valid layout', () => {
    expect(validateLayout(spec)).toEqual([]);
  });
});
