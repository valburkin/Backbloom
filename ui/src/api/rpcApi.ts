import type {
  Backend,
  BackendConfig,
  ChannelAdjustment,
  DiscoveredDevice,
  ServerInfo,
  SettingsSchema,
  SysInfo,
  TokenInfo,
} from './types';

/**
 * Events the UI subscribes to via `serverinfo`. settings-update is only delivered to admin sessions.
 * performance-update is subscribed separately (see subscribePerformance): asking for it makes the backend
 * answer `serverinfo` without the server info.
 */
export const SUBSCRIPTIONS = [
  'components-update',
  'priorities-update',
  'instance-update',
  'leds-update',
  'adjustment-update',
  'imageToLedMapping-update',
  'settings-update',
  'controllers-update',
] as const;

/** Priority and origin the UI uses for colors and effects it starts (the backend's web-UI slot). */
export const UI_PRIORITY = 1;
export const UI_ORIGIN = 'Overglow UI';

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

  /** True when the backend requires a token for *any* command (typically remote connections). */
  async tokenRequired(): Promise<boolean> {
    return info<{ required?: boolean }>(await this.backend.call('authorize', 'tokenRequired')).required === true;
  }

  /** True when this session is not yet admin-authorized (needed for getconfig / setconfig). */
  async adminRequired(): Promise<boolean> {
    return info<{ adminRequired?: boolean }>(await this.backend.call('authorize', 'adminRequired')).adminRequired === true;
  }

  /** True when the backend still uses its factory-default password. */
  async newPasswordRequired(): Promise<boolean> {
    return (
      info<{ newPasswordRequired?: boolean }>(await this.backend.call('authorize', 'newPasswordRequired'))
        .newPasswordRequired === true
    );
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

  async changePassword(password: string, newPassword: string): Promise<void> {
    await this.backend.call('authorize', 'newPassword', { password, newPassword });
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
   * Writes settings to the session's current instance. The backend replaces each top-level section it receives
   * and keeps the others, so send whole sections. Values that fail its schema are silently reset to defaults.
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

  /** Restores a backup. The service shuts down afterwards to apply it. */
  async importSettings(config: unknown): Promise<void> {
    await this.backend.call('load-db', undefined, { config }, { timeoutMs: 15000 });
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

  /** Starts `performance-update` events for this connection (the reply carries no server info). */
  async subscribePerformance(): Promise<void> {
    await this.backend.call('serverinfo', undefined, { subscribe: ['performance-update'] });
  }

  /** Asks for a fresh round of `performance-update` events. */
  async requestPerformance(): Promise<void> {
    await this.backend.call('performance-counters', 'all');
  }

  // ---- devices ----

  /**
   * Blink one LED red → green → blue (~4.5 s): LED `index` of the controller with that id, or without a controller
   * the LED at that position among all controllers' LEDs.
   */
  async blinkLed(index: number, controllerId?: string): Promise<void> {
    const params = controllerId === undefined ? { blinkIndex: index } : { blinkIndex: index, controller: controllerId };
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
