import { describe, expect, it } from 'vitest';
import { controllerFromWledConfig } from './wled';
import cfg from './__fixtures__/wled-two-outputs.json';

describe('controllerFromWledConfig', () => {
  it('parses the two-output fixture', () => {
    expect(controllerFromWledConfig('WLED-Monitor.local', cfg)).toEqual({
      id: 'wled-monitor-local',
      name: 'WLED-Monitor',
      kind: 'wled',
      host: 'WLED-Monitor.local',
      outputs: [
        { index: 0, start: 0, length: 91, pin: 16 },
        { index: 1, start: 91, length: 77, pin: 2 },
      ],
    });
  });

  it('uses an explicit name and IP hosts', () => {
    const c = controllerFromWledConfig('192.168.1.40', cfg, '  Desk  ');
    expect(c.id).toBe('192-168-1-40');
    expect(c.name).toBe('Desk');
  });

  it('tolerates missing fields and skips unusable outputs', () => {
    const c = controllerFromWledConfig('x', {
      hw: { led: { ins: [{ len: 10 }, null, { len: 0 }, { len: '5', pin: 4 }, { start: 30, len: 2, pin: [] }] } },
    });
    expect(c.outputs).toEqual([
      { index: 0, start: 0, length: 10 },
      { index: 3, start: 10, length: 5, pin: 4 },
      { index: 4, start: 30, length: 2 },
    ]);
  });

  it('throws clear errors on unusable input', () => {
    expect(() => controllerFromWledConfig('', cfg)).toThrow(/hostname/);
    expect(() => controllerFromWledConfig('x', null)).toThrow(/hw\.led\.ins/);
    expect(() => controllerFromWledConfig('x', { hw: {} })).toThrow(/Is this a WLED device/);
    expect(() => controllerFromWledConfig('x', { hw: { led: { ins: [] } } })).toThrow(/no LED outputs/);
  });
});
