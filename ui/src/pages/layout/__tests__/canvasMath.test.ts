import { describe, expect, it } from 'vitest';
import { defaultRoom, furnitureTop, onSurface, toUnfolded, type Room } from '../../../layout';
import { appendPoint, CORNER_REACH, fitView, foldCrossing, frontFocus, snapAt, surfaceRect, viewBox, zoomAt } from '../canvasMath';
import { BASEBOARD, furnitureGeometry, furnitureSnaps } from '../furniture';

const room: Room = {
  width: 4,
  depth: 3,
  height: 2.5,
  screen: { x: 1.5, y: 1, width: 1, height: 0.5 },
  furniture: 'desk',
};
const front = surfaceRect(room, 'front');

describe('view', () => {
  it('fits an area and keeps the point under the cursor while zooming', () => {
    const v = fitView(frontFocus(room), 800, 600, 0);
    const box = viewBox(v, 800, 600);
    expect(box.x).toBeLessThanOrEqual(front.x);
    expect(box.x + box.w).toBeGreaterThanOrEqual(front.x + front.w);
    const before = { x: box.x + 200 / v.scale, y: box.y + 150 / v.scale };
    const z = zoomAt(v, 2, 200, 150, 800, 600);
    const after = viewBox(z, 800, 600);
    expect(after.x + 200 / z.scale).toBeCloseTo(before.x);
    expect(after.y + 150 / z.scale).toBeCloseTo(before.y);
  });
});

describe('snapAt', () => {
  const at = (x: number, y: number) => ({ x: front.x + x, y: front.y + y });

  it('snaps to the screen corners, the screen border and the furniture', () => {
    expect(snapAt(room, at(1.51, 1.01).x, at(1.51, 1.01).y, 0.03)).toEqual({ point: { surface: 'front', x: 1.5, y: 1 }, to: 'corner' });
    const border = snapAt(room, at(1.8, 1.52).x, at(1.8, 1.52).y, 0.03);
    expect(border).toMatchObject({ to: 'line', point: { y: 1.5 } });
    const top = furnitureTop(room)!;
    const desk = snapAt(room, at(top.x + 0.2, top.y - 0.01).x, at(top.x + 0.2, top.y - 0.01).y, 0.03);
    expect(desk.point.y).toBeCloseTo(top.y);
  });

  it('snaps to other points first, also across a fold', () => {
    const p = snapAt(room, at(0.4, 0.02).x, at(0.4, 0.02).y, 0.05, { points: [{ surface: 'ceiling', x: 0.41, y: 3 }] });
    expect(p).toMatchObject({ to: 'point', point: { surface: 'front', x: 0.41, y: 0 } });
  });

  it('aligns with a neighbor and lets go with free', () => {
    const aligned = snapAt(room, at(0.5, 0.72).x, at(0.5, 0.72).y, 0.03, { align: [{ surface: 'front', x: 0.9, y: 0.7 }] });
    expect(aligned).toMatchObject({ to: 'align', point: { y: 0.7 } });
    expect(snapAt(room, at(1.51, 1.01).x, at(1.51, 1.01).y, 0.03, { free: true }).to).toBeNull();
  });

  it('puts positions outside every surface on the nearest one', () => {
    expect(snapAt(room, 0.1, 0.1, 0).point.surface).toBe('left');
    expect(snapAt(room, 0.1, 0.1, 0, { fallback: 'ceiling' }).point).toEqual({ surface: 'ceiling', x: 0, y: 0.1 });
  });
});

describe('snapping to the furniture as drawn', () => {
  const near = (r: Room, surface: 'front' | 'floor', x: number, y: number, tolerance = 0.03, opts = {}) => {
    const s = surfaceRect(r, surface);
    return snapAt(r, s.x + x, s.y + y, tolerance, opts);
  };
  const desk = furnitureGeometry(room)!.front;
  const tvRoom = defaultRoom('tv');
  const tv = furnitureGeometry(tvRoom)!.front;

  it('draws and snaps from the same numbers', () => {
    const top = furnitureTop(room)!;
    expect(desk.top).toMatchObject({ x: top.x, y: top.y, w: top.width });
    const corners = furnitureSnaps(room, 'front').corners;
    for (const c of [
      { x: desk.top.x, y: desk.top.y },
      { x: desk.top.x + desk.top.w, y: desk.top.y },
      { x: desk.top.x, y: desk.top.y + desk.top.h },
      { x: desk.top.x + desk.top.w, y: desk.top.y + desk.top.h },
    ]) {
      expect(corners).toContainEqual(c);
    }
  });

  it('snaps to the corners of the desk top, front and back', () => {
    const t = desk.top;
    expect(near(room, 'front', t.x - 0.01, t.y - 0.012)).toEqual({ point: { surface: 'front', x: t.x, y: t.y }, to: 'corner' });
    expect(near(room, 'front', t.x + t.w + 0.008, t.y + t.h + 0.01)).toEqual({ point: { surface: 'front', x: t.x + t.w, y: t.y + t.h }, to: 'corner' });
  });

  it('snaps onto the desk top, the edge under it and the rail under that', () => {
    const t = desk.top;
    const onTop = near(room, 'front', t.x + 0.3, t.y - 0.02);
    expect(onTop).toMatchObject({ to: 'line', point: { y: t.y } });
    expect(onTop.point.x).toBeCloseTo(t.x + 0.3, 9);
    expect(near(room, 'front', t.x + 0.3, t.y + t.h + 0.006)).toMatchObject({ to: 'line', point: { y: t.y + t.h } });
    const railBottom = desk.body.y + desk.body.h;
    expect(near(room, 'front', t.x + 0.4, railBottom + 0.02)).toMatchObject({ to: 'line', point: { y: railBottom } });
    expect(near(room, 'front', desk.body.x + 0.01, railBottom + 0.01)).toEqual({ point: { surface: 'front', x: desk.body.x, y: railBottom }, to: 'corner' });
  });

  it("snaps onto the monitor stand's neck and where it meets the foot", () => {
    const { neck, foot } = desk.stand!;
    const nx = neck.x + neck.w / 2;
    expect(near(room, 'front', nx + 0.02, (room.screen.y + room.screen.height + foot.y) / 2)).toMatchObject({ to: 'line', point: { x: nx } });
    expect(near(room, 'front', nx + 0.01, foot.y - 0.01)).toEqual({ point: { surface: 'front', x: nx, y: foot.y }, to: 'corner' });
  });

  it("snaps to the TV console's top, its corners and the bottom of the cabinet", () => {
    const t = tv.top;
    expect(near(tvRoom, 'front', t.x + t.w - 0.015, t.y - 0.02)).toEqual({ point: { surface: 'front', x: t.x + t.w, y: t.y }, to: 'corner' });
    expect(near(tvRoom, 'front', t.x + 0.6, t.y + 0.012)).toMatchObject({ to: 'line', point: { y: t.y } });
    const b = tv.body;
    expect(near(tvRoom, 'front', b.x + b.w + 0.01, b.y + b.h + 0.012)).toEqual({ point: { surface: 'front', x: b.x + b.w, y: b.y + b.h }, to: 'corner' });
    // Hung on the wall high above the console; standing on it when just above.
    expect(tv.stand).toBeNull();
    expect(tv.mount).not.toBeNull();
    const low: Room = { ...tvRoom, screen: { ...tvRoom.screen, y: t.y - 0.012 - 0.06 - tvRoom.screen.height } };
    const { neck, foot } = furnitureGeometry(low)!.front.stand!;
    const nx = neck.x + neck.w / 2;
    expect(near(low, 'front', nx - 0.01, foot.y - 0.008)).toEqual({ point: { surface: 'front', x: nx, y: foot.y }, to: 'corner' });
  });

  it('snaps to the footprint of the furniture on the floor', () => {
    const f = furnitureGeometry(room)!.floor.footprint;
    expect(near(room, 'floor', f.x + f.w + 0.012, f.y + f.h + 0.015)).toEqual({ point: { surface: 'floor', x: f.x + f.w, y: f.y + f.h }, to: 'corner' });
    expect(near(room, 'floor', f.x + 0.5, f.y + f.h - 0.02)).toMatchObject({ to: 'line', point: { y: f.h } });
  });

  it('snaps to the baseboard and to the corners of every surface', () => {
    expect(near(room, 'front', 0.8, room.height - BASEBOARD - 0.01)).toMatchObject({ to: 'line', point: { y: room.height - BASEBOARD } });
    expect(near(room, 'front', room.width - 0.02, 0.015)).toEqual({ point: { surface: 'front', x: room.width, y: 0 }, to: 'corner' });
    const ceiling = surfaceRect(room, 'ceiling');
    expect(snapAt(room, ceiling.x + 0.01, ceiling.y + 0.02, 0.03)).toEqual({ point: { surface: 'ceiling', x: 0, y: 0 }, to: 'corner' });
  });

  it('prefers a corner over a nearer line, up to CORNER_REACH times the tolerance', () => {
    const t = desk.top;
    // 1 cm from the top edge, 3.5 cm from its end: past the line tolerance for the end, within the corner's reach.
    expect(near(room, 'front', t.x + 0.035, t.y - 0.01)).toEqual({ point: { surface: 'front', x: t.x, y: t.y }, to: 'corner' });
    expect(near(room, 'front', t.x + 0.03 * CORNER_REACH + 0.01, t.y - 0.02).to).toBe('line');
  });

  it('takes the nearest of other points and corners', () => {
    const t = desk.top;
    const other = { surface: 'front' as const, x: t.x + 0.025, y: t.y };
    expect(near(room, 'front', t.x + 0.004, t.y, 0.03, { points: [other] })).toMatchObject({ to: 'corner', point: { x: t.x } });
    expect(near(room, 'front', t.x + 0.022, t.y, 0.03, { points: [other] })).toMatchObject({ to: 'point', point: { x: other.x } });
  });

  it('slides along a line to line up with a neighbor', () => {
    const t = desk.top;
    const neighbor = { surface: 'front' as const, x: t.x + 0.5, y: 1.2 };
    expect(near(room, 'front', t.x + 0.51, t.y - 0.01, 0.03, { align: [neighbor] })).toEqual({ point: { surface: 'front', x: t.x + 0.5, y: t.y }, to: 'line' });
  });

  it('snaps to corners on the surface across a fold', () => {
    // On the left wall, within reach of the corner of a desk pushed against it.
    const pushed: Room = { ...room, screen: { ...room.screen, x: 0.1 } };
    const top = furnitureGeometry(pushed)!.front.top;
    expect(top.x).toBe(0);
    const left = surfaceRect(pushed, 'left');
    const p = snapAt(pushed, left.x + left.w - 0.01, left.y + top.y + top.h + 0.01, 0.03);
    expect(p.to).toBe('corner');
    expect(toUnfolded(pushed, p.point)).toEqual(toUnfolded(pushed, { surface: 'front', x: 0, y: top.y + top.h }));
  });
});

describe('drawing across folds', () => {
  it('adds a bend on the shared edge when a path crosses onto a touching surface', () => {
    const a = { surface: 'front' as const, x: 0.5, y: 1 };
    const b = { surface: 'left' as const, x: 2.5, y: 1 };
    const fold = foldCrossing(room, a, b)!;
    expect(fold).toMatchObject({ surface: 'front', x: 0, y: 1 });
    expect(onSurface(room, fold, 'left')).toMatchObject({ x: 3, y: 1 });
    expect(appendPoint(room, [a], b)).toEqual([a, fold, b]);
  });

  it('adds nothing on one surface, when an end is on the edge, or between surfaces that only meet in the real room', () => {
    const a = { surface: 'front' as const, x: 0.5, y: 1 };
    expect(appendPoint(room, [a], { surface: 'front', x: 1, y: 1 })).toHaveLength(2);
    expect(foldCrossing(room, { surface: 'front', x: 0, y: 1 }, { surface: 'left', x: 2, y: 1 })).toBeNull();
    expect(foldCrossing(room, { surface: 'ceiling', x: 0.5, y: 1 }, { surface: 'left', x: 1, y: 1 })).toBeNull();
  });
});

describe('snapping onto other points', () => {
  it('takes only the position, never the pin or section of the point it snaps to', () => {
    const pinned = { surface: 'front' as const, x: 1.2, y: 0.8, at: 17.5, next: { kind: 'gap' as const } };
    const r = snapAt(room, front.x + 1.21, front.y + 0.81, 0.05, { points: [pinned] });
    expect(r).toEqual({ to: 'point', point: { surface: 'front', x: 1.2, y: 0.8 } });
  });
});
