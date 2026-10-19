import { describe, expect, it } from 'vitest';
import { compileLayout, compileSegment, compileStrip, DISABLED_RECT, stripLedCount } from './compile';
import type { LayoutSpec, Segment, Strip } from './types';

const strip = (segments: Segment[], output = 0): Strip => ({
  id: `s${output}`,
  name: `Strip ${output}`,
  controllerId: 'c',
  output,
  segments,
});

describe('compileSegment', () => {
  it('spaces edge LEDs evenly with the default 1.2 blend', () => {
    const rects = compileSegment({ kind: 'edge', count: 4, edge: 'top', from: 0, to: 1, depth: 0.1 });
    expect(rects).toHaveLength(4);
    // Centers 0.125, 0.375...; half width 1.2 * 0.25 / 2 = 0.15.
    expect(rects[1]).toEqual({ hmin: 0.225, hmax: 0.525, vmin: 0, vmax: 0.1, group: 0 });
    expect(rects[0].hmin).toBe(0); // clamped
    expect(rects[3].hmax).toBe(1); // clamped
  });

  it('runs backwards when from > to and maps each edge to its band', () => {
    const bottom = compileSegment({ kind: 'edge', count: 2, edge: 'bottom', from: 1, to: 0, depth: 0.2, blend: 1 });
    expect(bottom).toEqual([
      { hmin: 0.5, hmax: 1, vmin: 0.8, vmax: 1, group: 0 },
      { hmin: 0, hmax: 0.5, vmin: 0.8, vmax: 1, group: 0 },
    ]);
    const left = compileSegment({ kind: 'edge', count: 1, edge: 'left', from: 0, to: 1, depth: 0.3, blend: 1 });
    expect(left).toEqual([{ hmin: 0, hmax: 0.3, vmin: 0, vmax: 1, group: 0 }]);
    const right = compileSegment({ kind: 'edge', count: 1, edge: 'right', from: 0.2, to: 0.4, depth: 0.25, blend: 1 });
    expect(right).toEqual([{ hmin: 0.75, hmax: 1, vmin: 0.2, vmax: 0.4, group: 0 }]);
  });

  it('rounds to 4 decimals', () => {
    const [r] = compileSegment({ kind: 'edge', count: 3, edge: 'top', from: 0, to: 1, depth: 1 / 3, blend: 1 });
    expect(r.hmax).toBe(0.3333);
    expect(r.vmax).toBe(0.3333);
  });

  it('compiles fixed and off segments', () => {
    const area = { hmin: 0.4, hmax: 0.6, vmin: 0.8, vmax: 1 };
    expect(compileSegment({ kind: 'fixed', count: 2, area })).toEqual([
      { ...area, group: 0 },
      { ...area, group: 0 },
    ]);
    expect(compileSegment({ kind: 'off', count: 1 })).toEqual([{ ...DISABLED_RECT }]);
  });

  it('produces nothing for invalid counts', () => {
    expect(compileSegment({ kind: 'off', count: 0 })).toEqual([]);
    expect(compileSegment({ kind: 'off', count: -3 })).toEqual([]);
    expect(compileSegment({ kind: 'off', count: NaN })).toEqual([]);
  });
});

describe('compileStrip / stripLedCount', () => {
  it('concatenates segments in wiring order', () => {
    const s = strip([
      { kind: 'off', count: 2 },
      { kind: 'edge', count: 3, edge: 'left', from: 1, to: 0, depth: 0.1 },
    ]);
    const rects = compileStrip(s);
    expect(stripLedCount(s)).toBe(5);
    expect(rects).toHaveLength(5);
    expect(rects[0].disabled).toBe(true);
    expect(rects[2].vmax).toBeGreaterThan(rects[4].vmax);
  });
});

describe('compileLayout', () => {
  const spec = (strips: Strip[]): LayoutSpec => ({
    version: 1,
    controllers: [
      {
        id: 'c',
        name: 'C',
        kind: 'wled',
        host: 'c.local',
        outputs: [
          { index: 0, start: 0, length: 3 },
          { index: 1, start: 5, length: 2 },
        ],
      },
    ],
    strips,
  });
  const fixed = (count: number): Segment => ({ kind: 'fixed', count, area: { hmin: 0, hmax: 1, vmin: 0, vmax: 1 } });

  it('covers the whole index space and places strips at their output start', () => {
    const [c] = compileLayout(spec([strip([fixed(3)], 0), strip([fixed(2)], 1)]));
    expect(c.controllerId).toBe('c');
    expect(c.leds).toHaveLength(7);
    expect(c.leds.map((l) => Boolean(l.disabled))).toEqual([false, false, false, true, true, false, false]);
  });

  it('leaves uncovered LEDs dark and truncates long strips', () => {
    const [c] = compileLayout(spec([strip([fixed(1)], 0), strip([fixed(9)], 1)]));
    expect(c.leds).toHaveLength(7);
    expect(c.leds.map((l) => Boolean(l.disabled))).toEqual([false, true, true, true, true, false, false]);
  });

  it('ignores strips on unknown controllers or outputs', () => {
    const stray = { ...strip([fixed(3)], 0), controllerId: 'nope' };
    const [c] = compileLayout(spec([stray, strip([fixed(2)], 7)]));
    expect(c.leds.every((l) => l.disabled)).toBe(true);
  });
});
