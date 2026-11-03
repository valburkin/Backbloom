import type { BackendConfig, JsonSchema } from '../../api/types';
import type { Controller, LayoutSpec } from '../../layout';
import { withDefaults } from '../../schema/schema';

/** Driver groups as the backend reports them, in the order the picker shows them. */
const GROUPS: Record<string, string> = {
  leds_group_2_network: 'Network',
  leds_group_3_serial: 'USB',
  leds_group_6_HID: 'USB',
  leds_group_5_debug: 'Testing',
};
const GROUP_ORDER = ['Network', 'USB', 'Testing'];

export const DRIVER_NAMES: Record<string, string> = {
  wled: 'WLED',
  ddp: 'DDP (WLED, ESPixelStick…)',
  udpartnet: 'Art-Net',
  udpe131: 'E1.31 / sACN',
  philipshue: 'Philips Hue',
  home_assistant: 'Home Assistant lights',
  zigbee2mqtt: 'Zigbee2MQTT lights',
  lifx: 'LIFX',
  nanoleaf: 'Nanoleaf',
  wiz: 'WiZ',
  govee: 'Govee',
  yeelight: 'Yeelight',
  adalight: 'Adalight / HyperSerial',
  sp616e: 'SP616E',
  skydimo: 'Skydimo',
  synclight: 'SyncLight (USB)',
  file: 'Test device (writes to a file)',
};

export function driverName(type: string): string {
  return DRIVER_NAMES[type] ?? type;
}

export function groupName(group: string): string {
  return GROUPS[group] ?? group.replace(/^leds_group_\d+_/, '');
}

export interface DriverOption {
  value: string;
  label: string;
  group: string;
}

/** Device type picker options, grouped (Network / USB / Testing) and sorted by name within each group. */
export function driverOptions(available: ReadonlyArray<{ name: string; group: string }> | undefined): DriverOption[] {
  const rank = (g: string) => (GROUP_ORDER.indexOf(g) + 1 || GROUP_ORDER.length + 1);
  return (available ?? [])
    .map((d) => ({ value: d.name, label: driverName(d.name), group: groupName(d.group) }))
    .sort((a, b) => rank(a.group) - rank(b.group) || a.label.localeCompare(b.label));
}

/** Network drivers whose discovery (mDNS / SSDP) fills in an address setting. */
const NETWORK_TARGETS: Record<string, string> = {
  wled: 'host',
  nanoleaf: 'host',
  philipshue: 'output',
  home_assistant: 'homeAssistantHost',
};

/** Which setting a discovered device fills in, or null when the driver can't discover devices. */
export function discoveryTarget(type: string, group: string | undefined): string | null {
  if (NETWORK_TARGETS[type]) return NETWORK_TARGETS[type];
  if (group === 'USB') return 'output';
  return null;
}

/** Discovery on the network (asks other devices, so it is explained once first); USB enumeration stays on this PC. */
export function discoversOnNetwork(type: string): boolean {
  return type in NETWORK_TARGETS;
}

/**
 * Lamp devices whose settings list the lamps, one color each: the number of LEDs follows that list instead of being
 * asked for. Philips Hue isn't one: its lights come from the bridge (an entertainment area) when it connects.
 */
const LAMP_LISTS: Record<string, string> = {
  home_assistant: 'lamps',
  zigbee2mqtt: 'lamps',
  lifx: 'lamps',
  wiz: 'lamps',
  govee: 'lamps',
  yeelight: 'lights',
};

/** Number of LEDs a lamp device's settings define (one per lamp, at least 1), or null for other device types. */
export function lampCount(device: Record<string, unknown> | undefined): number | null {
  const list = LAMP_LISTS[String(device?.type ?? '')];
  if (!list) return null;
  const lamps = device?.[list];
  return Math.max(1, Array.isArray(lamps) ? lamps.length : 0);
}

/** A lamp device's lamp list (empty when it has none yet), or null for other device types. */
export function lampList(device: Record<string, unknown> | undefined): unknown[] | null {
  const list = LAMP_LISTS[String(device?.type ?? '')];
  if (!list) return null;
  const lamps = device?.[list];
  return Array.isArray(lamps) ? lamps : [];
}

/** Whether a device type's number of LEDs comes from its lamp list. */
export function isLampDevice(type: string): boolean {
  return type in LAMP_LISTS;
}

/** What the number of LEDs is called for a device type. */
export function ledCountLabel(type: string): string {
  return type === 'nanoleaf' ? 'Number of panels' : type === 'philipshue' ? 'Number of lights' : 'Number of LEDs';
}

type Obj = Record<string, unknown>;

/**
 * The `device` section after picking another driver: shared settings (color order, resend interval) are kept,
 * the driver's own settings start from its defaults — or from what was configured before, when switching back.
 */
export function switchDriver(current: Obj, type: string, schemas: { general?: JsonSchema; driver?: JsonSchema }, previous?: BackendConfig[string]): Obj {
  const prev = previous && typeof previous === 'object' && (previous as Obj).type === type ? (previous as Obj) : null;
  if (prev) return { ...prev };
  const shared: Obj = { type };
  for (const k of Object.keys(schemas.general?.properties ?? {})) if (k !== 'type' && current[k] !== undefined) shared[k] = current[k];
  return { ...(withDefaults(schemas.driver, {}) as Obj), ...shared };
}

/** The `file` device: a stand-in that writes colors to a file (or nowhere), what a new install starts with. */
export function isTestDevice(c: Controller): boolean {
  return (c.device?.type ?? c.kind) === 'file';
}

/** Where a device is reached, as typed in (WLED's host, a Hue bridge, a USB port…). */
export function addressOf(c: Controller): string {
  const d = c.device ?? {};
  for (const k of ['host', 'output', 'homeAssistantHost']) if (typeof d[k] === 'string' && d[k]) return String(d[k]);
  return c.host;
}

/** The addresses of the devices in the layout (lower case), to mark search results that are already added. */
export function addedAddresses(spec: LayoutSpec): Set<string> {
  const out = new Set<string>();
  for (const c of spec.controllers) {
    if (isTestDevice(c)) continue;
    for (const a of [addressOf(c), c.host]) if (a && a.toLowerCase() !== 'auto') out.add(a.toLowerCase());
  }
  return out;
}
