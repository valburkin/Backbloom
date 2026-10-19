import { describe, expect, it } from 'vitest';
import { contrastInk, hexToRgb, hsvToRgb, isRgb, rgbToHex, rgbToHsv } from '../color';

describe('color conversions', () => {
  it('round-trips rgb → hsv → rgb', () => {
    for (const c of [
      [255, 0, 0],
      [0, 255, 0],
      [0, 0, 255],
      [255, 138, 0],
      [12, 200, 180],
      [0, 0, 0],
      [255, 255, 255],
      [128, 64, 200],
    ] as const) {
      expect(hsvToRgb(rgbToHsv(c))).toEqual(c);
    }
  });

  it('knows the primaries', () => {
    expect(rgbToHsv([0, 0, 255])).toEqual({ h: 240, s: 1, v: 1 });
    expect(hsvToRgb({ h: 120, s: 1, v: 1 })).toEqual([0, 255, 0]);
    expect(hsvToRgb({ h: 360, s: 1, v: 1 })).toEqual([255, 0, 0]);
  });

  it('parses and formats hex', () => {
    expect(rgbToHex([255, 138, 0])).toBe('#ff8a00');
    expect(hexToRgb('#FF8A00')).toEqual([255, 138, 0]);
    expect(hexToRgb('f80')).toEqual([255, 136, 0]);
    expect(hexToRgb('#12345')).toBeNull();
    expect(hexToRgb('zzzzzz')).toBeNull();
  });

  it('validates and picks readable ink', () => {
    expect(isRgb([1, 2, 3])).toBe(true);
    expect(isRgb([1, 2, 300])).toBe(false);
    expect(isRgb('red')).toBe(false);
    expect(contrastInk([255, 255, 255])).toBe('#0b0d12');
    expect(contrastInk([0, 0, 80])).toBe('#ffffff');
  });
});
