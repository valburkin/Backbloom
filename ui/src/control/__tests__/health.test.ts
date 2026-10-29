import { describe, expect, it } from 'vitest';
import type { ControllerInfo } from '../../api/types';
import { deviceProblems, deviceStatus, isUsbDevice } from '../health';

const c = (extra: Partial<ControllerInfo>): ControllerInfo => ({
  id: 'wled',
  name: 'WLED',
  type: 'wled',
  enabled: true,
  running: true,
  state: 'ready',
  start: 0,
  ledCount: 10,
  strips: [],
  ...extra,
});

describe('deviceProblems', () => {
  it('stays quiet while devices send colors, connect, are off or switched off', () => {
    expect(deviceProblems(undefined)).toEqual([]);
    expect(deviceProblems([c({}), c({ state: 'opening' }), c({ state: 'off' }), c({ enabled: false, running: false, state: 'failed' })])).toEqual([]);
  });

  it('names devices that can’t be reached, gave up or can’t be used', () => {
    expect(deviceProblems([c({ state: 'waitingToRetry' })])).toEqual([{ id: 'wled', text: 'WLED can’t be reached — check that it’s on.', tone: 'warn' }]);
    expect(deviceProblems([c({ state: 'failed' })])[0]).toMatchObject({ tone: 'bad', text: expect.stringMatching(/^WLED stopped trying to connect/) });
    expect(deviceProblems([c({ running: false, state: 'off', status: 'No serial port set.' })])[0].text).toBe('WLED can’t be used: No serial port set.');
  });

  it('speaks of the cable for USB devices', () => {
    const ledDevices = { available: [{ name: 'adalight', group: 'serial' }] };
    expect(isUsbDevice('adalight', ledDevices)).toBe(true);
    expect(isUsbDevice('wled', ledDevices)).toBe(false);
    expect(deviceProblems([c({ type: 'adalight', state: 'failed' })], ledDevices)[0].text).toBe('WLED stopped trying to connect — check the cable and power.');
  });
});

describe('deviceStatus (the Devices page uses the same words)', () => {
  it('describes every state', () => {
    expect(deviceStatus(c({}))).toEqual({ text: 'Sending colors', tone: 'ok', problem: false, live: true });
    expect(deviceStatus(c({ state: 'waitingToRetry' })).text).toBe('Can’t be reached — check that it’s on');
    expect(deviceStatus(c({ enabled: false, running: false })).text).toBe('Switched off');
    expect(deviceStatus(c({ running: false })).text).toBe('Not sending');
    expect(deviceStatus(c({ state: 'opening' })).tone).toBe('neutral');
  });
});
