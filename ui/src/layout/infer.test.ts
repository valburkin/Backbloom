import { describe, expect, it } from 'vitest';
import { compileLayout, compileStrip } from './compile';
import { inferSegments, specFromLegacy } from './infer';
import { validateLayout } from './validate';
import { controllerFromWledConfig } from './wled';
import type { LayoutSpec, LedRect, Segment } from './types';
import legacy from './__fixtures__/my-layout.json';
import cfg from './__fixtures__/wled-two-outputs.json';
import specJson from './__fixtures__/my-layout.spec.json';

const rects = legacy as LedRect[];
const handSpec = specJson as LayoutSpec;
const KEYS = ['hmin', 'hmax', 'vmin', 'vmax'] as const;

function roundTripError(input: LedRect[], segments: Segment[]): number {
  const out = compileStrip({ id: 's', name: 's', controllerId: 'c', output: 0, segments });
  expect(out).toHaveLength(input.length);
  let err = 0;
  out.forEach((r, i) => {
    expect(Boolean(r.disabled)).toBe(Boolean(input[i].disabled));
    if (!r.disabled) for (const k of KEYS) err = Math.max(err, Math.abs(r[k] - input[i][k]));
  });
  return err;
}

/** Same segment kinds/edges/counts as the reference, and parameters within `tol`. */
function expectClose(actual: Segment[], expected: Segment[], tol = 2e-4) {
  expect(actual.map((s) => [s.kind, s.count, s.kind === 'edge' ? s.edge : ''])).toEqual(
    expected.map((s) => [s.kind, s.count, s.kind === 'edge' ? s.edge : '']),
  );
  actual.forEach((a, i) => {
    const e = expected[i];
    if (a.kind === 'edge' && e.kind === 'edge') {
      for (const k of ['from', 'to', 'depth', 'blend'] as const) expect(Math.abs(a[k]! - e[k]!)).toBeLessThan(tol);
    }
    if (a.kind === 'fixed' && e.kind === 'fixed') expect(a.area).toEqual(e.area);
  });
}

describe('inferSegments', () => {
  const monitor = rects.slice(0, 91);
  const ceiling = rects.slice(91);

  it('recovers the compact monitor layout (9 segments) and round-trips', () => {
    const segs = inferSegments(monitor);
    expectClose(segs, handSpec.strips[0].segments);
    expect(roundTripError(monitor, segs)).toBeLessThan(0.001);
  });

  it('recovers the ceiling bar as one segment and round-trips', () => {
    const segs = inferSegments(ceiling);
    expect(segs).toEqual([{ kind: 'edge', count: 77, edge: 'top', from: 1, to: 0, depth: 0.25, blend: 5 }]);
    expect(roundTripError(ceiling, segs)).toBeLessThan(0.001);
  });

  it('groups disabled LEDs into off segments', () => {
    const off: LedRect = { hmin: 0, hmax: 0, vmin: 0, vmax: 0, group: 0, disabled: true };
    const input = [off, off, ...rects.slice(0, 18), off];
    const segs = inferSegments(input);
    expect(segs.map((s) => [s.kind, s.count])).toEqual([
      ['off', 2],
      ['edge', 18],
      ['off', 1],
    ]);
    expect(roundTripError(input, segs)).toBeLessThan(0.001);
  });

  it('falls back to one fixed segment per irregular LED', () => {
    const input: LedRect[] = [
      { hmin: 0.3, hmax: 0.35, vmin: 0.4, vmax: 0.7, group: 0 },
      { hmin: 0.1, hmax: 0.9, vmin: 0.45, vmax: 0.5, group: 0 },
      { hmin: 0.2, hmax: 0.21, vmin: 0.2, vmax: 0.9, group: 0 },
    ];
    const segs = inferSegments(input);
    expect(segs.map((s) => s.kind)).toEqual(['fixed', 'fixed', 'fixed']);
    expect(roundTripError(input, segs)).toBeLessThan(0.001);
  });

  it('round-trips scrambled and partially clamped input', () => {
    // Deterministic pseudo-random rects, some hugging edges, some not.
    let seed = 7;
    const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    const input: LedRect[] = Array.from({ length: 60 }, () => {
      const a = Math.round(rnd() * 1e4) / 1e4;
      const b = Math.round(rnd() * 1e4) / 1e4;
      return { hmin: rnd() < 0.3 ? 0 : Math.min(a, b), hmax: Math.max(a, b), vmin: 0, vmax: 0.1, group: 0 };
    });
    expect(roundTripError(input, inferSegments(input))).toBeLessThan(0.001);
  });

  it('handles empty input', () => {
    expect(inferSegments([])).toEqual([]);
  });
});

describe('specFromLegacy', () => {
  it('builds one strip per output that compiles back to the legacy layout', () => {
    const controller = controllerFromWledConfig('wled-monitor.local', cfg);
    const spec = specFromLegacy(rects, controller);
    expect(spec.strips.map((s) => [s.id, s.name, s.output, s.segments.length])).toEqual([
      ['strip-1', 'Strip 1', 0, 9],
      ['strip-2', 'Strip 2', 1, 1],
    ]);
    expect(validateLayout(spec)).toEqual([]);
    const [compiled] = compileLayout(spec);
    expect(compiled.leds).toHaveLength(rects.length);
    compiled.leds.forEach((led, i) => {
      for (const k of KEYS) expect(Math.abs(led[k] - rects[i][k])).toBeLessThan(0.001);
    });
  });

  it('synthesizes an output for a controller without outputs', () => {
    const spec = specFromLegacy(rects.slice(0, 18), { id: 'c', name: 'c', kind: 'wled', host: 'c', outputs: [] });
    expect(spec.controllers[0].outputs).toEqual([{ index: 0, start: 0, length: 18 }]);
    expect(spec.strips).toHaveLength(1);
  });
});
