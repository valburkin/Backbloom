import { describe, expect, it } from 'vitest';
import { easeShare, exposure, previewColors } from '../previewLight';

describe('preview colors', () => {
  it('draws each LED by its light: full stays full, off stays off, a dim LED stays visible', () => {
    expect(previewColors([255, 0, 0, 0, 0, 0, 255, 255, 255])).toEqual([255, 0, 0, 0, 0, 0, 255, 255, 255]);
    expect(previewColors([3, 0, 0])![0]).toBeGreaterThan(20);
    // half the light is about 188 on the screen's curve
    expect(previewColors([128, 128, 128])).toEqual([188, 188, 188]);
  });

  it('stays readable at a low brightness and still gets darker along the slider', () => {
    // 10 % of the full output left: a white LED gets about 26 of 255
    const low = previewColors([26, 26, 26], 0.1)![0];
    const lower = previewColors([3, 3, 3], 0.01)![0];
    expect(low).toBeGreaterThan(120);
    expect(lower).toBeGreaterThan(70);
    expect(lower).toBeLessThan(low);
    expect(low).toBeLessThan(255);
  });

  it('keeps the hue when brightening would pass full: nothing turns white', () => {
    const [r, g, b] = previewColors([200, 20, 0], 0.01)!;
    expect(r).toBe(255);
    expect(g).toBeLessThan(150);
    expect(b).toBe(0);
  });

  it('passes odd input through: no NaN, extra values kept', () => {
    expect(previewColors([26, 26, 26], Number.NaN)).toEqual(previewColors([26, 26, 26]));
    expect(previewColors([255, 0, 0, 7])).toEqual([255, 0, 0, 7]);
  });

  it('eases a new share in over the transitions instead of jumping', () => {
    // brightness up from 1 % to full: a moment later the preview still brightens the colors still fading up
    const soon = easeShare(0.01, 1, 50);
    expect(soon).toBeGreaterThan(0.01);
    expect(soon).toBeLessThan(0.1);
    // and arrives within about a second
    expect(easeShare(0.01, 1, 1500)).toBe(1);
    // down works the same way, and there is nothing to ease from without a share
    expect(easeShare(1, 0.01, 50)).toBeGreaterThan(0.1);
    expect(easeShare(Number.NaN, 0.5, 10)).toBe(0.5);
  });

  it('brightens only for a share below full', () => {
    expect(exposure(undefined)).toBe(1);
    expect(exposure(1)).toBe(1);
    expect(exposure(0)).toBe(1);
    expect(exposure(0.25)).toBeCloseTo(Math.pow(4, 0.6), 6);
    expect(previewColors(null)).toBeNull();
  });
});
