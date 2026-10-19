import type { Backend, BackendConfig, ServerInfo, SysInfo } from './types';

/** Events the UI subscribes to via `serverinfo`. settings-update is only delivered to admin sessions. */
export const SUBSCRIPTIONS = [
  'components-update',
  'priorities-update',
  'instance-update',
  'leds-update',
  'settings-update',
] as const;

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

  /** Writes settings to the session's current instance. Send the *full* config (as the legacy UI does). */
  async setConfig(config: BackendConfig): Promise<void> {
    await this.backend.call('config', 'setconfig', { config }, { timeoutMs: 15000 });
  }

  /** Changes which instance this *connection* talks to (per-session API state, not a config change). */
  async switchInstance(instance: number): Promise<void> {
    await this.backend.call('instance', 'switchTo', { instance });
  }

  // ---- devices ----

  /** Blink one LED of the current instance red → green → blue (~4.5 s). */
  async blinkLed(index: number): Promise<void> {
    await this.backend.call('leddevice', 'identify', { ledDeviceType: 'blink', params: { blinkIndex: index } });
  }
}
