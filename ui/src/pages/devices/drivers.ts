import type { BackendConfig, JsonSchema } from '../../api/types';
import { withDefaults } from '../../schema/schema';

/** Driver groups as the backend reports them, in the order the picker shows them. */
const GROUPS: Record<string, string> = {
  leds_group_2_network: 'Network',
  leds_group_3_serial: 'USB',
  leds_group_6_HID: 'USB',
  leds_group_5_debug: 'Debug',
};
const GROUP_ORDER = ['Network', 'USB', 'Debug'];

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
  yeelight: 'Yeelight',
  adalight: 'Adalight / HyperSerial',
  sp616e: 'SP616E',
  skydimo: 'Skydimo',
  synclight: 'SyncLight (USB HID)',
  file: 'Write to a file (testing)',
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

/** Driver picker options, grouped (Network / USB / Debug) and sorted by name within each group. */
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
