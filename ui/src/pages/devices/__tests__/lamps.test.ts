import { describe, expect, it } from 'vitest';
import type { Controller, Strip } from '../../../layout';
import { isPerLampDevice, lampListChange } from '../lamps';

const lamp = (n: number) => ({ ipAddress: `192.168.1.${40 + n}`, macAddress: `m${n}`, name: `Lamp ${n}` });
const device = (lamps: unknown[], outputs = lamps.map((_, i) => ({ index: i, start: i, length: 1 }))): Controller => ({
  id: 'lifx',
  name: 'LIFX',
  kind: 'lifx',
  host: '',
  outputs,
  device: { type: 'lifx', lamps },
});
const strip = (output: number): Strip => ({ id: `s${output}`, name: `Lamp ${output}`, controllerId: 'lifx', output, ledCount: 1, path: [] });

describe('per-lamp devices', () => {
  it('tells one output per lamp from one output for all lamps', () => {
    expect(isPerLampDevice(device([lamp(0), lamp(1)]))).toBe(true);
    expect(isPerLampDevice(device([lamp(0)]))).toBe(true);
    expect(isPerLampDevice(device([lamp(0), lamp(1)], [{ index: 0, start: 0, length: 2 }]))).toBe(false);
    expect(isPerLampDevice({ ...device([]), device: { type: 'wled' } })).toBe(false);
  });

  it('takes a removed lamp’s output and strip along, the lamps after it keep their strips', () => {
    const before = [lamp(0), lamp(1), lamp(2)];
    const c = device(before);
    const change = lampListChange(c, [strip(0), strip(1), strip(2)], before, [lamp(0), lamp(2)]);
    expect(change.outputs).toEqual([
      { index: 0, start: 0, length: 1 },
      { index: 1, start: 1, length: 1 },
    ]);
    expect(change.removeStrips).toEqual(['s1']);
    // lamp 2's strip moves to the output lamp 2 has now
    expect(change.moveStrips).toEqual([{ stripId: 's2', output: 1 }]);
    expect(change.addStrips).toEqual([]);
  });

  it('removes the last lamp without leaving a strip on a missing output', () => {
    const before = [lamp(0), lamp(1)];
    const change = lampListChange(device(before), [strip(0), strip(1)], before, [lamp(0)]);
    expect(change.removeStrips).toEqual(['s1']);
    expect(change.outputs).toHaveLength(1);
  });

  it('gets outputs back after every lamp was removed', () => {
    const before = [lamp(0)];
    const empty = lampListChange(device(before), [strip(0)], before, []);
    expect(empty.outputs).toEqual([]);
    expect(empty.removeStrips).toEqual(['s0']);
    const none = device([], empty.outputs);
    expect(isPerLampDevice(none)).toBe(true);
    const back = lampListChange(none, [], [], [lamp(3)]);
    expect(back.outputs).toEqual([{ index: 0, start: 0, length: 1 }]);
    expect(back.addStrips).toEqual([{ output: 0, name: 'Lamp 3' }]);
  });

  it('gives a new lamp its own output and strip, also on a device with one lamp', () => {
    const change = lampListChange(device([lamp(0)]), [strip(0)], [lamp(0)], [lamp(0), lamp(5)]);
    expect(change.outputs).toHaveLength(2);
    expect(change.addStrips).toEqual([{ output: 1, name: 'Lamp 5' }]);
    expect(change.removeStrips).toEqual([]);
    expect(change.moveStrips).toEqual([]);
  });
});
