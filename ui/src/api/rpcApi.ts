import type {
  ActiveProfile,
  Backend,
  BackendConfig,
  ChannelAdjustment,
  DiscoveredDevice,
  EffectPreviews,
  HomeAssistantLight,
  HueBridgeState,
  ConnectedBoard,
  ProbeCandidate,
  FindResult,
  ProfileState,
  RunningProgram,
  ScreenInfo,
  ServerInfo,
  SettingsSchema,
  SetupState,
  SysInfo,
  TokenInfo,
  WledChange,
  WledState,
  WledTest,
} from './types';
import { RpcError } from './types';

/** Events the UI subscribes to via `serverinfo`. settings-update is only delivered to admin sessions. */
export const SUBSCRIPTIONS = [
  'components-update',
  'priorities-update',
  'instance-update',
  'leds-update',
  'adjustment-update',
  'imageToLedMapping-update',
  'settings-update',
  'controllers-update',
  'capture-update',
  'performance-update',
  'setup-update',
  'sound-update',
  'profile-update',
] as const;

/** Priority and origin the UI uses for colors and effects it starts (the backend's web-UI slot). */
export const UI_PRIORITY = 1;
export const UI_ORIGIN = 'Backbloom UI';

function info<T>(reply: { info?: unknown }): T {
  return (reply.info ?? {}) as T;
}

/** Typed wrappers around the JSON-RPC commands this UI uses. */
export class RpcApi {
  readonly backend: Backend;
  constructor(backend: Backend) {
    this.backend = backend;
  }

  // ---- authorization ----

  /**
   * True when this session must sign in: a connection from another device (this computer always has full access,
   * see the backend's LocalAccess).
   */
  async adminRequired(): Promise<boolean> {
    return info<{ adminRequired?: boolean }>(await this.backend.call('authorize', 'adminRequired')).adminRequired === true;
  }

  /** Password login. Resolves with the session token (reusable via loginWithToken) when the backend returns one. */
  async login(password: string): Promise<string | undefined> {
    return info<{ token?: string }>(await this.backend.call('authorize', 'login', { password })).token;
  }

  async loginWithToken(token: string): Promise<void> {
    await this.backend.call('authorize', 'login', { token });
  }

  async logout(): Promise<void> {
    await this.backend.call('authorize', 'logout');
  }

  /**
   * Sets the password. `current` is needed only from another device that signed in; this computer (and the first
   * password) doesn't need it.
   */
  async setPassword(newPassword: string, current?: string): Promise<void> {
    await this.backend.call('authorize', 'newPassword', current === undefined ? { newPassword } : { password: current, newPassword });
  }

  async listTokens(): Promise<TokenInfo[]> {
    const list = info<unknown>(await this.backend.call('authorize', 'getTokenList'));
    return Array.isArray(list) ? (list as TokenInfo[]) : [];
  }

  /** Creates an API token for another app. The secret is only returned once. */
  async createToken(comment: string): Promise<{ id: string; token: string; comment: string }> {
    return info(await this.backend.call('authorize', 'createToken', { comment }));
  }

  async deleteToken(id: string): Promise<void> {
    await this.backend.call('authorize', 'deleteToken', { id });
  }

  // ---- information ----

  async serverInfo(subscribe: readonly string[] = []): Promise<ServerInfo> {
    const params = subscribe.length ? { subscribe: [...subscribe] } : undefined;
    return info<ServerInfo>(await this.backend.call('serverinfo', undefined, params));
  }

  async sysInfo(): Promise<SysInfo> {
    return info<SysInfo>(await this.backend.call('sysinfo'));
  }

  // ---- configuration (admin) ----

  /** Full settings of the session's current instance (global settings included). */
  async getConfig(): Promise<BackendConfig> {
    return info<BackendConfig>(await this.backend.call('config', 'getconfig'));
  }

  async getSchema(): Promise<SettingsSchema> {
    return info<SettingsSchema>(await this.backend.call('config', 'getschema'));
  }

  /**
   * Writes settings. The backend replaces each top-level section it receives and keeps the others, so send whole
   * sections. Invalid values reject the whole call (nothing is stored); the RpcError's reply lists them in `errors`.
   */
  async setConfig(config: BackendConfig): Promise<void> {
    await this.backend.call('config', 'setconfig', { config }, { timeoutMs: 15000 });
  }

  /** Changes which instance this *connection* talks to (per-session API state, not a config change). */
  async switchInstance(instance: number): Promise<void> {
    await this.backend.call('instance', 'switchTo', { instance });
  }

  /** Backup of every setting and instance (the "export" file). */
  async exportSettings(): Promise<unknown> {
    return info(await this.backend.call('save-db', undefined, undefined, { timeoutMs: 15000 }));
  }

  /**
   * Restores a backup while the app keeps running (the current settings are saved to its `backups` folder first).
   * Resolves with what the backend changed to make the backup fit this version.
   */
  async importSettings(config: unknown): Promise<string[]> {
    const changes = info<{ changes?: unknown }>(await this.backend.call('load-db', undefined, { config }, { timeoutMs: 15000 })).changes;
    return Array.isArray(changes) ? changes.map(String) : [];
  }

  // ---- control ----

  async setComponent(component: string, state: boolean): Promise<void> {
    await this.backend.call('componentstate', undefined, { componentstate: { component, state } });
  }

  async setColor(rgb: readonly [number, number, number], duration?: number): Promise<void> {
    await this.backend.call('color', undefined, {
      color: [...rgb],
      priority: UI_PRIORITY,
      origin: UI_ORIGIN,
      ...(duration ? { duration } : {}),
    });
  }

  /** A looping preview of one effect (or all) on the user's LEDs for the effect cards (rendered by the backend, not on the lights). */
  async effectPreviews(name?: string): Promise<EffectPreviews> {
    const i = info<{ fps?: number; ledCount?: number; standard?: boolean; previews?: Array<{ name: string; frames: string }> }>(
      await this.backend.call('effects', 'previews', name === undefined ? undefined : { name }),
    );
    const frames = new Map<string, Uint8Array>();
    for (const p of i.previews ?? []) frames.set(p.name, Uint8Array.from(atob(p.frames), (c) => c.charCodeAt(0)));
    return { fps: i.fps ?? 15, ledCount: i.ledCount ?? 0, standard: i.standard === true, frames };
  }

  async runEffect(name: string, duration?: number): Promise<void> {
    await this.backend.call('effect', undefined, {
      effect: { name },
      priority: UI_PRIORITY,
      origin: UI_ORIGIN,
      ...(duration ? { duration } : {}),
    });
  }

  /** Removes a color/effect source; -1 clears every source except the grabbers. */
  async clear(priority: number = UI_PRIORITY): Promise<void> {
    await this.backend.call('clear', undefined, { priority });
  }

  async selectSource(priority: number | 'auto'): Promise<void> {
    await this.backend.call('sourceselect', undefined, priority === 'auto' ? { auto: true } : { priority });
  }

  /** Changes color adjustments immediately (the backend also stores them in the `color` settings). */
  async adjust(adjustment: Partial<ChannelAdjustment>): Promise<void> {
    await this.backend.call('adjustment', undefined, { adjustment });
  }

  async ledStream(on: boolean): Promise<void> {
    await this.backend.call('ledcolors', on ? 'ledstream-start' : 'ledstream-stop');
  }

  async logStream(on: boolean): Promise<void> {
    await this.backend.call('logging', on ? 'start' : 'stop');
  }

  /** Asks for a fresh round of `performance-update` events. */
  async requestPerformance(): Promise<void> {
    await this.backend.call('performance-counters', 'all');
  }

  // ---- first run, network access ----

  async setupState(): Promise<SetupState> {
    return info<SetupState>(await this.backend.call('setup', 'getstate'));
  }

  /** "Start lighting": ends the first-run hold (the LEDs still wait while HyperHDR runs); optionally sets autostart. */
  async startLighting(autostart?: boolean): Promise<SetupState> {
    return info<SetupState>(await this.backend.call('setup', 'start', autostart === undefined ? {} : { autostart }));
  }

  async setAutostart(enabled: boolean): Promise<SetupState> {
    return info<SetupState>(await this.backend.call('setup', 'autostart', { enabled }));
  }

  /** Remembers that the user has read an explanation ("discovery"). */
  async acknowledge(topic: 'discovery'): Promise<SetupState> {
    return info<SetupState>(await this.backend.call('setup', 'acknowledge', { topic }));
  }

  // ---- presets (layout profiles) ----

  /**
   * Turns the preset `id` on by hand, whatever programs run, until `dismissProfile`, another `setProfile`, the preset
   * being removed or a restart; null goes back to the automatic choice by the running programs. Nothing is saved.
   * Turning one on clears the colors and effects that were started (so its strips show). Resolves with the preset
   * that is on now (`serverinfo.profile`).
   */
  async setProfile(id: string | null): Promise<ActiveProfile | null> {
    return info<Partial<ProfileState>>(await this.backend.call('profile', 'select', { id })).profile ?? null;
  }

  /**
   * Stops using the preset in use (the UI's switch to Everyday): one picked by hand goes back to the automatic choice;
   * one its program uses stays unused until none of its programs runs any more. Resolves with the state after it.
   */
  async dismissProfile(): Promise<ProfileState> {
    const state = info<Partial<ProfileState>>(await this.backend.call('profile', 'dismiss'));
    return { profile: state.profile ?? null, dismissed: state.dismissed ?? null };
  }

  /** Programs with a window open (one per executable, sorted), for the program pickers (Presets, Layout). */
  async runningPrograms(): Promise<RunningProgram[]> {
    const programs = info<{ programs?: unknown }>(await this.backend.call('profile', 'programs')).programs;
    return Array.isArray(programs)
      ? programs.filter((p): p is RunningProgram => !!p && typeof (p as RunningProgram).exe === 'string' && typeof (p as RunningProgram).title === 'string')
      : [];
  }
  // ---- devices ----

  /**
   * Blink one LED red → green → blue (~4.5 s): LED `index` of the controller with that id, or without a controller
   * the LED at that position among all controllers' LEDs. With `hold` it stays lit (cycling red, green, blue) until
   * the next blink, `stopBlink` or 120 s. Every blink replaces the one before (on any controller).
   */
  async blinkLed(index: number, controllerId?: string, hold?: boolean): Promise<void> {
    const params: Record<string, unknown> = { blinkIndex: index };
    if (controllerId !== undefined) params.controller = controllerId;
    if (hold) params.hold = true;
    await this.backend.call('leddevice', 'identify', { ledDeviceType: 'blink', params });
  }

  /** End a blink (a held one, usually): on the controller with that id, or on every controller. */
  async stopBlink(controllerId?: string): Promise<void> {
    const params = controllerId === undefined ? { stop: true } : { stop: true, controller: controllerId };
    await this.backend.call('leddevice', 'identify', { ledDeviceType: 'blink', params });
  }

  /** Devices the driver can find on the network / serial ports; `value` goes into the host or port setting. */
  async discover(ledDeviceType: string): Promise<DiscoveredDevice[]> {
    const res = info<{ devices?: unknown }>(
      await this.backend.call('leddevice', 'discover', { ledDeviceType, params: {} }, { timeoutMs: 15000 }),
    );
    return Array.isArray(res.devices)
      ? res.devices.filter((d): d is DiscoveredDevice => !!d && typeof d === 'object' && typeof (d as DiscoveredDevice).value === 'string')
      : [];
  }

  /** A driver tool's answer about one device (`leddevice getProperties`); an `error` in it rejects. */
  async deviceProperties<T = Record<string, unknown>>(ledDeviceType: string, params: Record<string, unknown>, timeoutMs = 15000): Promise<T> {
    const res = info<{ properties?: unknown }>(await this.backend.call('leddevice', 'getProperties', { ledDeviceType, params }, { timeoutMs }));
    const properties = (res.properties && typeof res.properties === 'object' ? res.properties : {}) as Record<string, unknown>;
    if (typeof properties.error === 'string' && properties.error) throw new RpcError(properties.error, 'leddevice-getProperties');
    return properties as T;
  }

  /** Makes one light of a room-light driver signal itself (blink, flash), with the driver's own parameters. */
  async identifyLight(ledDeviceType: string, params: Record<string, unknown>): Promise<void> {
    await this.backend.call('leddevice', 'identify', { ledDeviceType, params }, { timeoutMs: 10000 });
  }

  /** Pairs with a Hue bridge whose button was just pressed: the app's user name and client key on it. */
  async huePair(host: string): Promise<{ username: string; clientkey: string }> {
    const p = await this.deviceProperties<{ username?: unknown; clientkey?: unknown }>('philipshue', { host, pair: true });
    if (typeof p.username !== 'string' || !p.username) throw new RpcError('The bridge didn’t answer with a user name.', 'leddevice-getProperties');
    return { username: p.username, clientkey: typeof p.clientkey === 'string' ? p.clientkey : '' };
  }

  /** Creates an entertainment area on a Hue bridge from lights (their ids); resolves with the new area's id. */
  async hueCreateArea(host: string, user: string, name: string, lights: string[]): Promise<string> {
    const p = await this.deviceProperties<{ id?: unknown }>('philipshue', { host, user, createArea: { name, lights } });
    if (p.id === undefined || p.id === null || p.id === '') throw new RpcError('The bridge didn’t create the area.', 'leddevice-getProperties');
    return String(p.id);
  }

  /** A paired Hue bridge's entertainment areas, lights and their entertainment services (API v2). */
  async hueBridge(host: string, user: string): Promise<HueBridgeState> {
    const read = async (resource: string) => {
      const r = await this.deviceProperties<{ data?: unknown; errors?: Array<{ description?: string }> }>('philipshue', { host, user, filter: `/clip/v2/resource/${resource}` });
      if (!Array.isArray(r.data) && r.errors?.length) throw new RpcError(r.errors[0].description || 'The bridge refused the request.', 'leddevice-getProperties');
      return Array.isArray(r.data) ? r.data : [];
    };
    const [configurations, lights, services] = await Promise.all([read('entertainment_configuration'), read('light'), read('entertainment')]);
    return { configurations, lights, services } as HueBridgeState;
  }

  /** Pairs with a Nanoleaf whose power button was just held: its access token. */
  async nanoleafPair(host: string): Promise<string> {
    const p = await this.deviceProperties<{ token?: unknown }>('nanoleaf', { host, pair: true });
    if (typeof p.token !== 'string' || !p.token) throw new RpcError('The Nanoleaf didn’t answer with a token.', 'leddevice-getProperties');
    return p.token;
  }

  /** The lights a Home Assistant server offers, signed in with a long-lived access token. */
  async homeAssistantLights(host: string, token: string): Promise<HomeAssistantLight[]> {
    const lights = (await this.deviceProperties<{ lights?: unknown }>('home_assistant', { host, token })).lights;
    return Array.isArray(lights) ? lights.filter((l): l is HomeAssistantLight => !!l && typeof (l as HomeAssistantLight).id === 'string') : [];
  }

  // ---- hardware: setting up new boards (see docs/guided-setup.md) ----

  private async hardware<T>(subcommand: string, params?: Record<string, unknown>, timeoutMs = 15000): Promise<T> {
    return info<T>(await this.backend.call('hardware', subcommand, params, { timeoutMs }));
  }

  /** Every WLED board this PC can find: on USB, on the home network, and new boards' hotspots (takes several seconds). */
  async findBoards(): Promise<FindResult> {
    const r = await this.hardware<Partial<FindResult>>('find', undefined, 60000);
    return {
      boards: Array.isArray(r.boards) ? r.boards : [],
      wifi: { adapter: r.wifi?.adapter ?? null, currentSsid: r.wifi?.currentSsid ?? null, blocked: r.wifi?.blocked ?? null, savedPassword: r.wifi?.savedPassword === true },
      usbSilent: Array.isArray(r.usbSilent) ? r.usbSilent : [],
    };
  }

  /**
   * Puts a found board on the home Wi-Fi: over USB, or from its hotspot (this PC joins it for a moment and comes back:
   * up to a minute, `hardware-progress-update` events to this connection on the way).
   */
  async connectWifi(id: string, ssid: string, password: string | { useSavedPassword: true }): Promise<ConnectedBoard> {
    const secret = typeof password === 'string' ? { password } : { useSavedPassword: true };
    return this.hardware<ConnectedBoard>('connect-wifi', { id, ssid, ...secret }, 120000);
  }

  /** Flashes a WLED board's lights three times ("which one is it?"). */
  async wledIdentify(host: string): Promise<void> {
    await this.hardware('wled-identify', { host }, 15000);
  }

  /**
   * Finding the strips: temporary outputs on the likely pins (round 1: the common ones, round 2: the others), 60–300
   * LEDs each, the first 20 lit white and dim, the rest black; the board restarts (up to ~15 s).
   */
  async wledProbeStart(host: string, round: 1 | 2): Promise<ProbeCandidate[]> {
    const c = (await this.hardware<{ candidates?: unknown }>('wled-probe-start', { host, round }, 60000)).candidates;
    return Array.isArray(c) ? (c as ProbeCandidate[]) : [];
  }

  /** Ends finding the strips: `keep` the outputs written since, else back to the board's outputs from before. */
  async wledProbeEnd(host: string, keep: boolean): Promise<void> {
    await this.hardware('wled-probe-end', { host, keep }, 60000);
  }

  /** The monitors and their sizes from EDID. */
  async screens(): Promise<ScreenInfo[]> {
    const s = (await this.hardware<{ screens?: unknown }>('screens')).screens;
    return Array.isArray(s) ? (s as ScreenInfo[]) : [];
  }

  async wledRead(host: string): Promise<WledState> {
    return this.hardware<WledState>('wled-read', { host }, 8000);
  }

  /** Changes a WLED board's settings; `restarting` while it restarts to apply them (poll wledRead). */
  async wledWrite(host: string, change: WledChange): Promise<boolean> {
    return (await this.hardware<{ restarting?: unknown }>('wled-write', { host, ...change }, 15000)).restarting === true;
  }


  /**
   * Lights a check on one output of the board: `first`, `range` (the first `count` LEDs dim, the last bright),
   * `color`, `white` (full brightness at the board's limit), `points` (the output dim, the LEDs in `leds` bright, counted
   * from 1, at most 64) or `off` (the board's state from before the first check).
   */
  async wledTest(host: string, test: WledTest): Promise<void> {
    await this.hardware('wled-test', { host, ...test }, 15000);
  }
}
