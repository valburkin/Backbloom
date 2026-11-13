import { describe, expect, it } from 'vitest';
import { defaultRoom } from './room';
import { validateLayout } from './validate';
import type { Controller, LayoutSpec, Room, Strip } from './types';

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

const strip = (over: Partial<Strip> = {}): Strip => ({
  id: 'monitor',
  name: 'Monitor',
  controllerId: 'c',
  output: 0,
  ledCount: 10,
  path: [
    { surface: 'front', x: 2.3, y: 1.31 },
    { surface: 'front', x: 1.7, y: 1.31 },
  ],
  ...over,
});

const spec = (strips: Strip[], controllers: Controller[] = [controller()], room: Room = defaultRoom()): LayoutSpec => ({
  version: 1,
  room,
  controllers,
  strips,
});

const messages = (s: LayoutSpec) => validateLayout(s).map((i) => `${i.severity}: ${i.message}`);

describe('validateLayout', () => {
  it('accepts a preset without programs, with or without its own Look, and checks the Look', () => {
    const preset = { id: 'p', name: 'Gaming', apps: [], sections: {} };
    const look = { time_ms: 150, stiffness: 817, damping: 61, saturationGain: 1.5, luminanceGain: 1.2, backlightThreshold: 0 };
    expect(messages({ ...spec([strip()]), profiles: [preset, { ...preset, id: 'q', look }] })).toEqual([]);
    expect(messages({ ...spec([strip()]), profiles: [{ ...preset, look: { ...look, time_ms: 0 } }] })).toEqual([
      'error: Preset “Gaming” has a Look with time_ms 0; it must be a whole number between 25 and 5000.',
    ]);
  });

  it('accepts a correct layout', () => {
    expect(validateLayout(spec([strip(), strip({ id: 'b', output: 1, ledCount: 5 })]))).toEqual([]);
  });

  it('reports missing LEDs as a warning with the count', () => {
    const issues = validateLayout(spec([strip({ ledCount: 7 })]));
    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ severity: 'warning', stripId: 'monitor' });
    expect(issues[0].message).toContain('last 3 LEDs');
  });

  it('reports extra LEDs as an error with the count', () => {
    const [issue] = validateLayout(spec([strip({ ledCount: 11 })]));
    expect(issue.severity).toBe('error');
    expect(issue.message).toContain('1 LED too many');
  });

  it('reports unknown controllers and outputs', () => {
    expect(messages(spec([strip({ controllerId: 'x' })]))[0]).toMatch(/^error: .*device that doesn’t exist/);
    expect(messages(spec([strip({ output: 4 })]))[0]).toMatch(/^error: .*output 5 .*output 1 \(pin 16\), output 2/);
  });

  it('reports two strips on one output', () => {
    const m = messages(spec([strip(), strip({ id: 'b', name: 'Other' })]));
    expect(m).toEqual([expect.stringMatching(/^error: Strip “Monitor” and strip “Other” are both plugged into output 1/)]);
  });

  it('reports overlapping outputs and gaps between them', () => {
    const overlap = controller({ outputs: [{ index: 0, start: 0, length: 10 }, { index: 1, start: 8, length: 5 }] });
    expect(messages(spec([], [overlap]))).toEqual([expect.stringMatching(/^error: .*overlap \(LEDs 9–10/)]);
    const gap = controller({ outputs: [{ index: 0, start: 0, length: 10 }, { index: 1, start: 12, length: 5 }] });
    expect(messages(spec([], [gap]))).toEqual([expect.stringMatching(/^error: Output 2 of “Desk WLED” starts at LED 13, leaving LEDs 11–12 without an output/)]);
  });

  it('reports duplicate ids', () => {
    const m = messages(spec([strip(), strip({ output: 1, ledCount: 5 })], [controller(), controller()]));
    expect(m.some((x) => x.includes('Two devices share the id “c”'))).toBe(true);
    expect(m.some((x) => x.includes('Two strips share the id “monitor”'))).toBe(true);
  });

  it('reports bad strip settings', () => {
    const bad = (over: Partial<Strip>) => messages(spec([strip(over)])).filter((m) => m.startsWith('error'));
    expect(bad({ ledCount: 0 })[0]).toMatch(/at least 1/);
    expect(bad({ path: [strip().path[0]] })[0]).toMatch(/needs a path/);
    expect(bad({ path: [strip().path[0], strip().path[0]] })[0]).toMatch(/same spot/);
    expect(bad({ depth: 0 })[0]).toMatch(/looks 0 into the picture/);
    expect(bad({ blend: 0 })[0]).toMatch(/softness of 0/);
    expect(bad({ brightness: 1.5 })[0]).toMatch(/brightness of 1.5/);
    expect(bad({ dark: [{ start: 8, count: 5 }] })[0]).toMatch(/turns off LEDs 9–13/);
  });

  it('reports bad parts with the bend index, and strips that are all gaps', () => {
    const [a, b] = strip().path;
    const withSection = (next: unknown) => strip({ path: [{ ...a, next: next as never }, b] });
    const first = (next: unknown) => validateLayout(spec([withSection(next)])).find((i) => i.severity === 'error');
    expect(first({ kind: 'area' })).toMatchObject({ pointIndex: 0, message: expect.stringMatching(/Part 1 .* needs an area/) });
    expect(first({ kind: 'area', area: { hmin: 0.6, hmax: 0.4, vmin: 0, vmax: 1 } })?.message).toMatch(/inside out/);
    expect(first({ kind: 'edge', from: 0 })?.message).toMatch(/start and an end on the screen’s outline/);
    expect(first({ kind: 'sparkle' })?.message).toMatch(/isn’t known/);
    expect(first({ kind: 'effect', effect: ' ' })?.message).toMatch(/name of the effect/);
    expect(first({ kind: 'color', color: [255, 0] })?.message).toMatch(/needs a color/);
    expect(first({ kind: 'color', color: [255, 0, 256] })?.message).toMatch(/needs a color/);
    expect(validateLayout(spec([withSection({ kind: 'effect', effect: 'Candle' })]))).toEqual([]);
    expect(validateLayout(spec([withSection({ kind: 'color', color: [10, 20, 30] })]))).toEqual([]);
    expect(first({ kind: 'gap' })?.message).toMatch(/no length with LEDs/);
    expect(validateLayout(spec([withSection({ kind: 'edge', from: 3.5, to: 4.5 })]))).toEqual([]);
  });

  it('reports bends outside the room or matched out of order, with the bend index', () => {
    const outside = validateLayout(spec([strip({ path: [strip().path[0], { surface: 'front', x: 9, y: 1 }] })]));
    expect(outside[0]).toMatchObject({ severity: 'error', pointIndex: 1 });
    expect(outside[0].message).toMatch(/Bend 2 .* outside the room/);

    const backwards = strip({
      path: [
        { surface: 'front', x: 1, y: 1, at: 6 },
        { surface: 'front', x: 2, y: 1, at: 3 },
        { surface: 'front', x: 3, y: 1 },
      ],
    });
    expect(validateLayout(spec([backwards]))[0]).toMatchObject({ severity: 'error', pointIndex: 1 });
    expect(messages(spec([strip({ path: [{ surface: 'front', x: 1, y: 1, at: 11 }, strip().path[1]] })]))[0]).toMatch(/matched to LED 11, past the end of the strip/);
  });

  it('reports a bad room', () => {
    const room = defaultRoom();
    const issues = validateLayout(spec([strip()], [controller()], { ...room, height: 0.2 }));
    expect(issues[0]).toMatchObject({ severity: 'error', room: true });
    expect(messages(spec([], [controller()], { ...room, screen: { ...room.screen, x: 3.8 } }))[0]).toMatch(/screen reaches past the front wall/);
  });

  it('warns about devices without outputs', () => {
    expect(messages(spec([], [controller({ outputs: [] })]))).toEqual([expect.stringMatching(/^warning: .*no LED outputs/)]);
  });

  it('reports a layout without a room, and a screen without a size', () => {
    expect(messages({ ...spec([]), room: undefined as unknown as Room })).toContain('error: The layout has no room.');
    expect(messages(spec([], [controller()], { ...defaultRoom(), screen: { x: 1, y: 1, width: 0, height: 0.3 } }))).toEqual([
      'error: The screen needs a position and a size above 0.',
    ]);
  });

  it('points at a bend that isn’t on a surface of the room', () => {
    const issues = validateLayout(spec([strip({ path: [{ surface: 'front', x: 2.3, y: 1.31 }, { surface: 'roof' as 'front', x: 1, y: 1 }] })]));
    expect(issues).toContainEqual(expect.objectContaining({ severity: 'error', stripId: 'monitor', pointIndex: 1, message: 'Bend 2 of strip “Monitor” isn’t on a wall, the ceiling or the floor.' }));
  });

  it('reports outputs that drive no LEDs or start before LED 1', () => {
    const c = controller({ outputs: [{ index: 0, start: 0, length: 0 }, { index: 1, start: -2, length: 5 }] });
    const found = messages(spec([], [c]));
    expect(found).toContain('error: Output 1 of “Desk WLED” must drive at least 1 LED (it has 0).');
    expect(found).toContain('error: Output 2 of “Desk WLED” has an invalid start (-2).');
  });

  it('names a device without outputs when a strip is plugged into it', () => {
    expect(messages(spec([strip()], [controller({ outputs: [] })]))).toEqual([
      'warning: Device “Desk WLED” has no LED outputs.',
      'error: Strip “Monitor” is plugged into output 1 of “Desk WLED”, but it has no outputs.',
    ]);
  });

  it('reports presets that aren’t in the shape the backend reads', () => {
    const base = spec([strip()]);
    const preset = { id: 'p', name: 'Gaming', apps: [], sections: {} };
    expect(messages({ ...base, profiles: {} as never })).toEqual(['error: The presets must be a list.']);
    expect(messages({ ...base, profiles: [{ ...preset, id: ' ' }] })).toEqual(['error: A preset needs an id.']);
    expect(messages({ ...base, profiles: [{ ...preset, apps: ['game.exe', ''] }] })).toEqual([
      'error: Preset “Gaming” needs its programs as a list of program names.',
    ]);
    expect(messages({ ...base, profiles: [{ ...preset, look: 5 as never }] })).toEqual(['error: Preset “Gaming” needs its Look as transition and color values.']);
    expect(messages({ ...base, profiles: [{ ...preset, sections: [] as never }] })).toEqual(['error: Preset “Gaming” needs what each strip’s parts show.']);
    expect(messages({ ...base, profiles: [{ ...preset, sections: { monitor: {} as never } }] })).toEqual([
      'error: Preset “Gaming”: the parts of strip “monitor” must be a list.',
    ]);
  });
});
