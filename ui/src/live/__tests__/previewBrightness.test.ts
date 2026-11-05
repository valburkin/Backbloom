import { describe, expect, it } from 'vitest';
import { scaleForBrightness } from '../../settings/brightness';
import { atFullBrightness, outputShare, shownBrighter } from '../previewBrightness';

describe('previews at full brightness', () => {
  it('divides by the share of the full output Home’s Brightness gives (about the slider position squared)', () => {
    expect(outputShare(scaleForBrightness(0.5), undefined, true)).toBeCloseTo(0.25, 6);
    expect(outputShare(scaleForBrightness(0.2, 2.2), 2.2, true)).toBeCloseTo(0.04, 6);
    expect(atFullBrightness([10, 25, 0, 200, 64, 1], 0.25)).toEqual([40, 100, 0, 255, 255, 4]);
    expect(shownBrighter(0.25)).toBe(true);
  });

  it('leaves the colors at full or boosted brightness, at 0, and while the lights are off', () => {
    const frame = [10, 20, 30];
    expect(outputShare(1, undefined, true)).toBe(1);
    expect(outputShare(1.5, undefined, true)).toBe(1);
    expect(outputShare(0, undefined, true)).toBe(1);
    expect(outputShare(undefined, undefined, true)).toBe(1);
    expect(outputShare(scaleForBrightness(0.5), undefined, false)).toBe(1);
    expect(atFullBrightness(frame, 1)).toBe(frame);
    expect(atFullBrightness(null, 0.25)).toBeNull();
    expect(shownBrighter(1)).toBe(false);
  });
});
