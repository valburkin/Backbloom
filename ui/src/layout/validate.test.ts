import { describe, expect, it } from 'vitest';
import { validateLayout } from './validate';
import type { Controller, LayoutSpec, Segment, Strip } from './types';

const controller = (over: Partial<Controller> = {}): Controller => ({
  id: 'c',
  name: 'Desk WLED',
  kind: 'wled',
  host: 'desk.local',
  outputs: [
    { index: 0, start: 0, length: 10, pin: 16 },
    { index: 1, start: 10, length: 5, pin: 2 },
  ],
  ...over,
});

const edge = (over: Partial<Extract<Segment, { kind: 'edge' }>> = {}): Segment => ({
  kind: 'edge',
  count: 10,
  edge: 'top',
  from: 0,
  to: 1,
  depth: 0.1,
  ...over,
});

const strip = (over: Partial<Strip> = {}): Strip => ({
  id: 'monitor',
  name: 'Monitor',
  controllerId: 'c',
  output: 0,
  segments: [edge()],
  ...over,
});

const spec = (strips: Strip[], controllers: Controller[] = [controller()]): LayoutSpec => ({
  version: 1,
  controllers,
  strips,
});

const messages = (s: LayoutSpec) => validateLayout(s).map((i) => `${i.severity}: ${i.message}`);

describe('validateLayout', () => {
  it('accepts a correct layout', () => {
    expect(validateLayout(spec([strip(), strip({ id: 'b', output: 1, segments: [edge({ count: 5 })] })]))).toEqual([]);
  });

  it('reports missing LEDs as a warning with the count', () => {
    const issues = validateLayout(spec([strip({ segments: [edge({ count: 7 })] })]));
    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ severity: 'warning', stripId: 'monitor' });
    expect(issues[0].message).toContain('3 LEDs missing');
  });

  it('reports extra LEDs as an error with the count', () => {
    const [issue] = validateLayout(spec([strip({ segments: [edge({ count: 11 })] })]));
    expect(issue.severity).toBe('error');
    expect(issue.message).toContain('1 LED too many');
  });

  it('reports unknown controllers and outputs', () => {
    expect(messages(spec([strip({ controllerId: 'x' })]))[0]).toMatch(/^error: .*controller that doesn't exist/);
    expect(messages(spec([strip({ output: 4 })]))[0]).toMatch(/^error: .*output 5 .*output 1 \(pin 16\), output 2/);
  });

  it('reports two strips on one output', () => {
    const m = messages(spec([strip(), strip({ id: 'b', name: 'Other' })]));
    expect(m).toEqual([expect.stringMatching(/^error: Strip "Monitor" and strip "Other" are both wired to output 1/)]);
  });

  it('reports overlapping outputs', () => {
    const c = controller({
      outputs: [
        { index: 0, start: 0, length: 10 },
        { index: 1, start: 8, length: 5 },
      ],
    });
    const m = messages(spec([], [c]));
    expect(m).toEqual([expect.stringMatching(/^error: .*overlap \(LEDs 8–9/)]);
  });

  it('reports outputs that leave a gap (the backend counts a controller as its outputs one after the other)', () => {
    const c = controller({
      outputs: [
        { index: 0, start: 0, length: 10 },
        { index: 1, start: 12, length: 5 },
      ],
    });
    expect(messages(spec([], [c]))).toEqual([expect.stringMatching(/^error: Output 2 of "Desk WLED" starts at LED 12, leaving LEDs 10–11 without an output/)]);
  });

  it('reports a strip brightness outside 0..1', () => {
    expect(messages(spec([strip({ segments: [edge()], brightness: 1.5 })]))).toEqual([expect.stringMatching(/^error: .*brightness of 1.5/)]);
    expect(validateLayout(spec([strip({ brightness: 0.4 }), strip({ id: 'b', output: 1, segments: [edge({ count: 5 })] })]))).toEqual([]);
  });

  it('reports duplicate ids', () => {
    const m = messages(spec([strip(), strip({ output: 1, segments: [edge({ count: 5 })] })], [controller(), controller()]));
    expect(m.some((x) => x.includes('Two controllers share the id "c"'))).toBe(true);
    expect(m.some((x) => x.includes('Two strips share the id "monitor"'))).toBe(true);
  });

  it('reports bad segment parameters with the segment index', () => {
    const bad: Segment[] = [
      edge({ count: 0 }),
      edge({ count: 2, depth: 0 }),
      edge({ count: 2, depth: 1.5 }),
      edge({ count: 2, from: 0.5, to: 0.5 }),
      edge({ count: 2, blend: 0 }),
      { kind: 'fixed', count: 2, area: { hmin: 0.6, hmax: 0.4, vmin: 0, vmax: 1 } },
      { kind: 'fixed', count: 2, area: { hmin: 0, hmax: 1, vmin: 0.9, vmax: 0.1 } },
    ];
    const issues = validateLayout(spec([strip({ segments: bad })])).filter((i) => i.segmentIndex !== undefined);
    expect(issues.map((i) => i.segmentIndex)).toEqual([0, 1, 2, 3, 4, 5, 6]);
    expect(issues.every((i) => i.severity === 'error')).toBe(true);
    expect(issues[0].message).toMatch(/^Segment 1 of strip "Monitor" needs .* at least 1/);
    expect(issues[3].message).toContain('same position');
    expect(issues[5].message).toContain("left edge");
  });

  it('allows from == to for a single LED', () => {
    const segments = [edge({ count: 1, from: 0.5, to: 0.5 }), { kind: 'off', count: 9 } as Segment];
    expect(validateLayout(spec([strip({ segments })]))).toEqual([]);
  });

  it('warns about empty strips and controllers without outputs', () => {
    expect(messages(spec([strip({ segments: [] })]))).toEqual([expect.stringMatching(/^warning: .*no segments yet.*10 LEDs/)]);
    expect(messages(spec([], [controller({ outputs: [] })]))).toEqual([expect.stringMatching(/^warning: .*no LED outputs/)]);
  });
});
