import { describe, expect, it } from 'vitest';
import type { Strip } from '../../layout';
import { LANE_BASE, nearestEdge, stripDots } from '../geometry';

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
