import { describe, expect, it } from 'vitest';
import type { Strip } from '../../layout';
import spec from '../../layout/__fixtures__/my-layout.spec.json';
import type { LayoutSpec } from '../../layout';
import { assignLanes, LANE_BASE, LANE_STEP, nearestEdge, stripDots } from '../geometry';

const box = { x: 100, y: 100, w: 1000, h: 500 };
const strip = (segments: Strip['segments']): Strip => ({ id: 's', name: 's', controllerId: 'c', output: 0, segments });

describe('stripDots', () => {
  it('places edge LEDs just outside the screen, in wiring order', () => {
    const dots = stripDots(strip([{ kind: 'edge', count: 4, edge: 'bottom', from: 1, to: 0, depth: 0.1 }]), box);
    expect(dots.map((d) => d.x)).toEqual([975, 725, 475, 225]); // right → left
    expect(dots.every((d) => d.y === box.y + box.h + LANE_BASE)).toBe(true);
  });

  it('interpolates dark LEDs between their neighbors', () => {
    const dots = stripDots(
      strip([
        { kind: 'edge', count: 1, edge: 'top', from: 0, to: 0.2, depth: 0.1 },
        { kind: 'off', count: 1 },
        { kind: 'edge', count: 1, edge: 'top', from: 0.4, to: 0.6, depth: 0.1 },
      ]),
      box,
    );
    expect(dots[1].kind).toBe('off');
    expect(dots[1].x).toBeCloseTo((dots[0].x + dots[2].x) / 2);
    expect(dots.every((d) => Number.isFinite(d.x) && Number.isFinite(d.y))).toBe(true);
  });

  it('places trailing dark LEDs past the last lit one', () => {
    const dots = stripDots(strip([{ kind: 'edge', count: 3, edge: 'left', from: 0, to: 1, depth: 0.1 }, { kind: 'off', count: 2 }]), box);
    expect(dots[3].y).toBeGreaterThan(dots[2].y);
    expect(dots[4].y).toBeGreaterThan(dots[3].y);
  });

  it('nearestEdge picks the closest side of a fixed area', () => {
    expect(nearestEdge({ hmin: 0.4, hmax: 0.6, vmin: 0.85, vmax: 1 })).toEqual({ edge: 'bottom', t: 0.5 });
  });
});

describe('assignLanes', () => {
  it('puts runs that share an edge in separate lanes, innermost first', () => {
    const s = strip([
      { kind: 'edge', count: 5, edge: 'bottom', from: 1, to: 0.5, depth: 0.1 },
      { kind: 'edge', count: 5, edge: 'bottom', from: 0.5, to: 0, depth: 0.1 },
      { kind: 'edge', count: 5, edge: 'bottom', from: 0.2, to: 0.8, depth: 0.1 },
      { kind: 'edge', count: 5, edge: 'top', from: 0, to: 1, depth: 0.1 },
    ]);
    const { lanes, count } = assignLanes([s]);
    expect(lanes.get('s')).toEqual([0, 0, 1, 0]); // touching runs share lane 0; the overlapping one moves out
    expect(count).toBe(2);
    const dots = stripDots(s, box, lanes.get('s'));
    expect(dots[10].y).toBe(box.y + box.h + LANE_BASE + LANE_STEP);
  });

  it('gives overlapping runs of the real layout different lanes', () => {
    const layout = spec as LayoutSpec;
    const { lanes, count } = assignLanes(layout.strips);
    const runs = layout.strips.flatMap((st) =>
      st.segments.flatMap((seg, i) =>
        seg.kind === 'edge' ? [{ edge: seg.edge, lo: Math.min(seg.from, seg.to), hi: Math.max(seg.from, seg.to), lane: lanes.get(st.id)![i] }] : [],
      ),
    );
    for (const a of runs)
      for (const b of runs)
        if (a !== b && a.edge === b.edge && a.lo < b.hi - 1e-6 && b.lo < a.hi - 1e-6) expect(a.lane).not.toBe(b.lane);
    expect(count).toBeGreaterThan(2); // the monitor strip doubles back along the bottom
  });
});
