import { describe, expect, it } from 'vitest';
import { aroundScreen, ceilingAround, defaultRoom, shapePath, shapes, type PathPoint, type ScreenCorner } from './index';

const room = defaultRoom();
const { x, y, width: w, height: h } = room.screen;
const key = (p: PathPoint) => `${p.surface}:${p.x.toFixed(3)},${p.y.toFixed(3)}`;

describe('aroundScreen', () => {
  const corners: Array<[ScreenCorner, PathPoint]> = [
    ['top-left', { surface: 'front', x, y }],
    ['top-right', { surface: 'front', x: x + w, y }],
    ['bottom-right', { surface: 'front', x: x + w, y: y + h }],
    ['bottom-left', { surface: 'front', x, y: y + h }],
    ['bottom-middle', { surface: 'front', x: x + w / 2, y: y + h }],
  ];

  it.each(corners)('starts at the %s corner and closes the ring there', (start, corner) => {
    const path = aroundScreen(room, { start, clockwise: true });
    expect(path[0]).toEqual(corner);
    expect(path[path.length - 1]).toEqual(corner);
    // the closing point is a copy: moving one end doesn't drag the other
    expect(path[path.length - 1]).not.toBe(path[0]);
    // every screen corner is visited once
    expect(new Set(path.slice(0, -1).map(key)).size).toBe(path.length - 1);
  });

  it('runs the other way round counterclockwise, from the same start', () => {
    for (const [start] of corners) {
      const cw = aroundScreen(room, { start, clockwise: true });
      const ccw = aroundScreen(room, { start, clockwise: false });
      expect(ccw.map(key)).toEqual([...cw].reverse().map(key));
    }
  });

  it('goes clockwise as seen from the seat: right along the top edge', () => {
    const [first, second] = aroundScreen(room, { start: 'top-left', clockwise: true });
    expect(second.x).toBeGreaterThan(first.x);
    expect(second.y).toBe(first.y);
  });
});

describe('ceilingAround', () => {
  it('is a closed rectangle inset from the ceiling edges, starting front right', () => {
    const path = ceilingAround(room);
    expect(path).toHaveLength(5);
    expect(path.every((p) => p.surface === 'ceiling')).toBe(true);
    expect(path[0]).toEqual({ surface: 'ceiling', x: room.width - 0.15, y: room.depth - 0.15 });
    expect(path[4]).toEqual(path[0]);
    expect(path.every((p) => p.x > 0 && p.x < room.width && p.y > 0 && p.y < room.depth)).toBe(true);
  });

  it('keeps the inset inside a tiny ceiling', () => {
    const tiny = { ...room, width: 0.2, depth: 0.4 };
    const path = ceilingAround(tiny);
    expect(path.every((p) => p.x >= 0 && p.x <= 0.2 && p.y >= 0 && p.y <= 0.4)).toBe(true);
    expect(path[0].x).toBeCloseTo(0.15);
    expect(path[1].x).toBeCloseTo(0.05);
  });
});

describe('shapes', () => {
  it('name the screen and the furniture the room has', () => {
    expect(shapes(defaultRoom('tv')).slice(0, 2).map((s) => s.name)).toEqual(['Around the TV', 'Behind the TV stand']);
    expect(shapes(defaultRoom('desk')).slice(0, 2).map((s) => s.name)).toEqual(['Around the monitor', 'Behind the desk']);
    expect(shapes({ ...room, furniture: 'none' }).map((s) => s.id)).toEqual(['around-screen', 'behind-furniture', 'ceiling-front', 'ceiling-around', 'line']);
  });

  it('every shape has a path of at least two points on the walls of the room', () => {
    for (const { id } of shapes(room)) {
      const path = shapePath(room, id);
      expect(path.length, id).toBeGreaterThanOrEqual(2);
      expect(path.every((p) => p.x >= 0 && p.y >= 0 && p.x <= room.width && p.y <= Math.max(room.height, room.depth)), id).toBe(true);
    }
    expect(shapePath(room, 'ceiling-around')).toEqual(ceilingAround(room));
  });
});
