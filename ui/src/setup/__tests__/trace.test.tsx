import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { MockBackend } from '../../api/mock';
import { ConnectionProvider } from '../../app/connection';
import { ToastProvider } from '../../app/toast';
import { defaultRoom, furnitureTop, sectionAt, validateLayout, type LayoutSpec } from '../../layout';
import { boardStart, checkLeds, fromBoard, ledSpots, pinRange, pointLeds, routePath, snapFront, traceCrop, type TracePoint } from '../trace';
import { TraceStrip, type TraceFrame } from '../TraceStrip';

const room = defaultRoom();
const s = room.screen;
const desk = furnitureTop(room)!;
const bottom = s.y + s.height;
const pt = (x: number, y: number): TracePoint => ({ x, y, where: '' });

/** The owner's real strip: from the desk's right end left to under the monitor, up and around it, on along the desk. */
const owners: TracePoint[] = [
  pt(desk.x + desk.width, desk.y),
  pt(s.x + s.width / 2, desk.y),
  pt(s.x + s.width / 2, bottom),
  pt(s.x + s.width, bottom),
  pt(s.x + s.width, s.y),
  pt(s.x, s.y),
  pt(s.x, bottom),
  pt(s.x, desk.y),
  pt(desk.x, desk.y),
];

describe('Trace your strip', () => {
  it('snaps clicks to the screen’s corners and edges, the desk’s back edge, and the board for the start', () => {
    expect(snapFront(room, s.x + 0.01, s.y - 0.01, 0.02)).toEqual({ x: s.x, y: s.y, where: 'the monitor’s top-left corner' });
    expect(snapFront(room, s.x + 0.2, bottom + 0.015, 0.02)).toEqual({ x: s.x + 0.2, y: bottom, where: 'the monitor’s bottom edge' });
    expect(snapFront(room, desk.x + desk.width - 0.01, desk.y + 0.01, 0.02)).toMatchObject({ x: desk.x + desk.width, y: desk.y, where: 'the desk’s right end' });
    expect(snapFront(room, desk.x + 0.4, desk.y - 0.012, 0.02)).toMatchObject({ x: desk.x + 0.4, y: desk.y, where: 'the desk’s back edge' });
    expect(snapFront(room, s.x - 0.1, s.y - 0.1, 0.02)).toEqual({ x: s.x - 0.1, y: s.y - 0.1, where: 'the wall' });
    const board = { x: desk.x + 0.3, y: desk.y };
    expect(snapFront(room, board.x + 0.01, board.y, 0.02, board)).toMatchObject({ ...board, where: 'the board' });
    // the board starts below the screen's middle, on the desk; the drawing shows the screen and the whole desk
    expect(boardStart(room)).toEqual({ x: s.x + s.width / 2, y: desk.y });
    const crop = traceCrop(room);
    expect(crop.x).toBeLessThan(desk.x);
    expect(crop.x + crop.w).toBeGreaterThan(desk.x + desk.width);
    expect(crop.y).toBeLessThan(s.y);
  });

  it('puts LED 1 at the end nearest the board', () => {
    const near = { x: desk.x + desk.width, y: desk.y };
    expect(fromBoard(owners, near)[0]).toEqual(owners[0]);
    const reversed = fromBoard(owners, { x: desk.x, y: desk.y });
    expect(reversed[0]).toEqual(owners[owners.length - 1]);
    expect(reversed[reversed.length - 1]).toEqual(owners[0]);
  });

  it('spreads the strip’s LEDs evenly along the drawn route, as Layout does, and lights its corners', () => {
    const path = routePath(owners);
    expect(path.every((p) => p.surface === 'front' && p.at === undefined && p.next === undefined)).toBe(true);
    const at = pointLeds(room, path, 91);
    expect(at[0]).toBe(1);
    expect(at[at.length - 1]).toBe(91);
    // in order along the strip, each corner its own LED
    expect(at.every((n, i) => i === 0 || n > at[i - 1])).toBe(true);
    expect(checkLeds(at)).toEqual(at);
    expect(checkLeds([1, 1, 5])).toEqual([1, 5]);
    expect(ledSpots(room, path, 91)).toHaveLength(91);
    // up the right edge, then right to left along the top: the way it really runs (counterclockwise from the seat)
    expect(sectionAt(room, path, 3)).toEqual({ kind: 'edge', from: 2, to: 1 });
    expect(sectionAt(room, path, 4)).toEqual({ kind: 'edge', from: 1, to: 0 });
  });

  it('moves a corner’s LED onto it: that corner is matched, the rest spread between matched corners', () => {
    const at = pointLeds(room, routePath(owners), 91);
    expect(pinRange(at, 3, 91)).toEqual({ min: at[2] + 1, max: at[4] - 1 });
    expect(pinRange(at, 0, 91).min).toBe(1);
    const pinned = { 3: at[3] + 2 };
    const path = routePath(owners, pinned);
    expect(path[3].at).toBe(at[3] + 1.5);
    const moved = pointLeds(room, path, 91);
    expect(moved[3]).toBe(at[3] + 2);
    expect(moved[0]).toBe(1);
    expect(moved[moved.length - 1]).toBe(91);
    const spec: LayoutSpec = {
      version: 1,
      room,
      controllers: [{ id: 'wled', name: 'Monitor and desk', kind: 'wled', host: '192.168.1.77', outputs: [{ index: 0, start: 0, length: 91 }] }],
      strips: [{ id: 'strip-1', name: 'Monitor and desk', controllerId: 'wled', output: 0, ledCount: 91, path }],
    };
    expect(validateLayout(spec).filter((i) => i.severity === 'error')).toEqual([]);
  });

  it('starts with the board on the drawing and the route to draw', () => {
    let got: Parameters<TraceFrame>[0] | null = null;
    const frame: TraceFrame = (p) => {
      got = p;
      return <>{p.children}</>;
    };
    const html = renderToStaticMarkup(
      <ToastProvider>
        <ConnectionProvider backend={new MockBackend()}>
          <TraceStrip frame={frame} room={room} name="Monitor and desk" host="192.168.1.77" output={0} leds={91} onDone={() => {}} onCancel={() => {}} />
        </ConnectionProvider>
      </ToastProvider>,
    );
    const p = got!;
    expect(p.title).toBe('Trace your strip');
    expect(String(p.intro)).toContain('Drag the board');
    expect(html).toContain('Your monitor and desk from the front');
    expect(html).toContain('>Board<');
    expect(html).toContain('spreads the strip’s');
    const actions = renderToStaticMarkup(<>{p.actions}</>);
    expect(actions).toContain('Cancel');
    expect(actions).toMatch(/disabled=""[^>]*>Next: check the corners/);
  });
});
