import { describe, expect, it } from 'vitest';
import {
  autoSection,
  defaultRoom,
  fromUnfolded,
  litLength,
  nearestOutline,
  outlineEdge,
  outlinePoint,
  shortWay,
  fromWorld,
  onSurface,
  pathLength,
  pathStations,
  pieceInUnfolded,
  placeLeds,
  SURFACES,
  surfaceSize,
  toUnfolded,
  toWorld,
} from './room';
import type { PathPoint, Room } from './types';

const room: Room = {
  width: 4,
  depth: 3,
  height: 2.5,
  screen: { x: 1.5, y: 1, width: 1, height: 0.5 },
  furniture: 'desk',
};

describe('surfaces', () => {
  it('maps every surface point to the room and back', () => {
    for (const surface of SURFACES) {
      const { w, h } = surfaceSize(room, surface);
      const p = { surface, x: w * 0.3, y: h * 0.7 };
      const back = fromWorld(room, toWorld(room, p), surface);
      expect(back.x).toBeCloseTo(p.x, 12);
      expect(back.y).toBeCloseTo(p.y, 12);
    }
  });

  it('joins the surfaces at the right edges', () => {
    // Front wall's left edge is the left wall's right edge (its front corner).
    expect(toWorld(room, { surface: 'front', x: 0, y: 1 })).toEqual(toWorld(room, { surface: 'left', x: 3, y: 1 }));
    // Front wall's top edge is the ceiling's bottom edge, its bottom edge the floor's top edge.
    expect(toWorld(room, { surface: 'front', x: 1, y: 0 })).toEqual(toWorld(room, { surface: 'ceiling', x: 1, y: 3 }));
    expect(toWorld(room, { surface: 'front', x: 1, y: 2.5 })).toEqual(toWorld(room, { surface: 'floor', x: 1, y: 0 }));
    // Right wall's back corner meets the back wall's left edge; the back wall's right edge meets the left wall.
    expect(toWorld(room, { surface: 'right', x: 3, y: 1 })).toEqual(toWorld(room, { surface: 'back', x: 0, y: 1 }));
    expect(toWorld(room, { surface: 'back', x: 4, y: 1 })).toEqual(toWorld(room, { surface: 'left', x: 0, y: 1 }));
  });

  it('finds a point on a neighboring surface only on their shared edge', () => {
    expect(onSurface(room, { surface: 'front', x: 1, y: 0 }, 'ceiling')).toMatchObject({ surface: 'ceiling', x: 1, y: 3 });
    expect(onSurface(room, { surface: 'front', x: 1, y: 0.2 }, 'ceiling')).toBeNull();
  });

  it('lays the surfaces out unfolded and finds them again', () => {
    for (const surface of SURFACES) {
      const p = { surface, x: 0.4, y: 0.3 };
      const u = toUnfolded(room, p);
      const back = fromUnfolded(room, u.x, u.y)!;
      expect(back.surface).toBe(surface);
      expect(back.x).toBeCloseTo(p.x, 12);
      expect(back.y).toBeCloseTo(p.y, 12);
    }
    expect(fromUnfolded(room, 0.1, 0.1)).toBeNull(); // the empty corner next to the ceiling
  });

  it('draws a piece across a fold through the shared edge', () => {
    const a = { surface: 'front' as const, x: 1, y: 0 };
    const b = { surface: 'ceiling' as const, x: 2, y: 2 };
    expect(pieceInUnfolded(room, a, b)).toEqual({ a: toUnfolded(room, { surface: 'ceiling', x: 1, y: 3 }), b: toUnfolded(room, b) });
    // Ceiling to left wall don't touch in the unfolded room: a jump unless one end is on the shared edge.
    expect(pieceInUnfolded(room, { surface: 'ceiling', x: 1, y: 1 }, { surface: 'left', x: 1, y: 1 })).toBeNull();
  });
});

describe('placing LEDs', () => {
  const line: PathPoint[] = [
    { surface: 'front', x: 3, y: 0.5 },
    { surface: 'front', x: 1, y: 0.5 },
  ];

  it('spaces LEDs evenly by length with the middle of LED k at k + 0.5', () => {
    expect(pathLength(room, line)).toBeCloseTo(2);
    const leds = placeLeds(room, { path: line, ledCount: 4 });
    expect(leds.map((l) => l.world.x)).toEqual([2.75, 2.25, 1.75, 1.25]);
  });

  it('spaces stations by length between matched points', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 0, y: 0 },
      { surface: 'front', x: 1, y: 0 },
      { surface: 'front', x: 3, y: 0, at: 10 },
      { surface: 'front', x: 4, y: 0 },
    ];
    pathStations(room, path, 20).forEach((s, i) => expect(s).toBeCloseTo([0, 10 / 3, 10, 20][i], 12));
  });

  it('keeps LEDs before the first matched point at the start', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 0, y: 0, at: 2 },
      { surface: 'front', x: 2, y: 0 },
    ];
    const leds = placeLeds(room, { path, ledCount: 4 });
    expect(leds.map((l) => l.world.x)).toEqual([0, 0, 0.5, 1.5]);
  });

  it('follows the strip around a fold', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 2, y: 1 },
      { surface: 'front', x: 2, y: 0 },
      { surface: 'ceiling', x: 2, y: 2 },
    ];
    const leds = placeLeds(room, { path, ledCount: 4 });
    expect(leds.map((l) => [l.world.y, l.world.z])).toEqual([
      [0, 1.75],
      [0, 2.25],
      [0.25, 2.5],
      [0.75, 2.5],
    ]);
  });
});

describe('gaps', () => {
  it('get no LEDs: the others spread over the lit sections', () => {
    const path: PathPoint[] = [
      { surface: 'front', x: 0, y: 0.5 },
      { surface: 'front', x: 1, y: 0.5, next: { kind: 'gap' } },
      { surface: 'front', x: 3, y: 0.5 },
      { surface: 'front', x: 4, y: 0.5 },
    ];
    expect(litLength(room, path)).toBeCloseTo(2);
    expect(pathStations(room, path, 4)).toEqual([0, 2, 2, 4]);
    expect(placeLeds(room, { path, ledCount: 4 }).map((l) => [l.piece, l.world.x])).toEqual([
      [0, 0.25],
      [0, 0.75],
      [2, 3.25],
      [2, 3.75],
    ]);
  });
});

describe('the screen outline', () => {
  const front = (x: number, y: number) => ({ surface: 'front' as const, x, y });

  it('numbers the outline clockwise from the top-left corner, one unit per edge', () => {
    expect(nearestOutline(room, 1.75, 1)).toBe(0.25);
    expect(nearestOutline(room, 2.5, 1.25)).toBe(1.5);
    expect(nearestOutline(room, 2.25, 1.5)).toBe(2.25);
    expect(nearestOutline(room, 1.5, 1.375)).toBe(3.25);
    expect([nearestOutline(room, 1.5, 1), nearestOutline(room, 2.5, 1), nearestOutline(room, 2.5, 1.5), nearestOutline(room, 1.5, 1.5)]).toEqual([0, 1, 2, 3]);
  });

  it('finds the closest outline point from outside and inside the screen', () => {
    expect(nearestOutline(room, 0.5, 0.2)).toBe(0); // up and left: the corner
    expect(nearestOutline(room, 2, 2.2)).toBe(2.5); // below the middle
    expect(nearestOutline(room, 1.6, 1.25)).toBe(3.5); // inside, near the left edge
  });

  it('turns outline positions into edges and wall positions', () => {
    expect(outlineEdge(0.25)).toEqual({ edge: 'top', pos: 0.25 });
    expect(outlineEdge(2.25)).toEqual({ edge: 'bottom', pos: 0.75 });
    expect(outlineEdge(4.25)).toEqual({ edge: 'top', pos: 0.25 });
    expect(outlineEdge(-0.5)).toEqual({ edge: 'left', pos: 0.5 });
    expect(outlinePoint(room, 1.5)).toEqual({ x: 2.5, y: 1.25 });
    expect(shortWay(3.9, 0.1)).toBeCloseTo(4.1);
  });

  it('gives each section the stretch it faces', () => {
    // On the screen's top border: that part of the top edge.
    expect(autoSection(room, front(1.75, 1), front(2.25, 1))).toEqual({ kind: 'edge', from: 0.25, to: 0.75 });
    // Across the ceiling, right to left: the whole top edge.
    expect(autoSection(room, { surface: 'ceiling', x: 3.8, y: 2.8 }, { surface: 'ceiling', x: 0.2, y: 2.8 })).toEqual({ kind: 'edge', from: 1, to: 0 });
    // Behind the desk from the right end to the middle: the bottom edge from its right corner to the middle.
    expect(autoSection(room, front(3, 1.8), front(2, 1.8))).toEqual({ kind: 'edge', from: 2, to: 2.5 });
    // Around the top-left corner: the short way.
    expect(autoSection(room, front(1.5, 1.1), front(1.6, 1))).toEqual({ kind: 'edge', from: 3.8, to: expect.closeTo(4.1, 10) });
  });

  it('works for the default rooms', () => {
    for (const r of [defaultRoom('desk'), defaultRoom('tv')]) {
      const s = r.screen;
      expect(nearestOutline(r, s.x + s.width / 2, s.y - 0.5)).toBeCloseTo(0.5);
    }
  });
});
