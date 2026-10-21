import { describe, expect, it } from 'vitest';
import { compileLayout } from './compile';
import { compileLegacySegment, inferSegments, specFromLegacy, type LegacySegment } from './infer';
import { validateLayout } from './validate';
import { controllerFromWledConfig } from './wled';
import type { LedRect } from './types';
import legacy from './__fixtures__/my-layout.json';
import cfg from './__fixtures__/wled-two-outputs.json';
import segmentsJson from './__fixtures__/my-layout.segments.json';

const rects = legacy as LedRect[];
/** The owner's layout as HyperHDR-style segments, made by hand. */
const handSegments = segmentsJson as { monitor: LegacySegment[]; ceiling: LegacySegment[] };
const KEYS = ['hmin', 'hmax', 'vmin', 'vmax'] as const;

function roundTripError(input: LedRect[], segments: LegacySegment[]): number {
  const out = segments.flatMap(compileLegacySegment);
  expect(out).toHaveLength(input.length);
  let err = 0;
  out.forEach((r, i) => {
    expect(Boolean(r.disabled)).toBe(Boolean(input[i].disabled));
    if (!r.disabled) for (const k of KEYS) err = Math.max(err, Math.abs(r[k] - input[i][k]));
  });
  return err;
}

/** Same segment kinds/edges/counts as the reference, and parameters within `tol`. */
function expectClose(actual: LegacySegment[], expected: LegacySegment[], tol = 2e-4) {
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

describe('hand-made segments (ground truth)', () => {
  it("compile to the owner's rects within 0.0006", () => {
    expect(roundTripError(rects.slice(0, 91), handSegments.monitor)).toBeLessThan(0.0006);
    expect(roundTripError(rects.slice(91), handSegments.ceiling)).toBeLessThan(0.0006);
  });
});

describe('inferSegments', () => {
  const monitor = rects.slice(0, 91);
  const ceiling = rects.slice(91);

  it('recovers the compact monitor layout (9 segments) and round-trips', () => {
    const segs = inferSegments(monitor);
    expectClose(segs, handSegments.monitor);
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
  const controller = controllerFromWledConfig('wled-monitor.local', cfg);
  const spec = specFromLegacy(rects, controller);

  it('builds one valid strip per output, around the screen of a default room', () => {
    expect(spec.strips.map((s) => [s.id, s.name, s.output, s.ledCount])).toEqual([
      ['strip-1', 'Strip 1', 0, 91],
      ['strip-2', 'Strip 2', 1, 77],
    ]);
    expect(validateLayout(spec)).toEqual([]);
    expect(spec.strips.every((s) => s.path.every((p) => p.surface === 'front'))).toBe(true);
    expect(spec.strips[1]).toMatchObject({ depth: 0.25, blend: 5 });
  });

  it('pins nothing, so LEDs spread evenly, and keeps every LED on the edge its rect was on', () => {
    expect(spec.strips.every((s) => s.path.every((p) => p.at === undefined))).toBe(true);
    const [compiled] = compileLayout(spec);
    expect(compiled.leds).toHaveLength(rects.length);
    const bands = (r: LedRect) => [r.vmin === 0, r.vmax === 1 && r.vmin > 0, r.hmin === 0 && r.hmax < 0.5, r.hmax === 1 && r.hmin > 0.5];
    const ceiling = compiled.leds.slice(91);
    expect(ceiling.every((r) => r.vmin === 0)).toBe(true);
    // The monitor strip's LEDs keep their order around the screen (a top LED stays on top, a side LED on that side).
    const monitor = compiled.leds.slice(0, 91);
    const same = monitor.filter((r, i) => bands(r).some((on, k) => on && bands(rects[i])[k])).length;
    expect(same).toBeGreaterThan(80);
  });

  it('synthesizes an output for a controller without outputs', () => {
    const one = specFromLegacy(rects.slice(0, 18), { id: 'c', name: 'c', kind: 'wled', host: 'c', outputs: [] });
    expect(one.controllers[0].outputs).toEqual([{ index: 0, start: 0, length: 18 }]);
    expect(one.strips).toHaveLength(1);
  });
});
