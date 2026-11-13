import { describe, expect, it } from 'vitest';
import { defaultRoom, shapePath, type Controller, type LayoutSpec, type Strip } from '../../layout';
import { NO_AUTH, lightDevices, type PlacedLight } from '../lights';
import { litSegments, pathWithRole, placesOfName, roleRows, withRole } from '../roles';
import { stripPath } from '../useAddToLayout';
import { placedPath, type BoardStrip } from '../wled';

const room = defaultRoom();
const strip = (over: Partial<BoardStrip>): BoardStrip => ({
  places: ['screen'],
  preset: 'ws2812b',
  ledCount: 60,
  pin: 16,
  clockPin: null,
  colorOrder: null,
  plan: null,
  ...over,
});
const kinds = (path: ReadonlyArray<{ next?: { kind: string } }>) => path.slice(0, -1).map((p) => p.next?.kind ?? 'edge');

describe('the screen’s average by default', () => {
  it('starts every strip with the screen’s average, wherever it runs: its route isn’t known yet', () => {
    expect(new Set(kinds(placedPath(strip({ places: ['screen'] }), room, 60)))).toEqual(new Set(['whole']));
    expect(new Set(kinds(placedPath(strip({ places: ['ceiling'] }), room, 60)))).toEqual(new Set(['whole']));
    // the reference setup's first strip: along the desk, then behind the monitor; the gap between them stays
    const both = kinds(placedPath(strip({ places: ['desk', 'screen'] }), room, 91));
    expect(both.filter((k) => k === 'gap')).toHaveLength(1);
    expect(new Set(both.filter((k) => k !== 'gap'))).toEqual(new Set(['whole']));
  });

  it('gives room lights and devices added as they are the screen’s average too', () => {
    const lamp: PlacedLight = { key: 'wiz:1', brand: 'wiz', host: '192.168.1.45', name: 'Bulb', lamp: { ipAddress: '192.168.1.45', name: 'Bulb' }, label: 'Lamp', place: 'left' } as PlacedLight;
    const [device] = lightDevices([lamp], NO_AUTH, room, () => ({ type: 'wiz' }));
    expect(kinds(device.strips[0].path)).toEqual(['whole']);
    // the first strip of a device added as it is goes around the screen, the next across the ceiling: both soft
    expect(new Set(kinds(stripPath({ output: 0, name: 'A' }, room, 0)))).toEqual(new Set(['whole']));
    expect(new Set(kinds(stripPath({ output: 1, name: 'B' }, room, 1)))).toEqual(new Set(['whole']));
    expect(new Set(kinds(stripPath({ output: 0, name: 'A', role: 'screen' }, room, 0)))).toEqual(new Set(['edge']));
    // gaps stay gaps
    const gapped = withRole([{ surface: 'front', x: 0, y: 0, next: { kind: 'gap' } }, { surface: 'front', x: 1, y: 0 }, { surface: 'front', x: 2, y: 0 }], 'soft');
    expect(kinds(gapped)).toEqual(['gap', 'whole']);
  });
});

describe('what each light does', () => {
  const wled: Controller = { id: 'wled', name: 'Desk and monitor', kind: 'wled', host: '192.168.1.77', outputs: [{ index: 0, start: 0, length: 91 }, { index: 1, start: 91, length: 77 }] };
  const hue: Controller = { id: 'hue', name: 'Philips Hue', kind: 'philipshue', host: '', outputs: [{ index: 0, start: 0, length: 1 }] };
  const desk: Strip = { id: 'strip-1', name: 'Desk and monitor', controllerId: 'wled', output: 0, ledCount: 91, path: placedPath(strip({ places: ['desk', 'screen'] }), room, 91) };
  const ceiling: Strip = { id: 'strip-2', name: 'Ceiling', controllerId: 'wled', output: 1, ledCount: 77, path: withRole(shapePath(room, 'ceiling-front'), 'soft') };
  const lamp: Strip = { id: 'strip-3', name: 'TV left', controllerId: 'hue', output: 0, ledCount: 1, path: withRole(shapePath(room, 'line'), 'soft') };
  const spec: LayoutSpec = { version: 1, room, controllers: [wled, hue], strips: [desk, ceiling, lamp] };

  it('has one row per strip or light, a strip along several places described by them', () => {
    expect(roleRows(spec).map((r) => [r.label, r.description, r.role])).toEqual([
      ['Desk and monitor', 'Along the desk and behind the monitor', 'whole'],
      ['Ceiling', 'On the ceiling', 'whole'],
      ['TV left', 'Philips Hue', 'whole'],
    ]);
    expect(placesOfName('Monitor 2')).toBe('Behind the monitor');
    expect(placesOfName('Hue Go')).toBeNull();
    // strips on a WLED board can be traced; a room light can't
    expect(roleRows(spec).map((r) => r.trace)).toEqual([{ host: '192.168.1.77', output: 0, leds: 91 }, { host: '192.168.1.77', output: 1, leds: 77 }, null]);
  });

  it('sets the whole strip at once, its gaps kept, with the layout’s roles', () => {
    const [row] = roleRows(spec);
    expect(row.segments).toEqual(litSegments(desk));
    const own = pathWithRole(desk, row.segments, 'own', ['Candle', 'Music: pulse']);
    expect(new Set(kinds(own))).toEqual(new Set(['effect', 'gap']));
    expect(roleRows({ ...spec, strips: [{ ...desk, path: own }] })[0].role).toBe('own');
    // an effect or color the part has stays
    const colored = { ...ceiling, path: ceiling.path.map((p, i) => (i < ceiling.path.length - 1 ? { ...p, next: { kind: 'color' as const, color: [1, 2, 3] as [number, number, number] } } : p)) };
    expect(pathWithRole(colored, litSegments(colored), 'own', ['Candle'])[0].next).toEqual({ kind: 'color', color: [1, 2, 3] });
  });
});
