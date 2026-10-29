import { describe, expect, it } from 'vitest';
import type { LayoutView } from '../../live/layoutView';
import { compactLayoutView } from '../LayoutLights';

// A ceiling strip high above a small screen with LEDs around it: the wall in between is empty.
const ceiling = Array.from({ length: 20 }, (_, i) => ({ x: 40 + i * 20, y: 20, led: i }));
const screenLeds = Array.from({ length: 10 }, (_, i) => ({ x: 200 + i * 8, y: 300, led: 20 + i }));
const view: LayoutView = {
  width: 480,
  height: 340,
  screen: { x: 200, y: 260, w: 80, h: 40 },
  surfaces: [{ x: 0, y: 0, w: 480, h: 340 }],
  leds: [...ceiling, ...screenLeds],
  spacing: 8,
};

describe('compactLayoutView', () => {
  it('squeezes the empty wall between strips and keeps their order and the screen', () => {
    const c = compactLayoutView(view);
    expect(c.height).toBeLessThan(view.height * 0.6);
    expect(c.surfaces).toEqual([]);
    expect(c.leds.map((l) => l.led)).toEqual(view.leds.map((l) => l.led));
    const top = c.leds[0].y;
    const bottom = c.leds[c.leds.length - 1].y;
    expect(top).toBeLessThan(c.screen.y);
    expect(bottom).toBeGreaterThan(c.screen.y);
    expect(c.screen.w).toBeCloseTo(80);
    expect(c.screen.h).toBeCloseTo(40);
    // neighbors on a strip keep their distance
    expect(c.leds[1].x - c.leds[0].x).toBeCloseTo(20);
  });

  it('leaves a view that is already tight alone', () => {
    const tight: LayoutView = { ...view, width: 400, height: 60, screen: { x: 40, y: 10, w: 380, h: 40 }, leds: ceiling.map((l) => ({ ...l, y: 30 })) };
    expect(compactLayoutView(tight)).toBe(tight);
  });
});
