import { describe, expect, it } from 'vitest';
import { compileStrip, stripLedCount } from './compile';
import { splitByAspect, templateById, templateDefaults, TEMPLATES, type TemplateValue } from './templates';
import { validateLayout } from './validate';
import type { EdgeSegment, LayoutSpec, Segment, Strip } from './types';

function stripOf(segments: Segment[]): Strip {
  return { id: 's', name: 'S', controllerId: 'c', output: 0, segments };
}

/** Wrap segments in a one-output controller sized to fit, and validate. */
function issuesFor(segments: Segment[]) {
  const s = stripOf(segments);
  const spec: LayoutSpec = {
    version: 1,
    controllers: [{ id: 'c', name: 'C', kind: 'wled', host: 'c', outputs: [{ index: 0, start: 0, length: stripLedCount(s) }] }],
    strips: [s],
  };
  return validateLayout(spec);
}

const ring = templateById('around-screen')!;
const buildRing = (v: Record<string, TemplateValue>) => ring.build({ ...templateDefaults(ring), ...v });

/** Count LEDs per edge (plus off/fixed). */
function perEdge(segments: Segment[]) {
  const n: Record<string, number> = {};
  for (const s of segments) {
    const key = s.kind === 'edge' ? s.edge : s.kind;
    n[key] = (n[key] ?? 0) + s.count;
  }
  return n;
}

describe('TEMPLATES', () => {
  it('has the expected presets with unique ids and valid defaults', () => {
    expect(TEMPLATES.map((t) => t.id)).toEqual([
      'around-screen',
      'top-and-sides',
      'bottom-only',
      'light-bar-above',
      'two-light-bars',
    ]);
    for (const t of TEMPLATES) {
      for (const p of t.params) {
        if (p.type === 'select') expect(p.options?.map((o) => o.value)).toContain(p.default);
      }
      const segs = t.build(templateDefaults(t));
      expect(segs.length).toBeGreaterThan(0);
      expect(issuesFor(segs)).toEqual([]);
    }
  });

  it('every template compiles cleanly for every select option', () => {
    for (const t of TEMPLATES) {
      for (const p of t.params.filter((x) => x.type === 'select')) {
        for (const o of p.options!) {
          const segs = t.build({ ...templateDefaults(t), [p.key]: o.value, gap: 4, gapMode: 'fixed' });
          expect(issuesFor(segs), `${t.id} ${p.key}=${o.value}`).toEqual([]);
        }
      }
    }
  });

  it('falls back to defaults for missing or bad values', () => {
    expect(ring.build({})).toEqual(ring.build(templateDefaults(ring)));
    expect(ring.build({ top: 'abc', start: 'nowhere' })).toEqual(ring.build(templateDefaults(ring)));
  });
});

describe('Around the screen', () => {
  const counts = { top: 30, right: 17, bottom: 28, left: 15 };

  it('produces counts matching params for every start and direction', () => {
    for (const start of ring.params.find((p) => p.key === 'start')!.options!.map((o) => o.value)) {
      for (const direction of ['clockwise', 'counterclockwise']) {
        const segs = buildRing({ ...counts, start, direction });
        expect(perEdge(segs), `${start} ${direction}`).toEqual(counts);
        expect(compileStrip(stripOf(segs))).toHaveLength(90);
      }
    }
  });

  it('walks clockwise from the bottom-left corner: up the left, across the top, down the right', () => {
    const segs = buildRing({ ...counts, start: 'bottom-left', direction: 'clockwise' }) as EdgeSegment[];
    expect(segs.map((s) => [s.edge, s.from, s.to])).toEqual([
      ['left', 1, 0],
      ['top', 0, 1],
      ['right', 0, 1],
      ['bottom', 1, 0],
    ]);
  });

  it('walks counterclockwise from the bottom-left corner along the bottom first', () => {
    const segs = buildRing({ ...counts, start: 'bottom-left', direction: 'counterclockwise' }) as EdgeSegment[];
    expect(segs.map((s) => [s.edge, s.from, s.to])).toEqual([
      ['bottom', 0, 1],
      ['right', 1, 0],
      ['top', 1, 0],
      ['left', 0, 1],
    ]);
  });

  it('starts at bottom center and splits the bottom in two', () => {
    const segs = buildRing({ ...counts, start: 'bottom-center', direction: 'clockwise' }) as EdgeSegment[];
    expect(segs.map((s) => [s.edge, s.count, s.from, s.to])).toEqual([
      ['bottom', 14, 0.5, 0],
      ['left', 15, 1, 0],
      ['top', 30, 0, 1],
      ['right', 17, 0, 1],
      ['bottom', 14, 1, 0.5],
    ]);
    // The ring is continuous: each LED center moves by one LED spacing along the bottom.
    const rects = compileStrip(stripOf(segs));
    expect(rects[0].vmin).toBeCloseTo(0.9);
  });

  it('puts the stand gap in the middle of the bottom as off or fixed LEDs', () => {
    const segs = buildRing({ ...counts, start: 'bottom-left', direction: 'clockwise', gap: 4 });
    expect(segs.map((s) => [s.kind, s.count])).toEqual([
      ['edge', 15],
      ['edge', 30],
      ['edge', 17],
      ['edge', 12],
      ['off', 4],
      ['edge', 12],
    ]);
    expect(perEdge(segs)).toEqual({ ...counts, bottom: 24, off: 4 });

    const fixed = buildRing({ ...counts, start: 'bottom-left', gap: 4, gapMode: 'fixed' });
    const gap = fixed.find((s) => s.kind === 'fixed');
    expect(gap).toEqual({ kind: 'fixed', count: 4, area: { hmin: 0.428571, hmax: 0.571429, vmin: 0.9, vmax: 1 } });
  });

  it('skips empty sides', () => {
    const segs = buildRing({ top: 10, right: 0, bottom: 0, left: 5, start: 'top-left' });
    expect(perEdge(segs)).toEqual({ top: 10, left: 5 });
  });
});

describe('other templates', () => {
  const build = (id: string, v: Record<string, TemplateValue>) => {
    const t = templateById(id)!;
    return t.build({ ...templateDefaults(t), ...v });
  };

  it('top and sides', () => {
    const segs = build('top-and-sides', { left: 12, top: 25, right: 13, start: 'bottom-right' }) as EdgeSegment[];
    expect(segs.map((s) => [s.edge, s.count, s.from, s.to])).toEqual([
      ['right', 13, 1, 0],
      ['top', 25, 1, 0],
      ['left', 12, 0, 1],
    ]);
  });

  it('bottom only', () => {
    expect(build('bottom-only', { count: 33, direction: 'right-to-left' })).toEqual([
      { kind: 'edge', count: 33, edge: 'bottom', from: 1, to: 0, depth: 0.15, blend: 1.5 },
    ]);
  });

  it('light bar above matches the ceiling bar of the reference layout', () => {
    expect(build('light-bar-above', { count: 77, direction: 'right-to-left' })).toEqual([
      { kind: 'edge', count: 77, edge: 'top', from: 1, to: 0, depth: 0.25, blend: 5 },
    ]);
  });

  it('two light bars', () => {
    const segs = build('two-light-bars', { count: 10, order: 'right-first', rightDirection: 'top-to-bottom' }) as EdgeSegment[];
    expect(segs.map((s) => [s.edge, s.count, s.from, s.to])).toEqual([
      ['right', 10, 0, 1],
      ['left', 10, 1, 0],
    ]);
  });
});

describe('splitByAspect', () => {
  it('splits a 16:9 ring', () => {
    expect(splitByAspect(120, 16, 9)).toEqual({ top: 38, right: 22, bottom: 38, left: 22 });
  });

  it('always adds up to the total', () => {
    for (const [w, h] of [[16, 9], [21, 9], [32, 9], [4, 3]]) {
      for (let total = 0; total < 300; total += 7) {
        const r = splitByAspect(total, w, h);
        expect(r.top + r.right + r.bottom + r.left).toBe(total);
        const t = splitByAspect(total, w, h, ['left', 'top', 'right']);
        expect(t.bottom).toBe(0);
        expect(t.top + t.right + t.left).toBe(total);
      }
    }
  });

  it('handles ultrawide screens and a single side', () => {
    expect(splitByAspect(100, 32, 9)).toEqual({ top: 39, right: 11, bottom: 39, left: 11 });
    expect(splitByAspect(50, 21, 9, ['bottom'])).toEqual({ top: 0, right: 0, bottom: 50, left: 0 });
  });

  it('rejects bad aspect ratios', () => {
    expect(() => splitByAspect(10, 0, 9)).toThrow(RangeError);
  });
});
