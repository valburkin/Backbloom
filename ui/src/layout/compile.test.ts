import { describe, expect, it } from 'vitest';
import { compileLayout, compileStrip, compileZones, darkMask, DISABLED_RECT, stripLedCount } from './compile';
import type { LayoutSpec, PathPoint, Room, Strip } from './types';

const room: Room = {
  width: 4,
  depth: 3,
  height: 2.5,
  screen: { x: 1.5, y: 1, width: 1, height: 0.5 },
  furniture: 'desk',
};

/** Along the screen's top border, left to right. */
const topBorder: PathPoint[] = [
  { surface: 'front', x: 1.5, y: 1 },
  { surface: 'front', x: 2.5, y: 1 },
];

const strip = (over: Partial<Strip> = {}): Strip => ({
  id: 's',
  name: 'Strip',
  controllerId: 'c',
  output: 0,
  ledCount: 4,
  path: topBorder,
  ...over,
});

describe('compileStrip', () => {
  it('maps a strip on the screen border to evenly spaced rects along that edge', () => {
    const rects = compileStrip(strip({ blend: 1 }), room);
    // Centers 0.125, 0.375...; half width 1 * 0.25 / 2; depth 0.1 of the height.
    expect(rects).toEqual([
      { hmin: 0, hmax: 0.25, vmin: 0, vmax: 0.1, group: 0 },
      { hmin: 0.25, hmax: 0.5, vmin: 0, vmax: 0.1, group: 0 },
      { hmin: 0.5, hmax: 0.75, vmin: 0, vmax: 0.1, group: 0 },
      { hmin: 0.75, hmax: 1, vmin: 0, vmax: 0.1, group: 0 },
    ]);
  });

  it('uses the default blend of 1.2 and clamps to the screen', () => {
    const rects = compileStrip(strip(), room);
    expect(rects[1]).toEqual({ hmin: 0.225, hmax: 0.525, vmin: 0, vmax: 0.1, group: 0 });
    expect(rects[0].hmin).toBe(0);
  });

  it('measures depth in screen heights, so the sides reach the same distance into the picture', () => {
    const right: PathPoint[] = [
      { surface: 'front', x: 2.5, y: 1 },
      { surface: 'front', x: 2.5, y: 1.5 },
    ];
    const [r] = compileStrip(strip({ ledCount: 1, path: right, depth: 0.2 }), room);
    expect(r).toMatchObject({ hmin: 0.9, hmax: 1 });
  });

  it('keeps dark ranges dark', () => {
    const rects = compileStrip(strip({ dark: [{ start: 1, count: 2 }] }), room);
    expect(rects.map((r) => !!r.disabled)).toEqual([false, true, true, false]);
    expect(rects[1]).toEqual(DISABLED_RECT);
  });

  it('shows the whole picture, one area or nothing per section', () => {
    const area = { hmin: 0.4, hmax: 0.6, vmin: 0.8, vmax: 1 };
    const path: PathPoint[] = [
      { surface: 'front', x: 0, y: 0.5, next: { kind: 'whole' } },
      { surface: 'front', x: 1, y: 0.5, next: { kind: 'area', area } },
      { surface: 'front', x: 2, y: 0.5, next: { kind: 'none' } },
      { surface: 'front', x: 3, y: 0.5 },
    ];
    expect(compileStrip(strip({ ledCount: 3, path }), room)).toEqual([
      { hmin: 0, hmax: 1, vmin: 0, vmax: 1, group: 0 },
      { ...area, group: 0 },
      DISABLED_RECT,
    ]);
  });

  it('repeats a chosen stretch of the outline, across corners', () => {
    const path: PathPoint[] = [
      { surface: 'ceiling', x: 1, y: 1, next: { kind: 'edge', from: 3.75, to: 4.25 } },
      { surface: 'ceiling', x: 3, y: 1 },
    ];
    const [a, b] = compileStrip(strip({ ledCount: 2, path, blend: 1 }), room);
    // Left edge near the top, then the top edge near the left.
    expect(a).toMatchObject({ hmin: 0, vmin: 0, vmax: 0.25 });
    expect(b).toMatchObject({ vmin: 0, hmin: 0, hmax: 0.25 });
  });

  it('spreads the LEDs over the lit sections only', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 1.5, y: 1 },
      { surface: 'front', x: 2, y: 1, next: { kind: 'gap' } },
      { surface: 'front', x: 2.5, y: 1.5 },
      { surface: 'front', x: 2, y: 1.5 },
    ];
    const rects = compileStrip(strip({ ledCount: 4, path, blend: 1 }), room);
    // Two LEDs on the top edge's left half, two on the bottom edge's right half (right to left).
    expect(rects.map((r) => [r.hmin, r.hmax, r.vmin, r.vmax])).toEqual([
      [0, 0.25, 0, 0.1],
      [0.25, 0.5, 0, 0.1],
      [0.75, 1, 0.9, 1],
      [0.5, 0.75, 0.9, 1],
    ]);
  });

  it('produces nothing for invalid counts', () => {
    expect(compileStrip(strip({ ledCount: 0 }), room)).toEqual([]);
    expect(compileStrip(strip({ ledCount: NaN }), room)).toEqual([]);
    expect(stripLedCount({ ledCount: 3.7 })).toBe(3);
  });

  it('builds the dark mask from ranges, ignoring LEDs past the end', () => {
    expect(darkMask({ ledCount: 4, dark: [{ start: 3, count: 5 }] })).toEqual([false, false, false, true]);
  });
});

describe('compileLayout', () => {
  const spec: LayoutSpec = {
    version: 1,
    room,
    controllers: [
      {
        id: 'c',
        name: 'C',
        kind: 'wled',
        host: 'c',
        outputs: [
          { index: 0, start: 0, length: 3 },
          { index: 1, start: 3, length: 4 },
        ],
      },
    ],
    strips: [strip({ output: 1, ledCount: 2 }), strip({ id: 't', output: 0, ledCount: 5 })],
  };

  it('places each strip at its output, truncates long strips and leaves the rest dark', () => {
    const [c] = compileLayout(spec);
    expect(c.leds).toHaveLength(7);
    expect(c.leds.map((r) => !!r.disabled)).toEqual([false, false, false, false, false, true, true]);
    // The 5-LED strip is cut to its 3-LED output: its first three rects.
    expect(c.leds.slice(0, 3)).toEqual(compileStrip(spec.strips[1], room).slice(0, 3));
  });
});

describe('compileZones', () => {
  const two = (over: Partial<Strip>, output: number): Strip => strip({ id: `s${output}`, output, ...over });
  const controller = { id: 'c', name: 'C', kind: 'wled', host: 'c', outputs: [{ index: 0, start: 0, length: 4 }, { index: 1, start: 4, length: 4 }] };
  const effect = { kind: 'effect' as const, effect: 'Candle' };

  it('gathers the LEDs of every section with the same role, keeping them out of the screen mapping', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 1.5, y: 1, next: effect },
      { surface: 'front', x: 2, y: 1 },
      { surface: 'front', x: 2.5, y: 1 },
    ];
    const spec: LayoutSpec = {
      version: 1,
      room,
      controllers: [controller],
      strips: [two({ path, dark: [{ start: 0, count: 1 }] }, 0), two({ path: [{ ...topBorder[0], next: effect }, topBorder[1]] }, 1)],
    };
    const [zone] = compileZones(spec);
    // LED 0 is dark; LEDs 1 of the first strip and all four of the second play the effect.
    expect(zone.role).toEqual(effect);
    expect(zone.leds).toEqual([1, 4, 5, 6, 7]);
    // Their zone rects are where 'auto' would put them (LED 1: the second of two on the top edge's left half).
    expect(zone.rects[0]).toMatchObject({ vmin: 0, hmin: 0.225, hmax: 0.525 });
    const [c] = compileLayout(spec);
    expect(c.leds.slice(0, 2).every((r) => r.disabled)).toBe(true);
    expect(c.leds[2].disabled).toBeUndefined();
    expect(c.leds.slice(4).every((r) => r.disabled)).toBe(true);
  });

  it('keeps different colors apart', () => {
    const path = (color: [number, number, number]): PathPoint[] => [{ ...topBorder[0], next: { kind: 'color', color } }, topBorder[1]];
    const spec: LayoutSpec = { version: 1, room, controllers: [controller], strips: [two({ path: path([255, 0, 0]) }, 0), two({ path: path([0, 0, 255]) }, 1)] };
    expect(compileZones(spec).map((z) => [z.role, z.leds])).toEqual([
      [{ kind: 'color', color: [255, 0, 0] }, [0, 1, 2, 3]],
      [{ kind: 'color', color: [0, 0, 255] }, [4, 5, 6, 7]],
    ]);
  });
});
