import { describe, expect, it } from 'vitest';
import { brightnessForScale, DEFAULT_GAMMA, gameScale, scaleForBrightness, withGameScale } from '../brightness';

// What a strip shows for white on the screen: the sRGB-encoded value the backend sends, with the contrast (gamma).
const encode = (scale: number) => (scale <= 0.0031308 ? scale * 12.92 : 1.055 * Math.pow(scale, 1 / 2.4) - 0.055);
const output = (scale: number, gamma = DEFAULT_GAMMA) => Math.pow(encode(scale), gamma);

describe('brightness curve', () => {
  it('gives about the square of the slider position as output, with the default contrast', () => {
    expect(scaleForBrightness(0)).toBe(0);
    expect(scaleForBrightness(1)).toBeCloseTo(1, 9);
    for (const p of [0.1, 0.25, 0.5, 0.75]) expect(output(scaleForBrightness(p))).toBeCloseTo(p * p, 6);
  });

  it('follows the contrast set on the Colors page', () => {
    for (const gamma of [1, 2.2]) {
      expect(output(scaleForBrightness(0.5, gamma), gamma)).toBeCloseTo(0.25, 6);
      expect(brightnessForScale(scaleForBrightness(0.3, gamma), gamma)).toBeCloseTo(0.3, 6);
    }
    expect(scaleForBrightness(0.5, 0)).toBe(scaleForBrightness(0.5));
  });

  it('keeps the bottom of the slider usable', () => {
    // 10% still lights an 8-bit strip (1% of the full output, about 2.5 of 255)
    expect(output(scaleForBrightness(0.1)) * 255).toBeGreaterThan(2);
  });

  it('turns a stored value back into the slider position, boosted ones above 100%', () => {
    for (const p of [0, 0.05, 0.3, 0.5, 0.9, 1]) expect(brightnessForScale(scaleForBrightness(p))).toBeCloseTo(p, 6);
    expect(brightnessForScale(1.5)).toBeGreaterThan(1);
    expect(brightnessForScale(-1)).toBe(0);
  });
});

describe('brightness per mode', () => {
  it('reads Game mode’s own brightness, undefined until it is set', () => {
    expect(gameScale(null)).toBeUndefined();
    expect(gameScale({ game: { fps: 60 } })).toBeUndefined();
    expect(gameScale({ game: { fps: 60, scaleOutput: 0.3 } })).toBe(0.3);
  });

  it('saves it in the game section and keeps the rest', () => {
    expect(withGameScale({ game: { fps: 60, saturationGain: 1.5 }, color: {} }, 0.4)).toEqual({ game: { fps: 60, saturationGain: 1.5, scaleOutput: 0.4 } });
    expect(withGameScale({}, 0.4)).toEqual({ game: { scaleOutput: 0.4 } });
  });
});
