import type {
  ActiveProfile,
  Backend,
  BackendConfig,
  ChannelAdjustment,
  DiscoveredDevice,
  EffectPreviews,
  RunningProgram,
  ServerInfo,
  SettingsSchema,
  SetupState,
  SysInfo,
  TokenInfo,
} from './types';

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

  // ---- profiles ----

  /**
   * Runs the layout's profile `id` whatever programs run (to try it out), until called with null: back to choosing by
   * the running programs. Nothing is saved. Resolves with the profile that runs now (`serverinfo.profile`).
   */
  async setProfile(id: string | null): Promise<ActiveProfile | null> {
    return info<{ profile?: ActiveProfile | null }>(await this.backend.call('profile', 'select', { id })).profile ?? null;
  }

  /** Programs with a window open (one per executable, sorted), for the profile editor's program picker. */
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
}
