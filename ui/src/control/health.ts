import type { ControllerInfo, ServerInfo } from '../api/types';

/** A device's state in words: the same wording on Home (its problems) and on Devices (every device). */
export interface DeviceStatus {
  /** Starts with a capital letter; Home puts the device's name before it in lower case. */
  text: string;
  tone: 'ok' | 'warn' | 'bad' | 'neutral';
  /** Switched on but gets no colors: Home warns about it. */
  problem: boolean;
  /** Colors are being sent right now. */
  live: boolean;
}

/** A device that should be sending colors but isn't, in words for the Home page. */
export interface DeviceProblem {
  id: string;
  text: string;
  tone: 'warn' | 'bad';
}

/** Whether a device type is connected by USB (serial or HID), from serverinfo's list of device types. */
export function isUsbDevice(type: string, ledDevices: ServerInfo['ledDevices']): boolean {
  return (ledDevices?.available ?? []).some((d) => d.name === type && /serial|HID/i.test(d.group));
}

/**
 * What a device does now (`serverinfo.controllers`): sending, connecting, unreachable and retrying, given up, or
 * unusable settings. Devices that are switched off or idle because the lights are off aren't problems.
 */
export function deviceStatus(c: ControllerInfo, usb = false): DeviceStatus {
  const s = (text: string, tone: DeviceStatus['tone'], problem = false, live = false): DeviceStatus => ({ text, tone, problem, live });
  if (!c.enabled) return s('Switched off', 'neutral');
  // the test device of a new install writes the colors nowhere: never "Sending colors" in green
  if (c.type === 'file') return s('Test device: the colors go nowhere', 'neutral');
  if (!c.running) return c.status ? s(`Can’t be used: ${c.status.replace(/\.$/, '')}`, 'bad', true) : s('Not sending', 'neutral');
  switch (c.state) {
    case 'ready':
      return s('Sending colors', 'ok', false, true);
    case 'opening':
      return s('Connecting…', 'neutral');
    case 'waitingToRetry':
      return s(usb ? 'Can’t be reached — check that it’s plugged in' : 'Can’t be reached — check that it’s on', 'warn', true);
    case 'failed':
      return s(usb ? 'Stopped trying to connect — check the cable and power' : 'Stopped trying to connect — check that it’s on and its address is right', 'bad', true);
    case 'closing':
      return s('Turning off', 'neutral');
    default:
      return s('Not sending', 'neutral');
  }
}

/** The devices that are switched on but don't get colors (the same data and words as the Devices page). */
export function deviceProblems(controllers: readonly ControllerInfo[] | undefined, ledDevices?: ServerInfo['ledDevices']): DeviceProblem[] {
  const problems: DeviceProblem[] = [];
  for (const c of controllers ?? []) {
    const st = deviceStatus(c, isUsbDevice(c.type, ledDevices));
    if (!st.problem) continue;
    const text = st.text.replace(/^./, (x) => x.toLowerCase());
    problems.push({ id: c.id, text: `${c.name || 'A device'} ${text}.`, tone: st.tone === 'bad' ? 'bad' : 'warn' });
  }
  return problems;
}
