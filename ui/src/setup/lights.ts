// "Add lights" (docs/guided-setup.md): one search across the room-light device types, one prompt per brand where
// pairing needs it, then "Which light is which" and the lights added to the layout. Pure helpers, tested.
import type { DiscoveredDevice, HomeAssistantLight, HueBridgeState } from '../api/types';
import { clampToSurface, surfaceSize, type Controller, type DriverSettings, type PathPoint, type Room } from '../layout';
import { withRole } from './roles';

export type LightBrand = 'philipshue' | 'nanoleaf' | 'lifx' | 'wiz' | 'yeelight' | 'govee' | 'home_assistant';

export interface BrandInfo {
  type: LightBrand;
  name: string;
  /** What connecting needs: the bridge's button, the power button held, an access token, or nothing. */
  pairing: 'hue' | 'nanoleaf' | 'token' | null;
  /** Shown under the brand when a light may be missing: how to make it findable. */
  missing?: string;
}

/** The room-light brands, in the order the results show them. */
export const BRANDS: readonly BrandInfo[] = [
  { type: 'philipshue', name: 'Philips Hue', pairing: 'hue', missing: 'The bridge must be on the same network as this PC.' },
  { type: 'govee', name: 'Govee', pairing: null, missing: 'Missing a light? In the Govee Home app, open the light, tap the gear and turn on LAN Control.' },
  { type: 'nanoleaf', name: 'Nanoleaf', pairing: 'nanoleaf' },
  { type: 'lifx', name: 'LIFX', pairing: null },
  { type: 'wiz', name: 'WiZ', pairing: null, missing: 'Missing a bulb? In the WiZ app, turn on Settings → Security → Allow local communication.' },
  { type: 'yeelight', name: 'Yeelight', pairing: null, missing: 'Missing a light? In the Yeelight app, open the light, tap ⋯ and turn on LAN Control.' },
  { type: 'home_assistant', name: 'Home Assistant', pairing: 'token' },
];

export function brandInfo(type: LightBrand): BrandInfo {
  return BRANDS.find((b) => b.type === type)!;
}

/** The brands this backend has a device type for (an older backend may not have Govee yet). */
export function searchableBrands(available: ReadonlyArray<{ name: string }> | undefined): LightBrand[] {
  const names = new Set((available ?? []).map((d) => d.name));
  return BRANDS.map((b) => b.type).filter((t) => names.has(t));
}

/** One light, ready to be named, placed and added. */
export interface FoundLight {
  /** Unique across the search: brand, where it is reached, and its id there. */
  key: string;
  brand: LightBrand;
  /** The name it came with ("Lamp #0", "Desk lamp"). */
  name: string;
  /** Where it is reached (a light's IP, or its bridge, Nanoleaf or Home Assistant server). */
  host: string;
  /** Hue: the light id on the bridge; Home Assistant: its entity id. */
  id?: string;
  /** Its entry in the device's lamp list (LIFX, WiZ, Govee: `lamps`; Yeelight: `lights`). */
  lamp?: Record<string, unknown>;
  /** Nanoleaf: its panels (one LED each). */
  panels?: number;
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : '';
}

function hostAndPort(value: string): { host: string; port?: number } {
  const m = /^(.*):(\d+)$/.exec(value);
  return m ? { host: m[1], port: Number(m[2]) } : { host: value };
}

/** The lights a search found for a brand whose lights answer by themselves (LIFX, WiZ, Yeelight, Govee). */
export function lightsFromSearch(brand: LightBrand, devices: readonly DiscoveredDevice[]): FoundLight[] {
  switch (brand) {
    case 'lifx':
    case 'wiz':
      return devices.map((d) => {
        const ip = str(d.ipAddress) || d.value;
        return { key: `${brand}:${str(d.macAddress) || ip}`, brand, name: d.name, host: ip, lamp: { ipAddress: ip, macAddress: str(d.macAddress), name: d.name } };
      });
    case 'govee':
      return devices.map((d) => {
        const ip = str(d.ipAddress) || d.value;
        const lamp: Record<string, unknown> = { ipAddress: ip, name: d.name };
        if (str(d.sku)) lamp.sku = str(d.sku);
        if (str(d.deviceId)) lamp.deviceId = str(d.deviceId);
        return { key: `govee:${str(d.deviceId) || ip}`, brand, name: d.name, host: ip, lamp };
      });
    case 'yeelight':
      return devices.map((d) => {
        const { host, port } = hostAndPort(d.value);
        const ip = str(d.ip) || host;
        const p = Number(d.port) > 0 ? Number(d.port) : (port ?? 55443);
        return { key: `yeelight:${ip}`, brand, name: d.name, host: ip, lamp: { host: ip, port: p, name: d.name } };
      });
    default:
      return [];
  }
}

/** One streamed color of a Hue entertainment area (a channel), and the light behind it. */
export interface HueChannel {
  channel: number;
  /** The light's API v2 id, its v1 number (for identify) and name; empty when the bridge doesn't say. */
  lightId: string;
  v1: string;
  name: string;
}

/** A Hue entertainment area: one LED per channel, in the order the bridge streams them (the driver's order). */
export interface HueArea {
  id: string;
  name: string;
  channels: HueChannel[];
}

function v1Number(idV1: string | undefined): string {
  return /\/lights\/(\d+)$/.exec(idV1 ?? '')?.[1] ?? '';
}

/** The bridge's entertainment areas (API v2), each channel with its light. */
export function entertainmentAreas(state: HueBridgeState): HueArea[] {
  const service = new Map(state.services.map((s) => [s.id, s]));
  const light = new Map(state.lights.map((l) => [l.id, l]));
  return state.configurations.map((c, i) => ({
    id: c.id,
    name: c.metadata?.name || c.name || `Area ${i + 1}`,
    channels: (c.channels ?? []).map((ch, j) => {
      const rid = ch.members?.find((m) => m.service?.rtype === 'entertainment')?.service?.rid ?? '';
      const l = light.get(service.get(rid)?.renderer_reference?.rid ?? '');
      return { channel: ch.channel_id ?? j, lightId: l?.id ?? '', v1: v1Number(l?.id_v1), name: l?.metadata?.name || `Light ${j + 1}` };
    }),
  }));
}

/** The bridge's lights that can be in an entertainment area (they have an entertainment renderer), by name. */
export function hueColorLights(state: HueBridgeState): Array<{ id: string; name: string }> {
  const renderers = new Set(state.services.filter((s) => s.renderer !== false).map((s) => s.renderer_reference?.rid));
  return state.lights
    .filter((l) => renderers.has(l.id))
    .map((l) => ({ id: l.id, name: l.metadata?.name || 'Light' }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** A paired Nanoleaf (its panels from the device's info), as a found light. */
export function nanoleafLight(host: string, info: { name?: unknown; panelLayout?: { layout?: { numPanels?: unknown } } }): FoundLight {
  const panels = Number(info.panelLayout?.layout?.numPanels);
  return {
    key: `nanoleaf:${host}`,
    brand: 'nanoleaf',
    name: typeof info.name === 'string' && info.name ? info.name : 'Nanoleaf',
    host,
    panels: Number.isFinite(panels) && panels > 0 ? panels : 1,
  };
}

/**
 * The channels of a Hue area, as found lights (one LED each; a gradient light has several channels, numbered after its
 * name). `id` is the light's v1 number, which identify takes.
 */
export function hueLights(host: string, area: HueArea): FoundLight[] {
  const seen = new Map<string, number>();
  for (const c of area.channels) seen.set(c.lightId, (seen.get(c.lightId) ?? 0) + 1);
  const nth = new Map<string, number>();
  return area.channels.map((c) => {
    const n = (nth.get(c.lightId) ?? 0) + 1;
    nth.set(c.lightId, n);
    const name = (seen.get(c.lightId) ?? 1) > 1 ? `${c.name} ${n}` : c.name;
    return { key: `philipshue:${host}:${area.id}:${c.channel}`, brand: 'philipshue' as const, name, host, id: c.v1 };
  });
}

/** Home Assistant lights, as found lights. */
export function homeAssistantLights(host: string, lights: readonly HomeAssistantLight[]): FoundLight[] {
  return lights.map((l) => ({ key: `home_assistant:${host}:${l.id}`, brand: 'home_assistant' as const, name: l.name || l.id, host, id: l.id }));
}

/** What pairing gave, per device: the Hue bridge's user and area, a Nanoleaf's token, Home Assistant's token. */
export interface LightAuth {
  hue: Record<string, { username: string; clientkey: string; areaId: string }>;
  nanoleaf: Record<string, { token: string }>;
  homeAssistant: Record<string, { token: string }>;
}

export const NO_AUTH: LightAuth = { hue: {}, nanoleaf: {}, homeAssistant: {} };

/**
 * The `leddevice identify` parameters that make one light signal itself (each driver takes its own); null when it
 * can't be told to (a Hue channel whose light the bridge didn't name).
 */
export function identifyParams(light: FoundLight, auth: LightAuth): Record<string, unknown> | null {
  switch (light.brand) {
    case 'lifx':
    case 'wiz':
      return { ...light.lamp };
    case 'govee':
      return { ipAddress: light.host };
    case 'yeelight':
      return { hostname: light.host, port: light.lamp?.port ?? 55443 };
    case 'philipshue':
      return light.id && /^\d+$/.test(light.id) ? { host: light.host, user: auth.hue[light.host]?.username ?? '', lightId: Number(light.id) } : null;
    case 'nanoleaf':
      return { host: light.host, token: auth.nanoleaf[light.host]?.token ?? '' };
    case 'home_assistant':
      return { host: light.host, token: auth.homeAssistant[light.host]?.token ?? '', entityId: light.id };
  }
}

/**
 * Names to start "Which light is which" with: the light's own name, or the brand and a number for the names searches
 * make up ("Lamp #0", "Yeelight 192.168.1.48").
 */
export function suggestedNames(lights: readonly FoundLight[]): Map<string, string> {
  const count = new Map<LightBrand, number>();
  const out = new Map<string, string>();
  for (const l of lights) {
    if (!/#\d+$|\d+\.\d+\.\d+\.\d+/.test(l.name)) {
      out.set(l.key, l.name);
      continue;
    }
    const n = (count.get(l.brand) ?? 0) + 1;
    count.set(l.brand, n);
    out.set(l.key, `${brandInfo(l.brand).name} ${n}`);
  }
  return out;
}

/**
 * The addresses already in the layout (lower case): the devices' own (a Hue bridge, Home Assistant) and their lamps'
 * (LIFX, WiZ, Govee, Yeelight), so a search marks what is added already.
 */
export function knownLightHosts(controllers: readonly Controller[], deviceAddresses: ReadonlySet<string>): Set<string> {
  const out = new Set(deviceAddresses);
  for (const c of controllers)
    for (const list of ['lamps', 'lights'])
      for (const lamp of (Array.isArray(c.device?.[list]) ? (c.device[list] as Array<Record<string, unknown>>) : []))
        for (const k of ['ipAddress', 'host']) if (typeof lamp?.[k] === 'string' && lamp[k]) out.add(String(lamp[k]).toLowerCase());
  return out;
}

/** What the layout has already: addresses (devices and lamps), and Home Assistant entities per server. */
export interface KnownLights {
  hosts: Set<string>;
  /** `<server>|<entity id>`, lower case. */
  entities: Set<string>;
}

export function knownLights(controllers: readonly Controller[], deviceAddresses: ReadonlySet<string>): KnownLights {
  const entities = new Set<string>();
  for (const c of controllers) {
    const host = typeof c.device?.homeAssistantHost === 'string' ? c.device.homeAssistantHost.toLowerCase() : '';
    if (!host || !Array.isArray(c.device?.lamps)) continue;
    for (const lamp of c.device.lamps as Array<Record<string, unknown>>) if (typeof lamp?.name === 'string') entities.add(`${host}|${lamp.name.toLowerCase()}`);
  }
  return { hosts: knownLightHosts(controllers, deviceAddresses), entities };
}

/**
 * The light is in the layout already: by its address (a Hue bridge or Nanoleaf: the whole device), Home Assistant by
 * its entity, so new lights of a known server can still be added.
 */
export function isAdded(l: FoundLight, known: KnownLights): boolean {
  if (l.brand === 'home_assistant') return known.entities.has(`${l.host.toLowerCase()}|${(l.id ?? '').toLowerCase()}`);
  return known.hosts.has(l.host.toLowerCase());
}

/**
 * What is picked after a search: what was picked before and is still there, plus the lights found by themselves for
 * the first time (`before`: those the last search found), never what is in the layout already.
 */
export function selectionAfterSearch(
  prev: ReadonlySet<string>,
  before: ReadonlySet<string>,
  found: readonly FoundLight[],
  paired: readonly FoundLight[],
  known: KnownLights,
): Set<string> {
  const usable = new Set([...found, ...paired].filter((l) => !isAdded(l, known)).map((l) => l.key));
  const fresh = found.filter((l) => usable.has(l.key) && !before.has(l.key)).map((l) => l.key);
  return new Set([...[...prev].filter((k) => usable.has(k)), ...fresh]);
}

/** Where a light is in the room. */
export type LightPlace = 'left' | 'right' | 'behind' | 'above' | 'ceiling' | 'left-wall' | 'right-wall' | 'back';

export const LIGHT_PLACES: ReadonlyArray<{ value: LightPlace; label: string }> = [
  { value: 'left', label: 'Left of the screen' },
  { value: 'right', label: 'Right of the screen' },
  { value: 'behind', label: 'Behind the screen' },
  { value: 'above', label: 'Above the screen' },
  { value: 'ceiling', label: 'On the ceiling' },
  { value: 'left-wall', label: 'Left wall' },
  { value: 'right-wall', label: 'Right wall' },
  { value: 'back', label: 'Behind you' },
];

/** A first guess, so lights spread out: alternately left and right of the screen, then behind it. */
export function defaultPlace(index: number): LightPlace {
  return (['left', 'right', 'behind', 'above'] as const)[index % 4];
}

/**
 * A short path for a light at `place` (the `nth` light there is set beside the ones before), so the layout shows it
 * there.
 */
export function placePath(room: Room, place: LightPlace, nth = 0): PathPoint[] {
  const s = room.screen;
  const len = 0.12;
  const step = 0.2 * nth;
  const line = (surface: PathPoint['surface'], x: number, y: number): PathPoint[] => {
    const { w } = surfaceSize(room, surface);
    const x0 = Math.min(Math.max(0, x - len / 2), w - len);
    return [clampToSurface(room, { surface, x: x0, y }), clampToSurface(room, { surface, x: x0 + len, y })];
  };
  switch (place) {
    case 'left':
      return line('front', s.x - 0.25 - step, s.y + s.height * 0.75);
    case 'right':
      return line('front', s.x + s.width + 0.25 + step, s.y + s.height * 0.75);
    case 'behind':
      return line('front', s.x + s.width / 2 + (nth % 2 ? 1 : -1) * Math.ceil(nth / 2) * 0.2, s.y + s.height / 2);
    case 'above':
      return line('front', s.x + s.width / 2 + step, Math.max(0.05, s.y - 0.2));
    case 'ceiling':
      return line('ceiling', room.width / 2 + step, Math.max(0, room.depth - 0.8));
    case 'left-wall':
      return line('left', Math.max(0, room.depth - 0.6 - step), 1.2);
    case 'right-wall':
      return line('right', 0.6 + step, 1.2);
    case 'back':
      return line('back', room.width / 2 + step, 1.2);
  }
}

/** A light as the user named and placed it. */
export interface PlacedLight extends FoundLight {
  label: string;
  place: LightPlace;
}

/** A device to add, with one strip per light (each on its own output of length 1, Nanoleaf: one for its panels). */
export interface LightDevice {
  controller: Controller;
  strips: Array<{ output: number; name: string; path: PathPoint[] }>;
}

/** Where a device groups its lights: one device per brand, per Hue bridge, Nanoleaf and Home Assistant server. */
export function deviceKey(l: FoundLight): string {
  return l.brand === 'philipshue' || l.brand === 'nanoleaf' || l.brand === 'home_assistant' ? `${l.brand}:${l.host}` : l.brand;
}

const LAMP_LIST: Partial<Record<LightBrand, string>> = { lifx: 'lamps', wiz: 'lamps', govee: 'lamps', yeelight: 'lights', home_assistant: 'lamps' };

/**
 * The devices for the placed lights. `settings(type, extra)` gives a device type's default settings plus `extra`
 * (from the settings schema). Hue lights keep the area's channel order, the others the order they were found.
 */
export function lightDevices(
  lights: readonly PlacedLight[],
  auth: LightAuth,
  room: Room,
  settings: (type: string, extra: Record<string, unknown>) => DriverSettings,
  brandName: (type: LightBrand) => string = (t) => brandInfo(t).name,
): LightDevice[] {
  const groups = new Map<string, PlacedLight[]>();
  for (const l of lights) groups.set(deviceKey(l), [...(groups.get(deviceKey(l)) ?? []), l]);
  const nth = new Map<LightPlace, number>();
  const pathFor = (place: LightPlace) => {
    const n = nth.get(place) ?? 0;
    nth.set(place, n + 1);
    // a room light shows the screen's average: soft light at the edge of vision (roles.ts)
    return withRole(placePath(room, place, n), 'soft');
  };
  const out: LightDevice[] = [];
  for (const [key, group] of groups) {
    const brand = group[0].brand;
    const host = group[0].host;
    // Hue: the found order is the area's channel order, which the bridge streams in
    const ordered = group;
    let extra: Record<string, unknown>;
    switch (brand) {
      case 'philipshue': {
        const a = auth.hue[host];
        extra = {
          output: host,
          username: a?.username ?? '',
          clientkey: a?.clientkey ?? '',
          useEntertainmentAPI: true,
          useEntertainmentAPIV2: true,
          entertainmentConfigurationId: a?.areaId ?? '',
        };
        break;
      }
      case 'nanoleaf':
        extra = { host, token: auth.nanoleaf[host]?.token ?? '' };
        break;
      case 'home_assistant':
        extra = {
          homeAssistantHost: host,
          longLivedAccessToken: auth.homeAssistant[host]?.token ?? '',
          lamps: ordered.map((l) => ({ name: l.id, colorModel: 0 })),
        };
        break;
      default:
        extra = { [LAMP_LIST[brand]!]: ordered.map((l) => ({ ...l.lamp, name: l.label.trim() || l.name })) };
    }
    const single = brand === 'nanoleaf';
    const outputs = single
      ? [{ index: 0, start: 0, length: Math.max(1, group[0].panels ?? 1) }]
      : ordered.map((_, i) => ({ index: i, start: i, length: 1 }));
    const name = brandName(brand);
    out.push({
      controller: { id: key.replace(/[^a-z0-9]+/gi, '-').toLowerCase(), name: single ? group[0].label.trim() || name : name, kind: brand, host: '', outputs, device: settings(brand, extra) },
      strips: ordered.map((l, i) => ({ output: single ? 0 : i, name: l.label.trim() || l.name, path: pathFor(l.place) })),
    });
  }
  return out;
}
