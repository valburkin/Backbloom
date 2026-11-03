import { describe, expect, it } from 'vitest';
import type { ControllerInfo } from '../../api/types';
import type { Controller } from '../../layout';
import { deviceProblems, deviceStatus } from '../../control/health';
import { hasRealDevice, shownDevices } from '../NoLights';

const testDevice: Controller = { id: 'controller-1', name: 'LED controller', kind: 'file', host: '', outputs: [{ index: 0, start: 0, length: 1 }], device: { type: 'file', output: '/dev/null' } };
const wled: Controller = { id: 'desk', name: 'Desk', kind: 'wled', host: 'wled-desk.local', outputs: [{ index: 0, start: 0, length: 91 }], device: { type: 'wled', host: 'wled-desk.local' } };

describe('a new install’s test device', () => {
  it('isn’t a light: Home and Devices show No lights yet until a real device is there', () => {
    expect(hasRealDevice([])).toBe(false);
    expect(hasRealDevice([testDevice])).toBe(false);
    expect(hasRealDevice([testDevice, wled])).toBe(true);
  });

  it('is listed only with expert tools on', () => {
    expect(shownDevices([testDevice, wled], false)).toEqual([wled]);
    expect(shownDevices([testDevice, wled], true)).toEqual([testDevice, wled]);
  });

  it('never shows as sending colors in green', () => {
    const info: ControllerInfo = { id: 'controller-1', name: 'LED controller', type: 'file', enabled: true, running: true, state: 'ready', start: 0, ledCount: 1, strips: [] };
    const s = deviceStatus(info);
    expect(s.tone).toBe('neutral');
    expect(s.live).toBe(false);
    expect(s.text).toMatch(/Test device/);
    expect(deviceProblems([info])).toEqual([]);
    // a real device that is ready still is
    expect(deviceStatus({ ...info, type: 'wled' })).toMatchObject({ tone: 'ok', live: true });
  });
});
