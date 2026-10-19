import legacyLeds from '../layout/__fixtures__/my-layout.json';
import wledTwoOutputs from '../layout/__fixtures__/wled-two-outputs.json';
import { BackendEvents } from './emitter';
import {
  ConnectionError,
  RpcError,
  type Backend,
  type BackendConfig,
  type CallOptions,
  type ConnectionStatus,
  type RpcEvent,
  type RpcReply,
} from './types';

export interface MockOptions {
  /** Require a password before config access (password: MOCK_PASSWORD). */
  requireAuth?: boolean;
  /** Simulated round-trip latency in ms. */
  latencyMs?: number;
  /** Seed config overrides (e.g. a saved `layout`). */
  config?: BackendConfig;
}

export const MOCK_PASSWORD = 'password';
export const MOCK_WLED_HOST = 'wled-mock.local';
const MOCK_TOKEN = 'mock-user-token-0123456789abcdef0123456789abcdef';

function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T;
}

/**
 * In-memory stand-in for HyperHDR, built from the layout fixtures: one instance driving a two-output
 * WLED controller with the product owner's 168-LED legacy layout. Implements the same Backend interface
 * as WsClient, including auth errors, so the whole UI can run without a server (`?mock` / VITE_MOCK=1).
 */
export class MockBackend implements Backend {
  readonly mode = 'mock' as const;
  readonly url = 'mock://local';
  private _status: ConnectionStatus = 'idle';
  private events = new BackendEvents();
  private readonly latencyMs: number;
  private readonly requireAuth: boolean;
  private admin = false;
  private config: BackendConfig;
  /** Log of LED indexes blinked, for tests / debugging. */
  readonly blinks: number[] = [];

  constructor(options: MockOptions = {}) {
    this.latencyMs = options.latencyMs ?? 60;
    this.requireAuth = options.requireAuth ?? false;
    this.config = {
      general: { name: 'Mock PC' },
      device: { type: 'wled', host: MOCK_WLED_HOST, refreshTime: 0, colorOrder: 'rgb' },
      leds: clone(legacyLeds),
      ...clone(options.config ?? {}),
    };
  }

  get status(): ConnectionStatus {
    return this._status;
  }

  connect(): void {
    if (this._status === 'open' || this._status === 'connecting') return;
    this.setStatus('connecting');
    setTimeout(() => {
      this.admin = !this.requireAuth;
      this.setStatus('open');
    }, this.latencyMs);
  }

  close(): void {
    this.setStatus('closed');
  }

  /** Simulate a dropped connection (auth state resets, like a real reconnect). */
  simulateDisconnect(): void {
    this.setStatus('reconnecting');
    setTimeout(() => this.connect(), 800);
  }

  onStatus(listener: (status: ConnectionStatus) => void): () => void {
    return this.events.onStatus(listener);
  }

  onEvent(command: string, listener: (event: RpcEvent) => void): () => void {
    return this.events.onEvent(command, listener);
  }

  async fetchWledConfig(host: string, signal?: AbortSignal): Promise<unknown> {
    await this.delay(350);
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    if (/fail|offline/i.test(host)) throw new TypeError('Failed to fetch');
    return clone(wledTwoOutputs);
  }

  async call(command: string, subcommand?: string, params: Record<string, unknown> = {}, _options?: CallOptions): Promise<RpcReply> {
    const full = subcommand ? `${command}-${subcommand}` : command;
    if (this._status !== 'open') throw new ConnectionError('Not connected to the lighting service', full);
    await this.delay(this.latencyMs);
    const ok = (info?: unknown): RpcReply => ({ command: full, success: true, info });
    const fail = (error: string, cmd = full): never => {
      throw new RpcError(error, cmd, { command: cmd, success: false, error });
    };

    switch (command) {
      case 'authorize':
        switch (subcommand) {
          case 'tokenRequired':
            return ok({ required: false });
          case 'adminRequired':
            return ok({ adminRequired: !this.admin });
          case 'newPasswordRequired':
            return ok({ newPasswordRequired: false });
          case 'logout':
            this.admin = !this.requireAuth;
            return ok();
          case 'login': {
            if (typeof params.token === 'string') {
              if (params.token === MOCK_TOKEN) {
                this.admin = true;
                return ok();
              }
              return fail('No Authorization');
            }
            const pw = String(params.password ?? '');
            if (pw.length < 8) return fail('Password too short');
            if (pw !== MOCK_PASSWORD) return fail('No Authorization');
            this.admin = true;
            return ok({ token: MOCK_TOKEN });
          }
          default:
            return fail('Command not implemented');
        }
      case 'serverinfo':
        return ok(this.serverInfo());
      case 'sysinfo':
        return ok({
          hyperhdr: { version: '22.0.0-mock', build: 'mock', readOnlyMode: false },
          system: { hostName: 'MOCK-PC', prettyName: 'Mock OS', architecture: 'x86_64' },
        });
      case 'config':
        if (subcommand === 'getconfig') {
          if (!this.admin) return fail('No Authorization', 'config');
          return ok(clone(this.config));
        }
        if (subcommand === 'setconfig') {
          if (!this.admin) return fail('No Authorization', 'config');
          const next = clone((params.config ?? {}) as BackendConfig);
          const changed = Object.keys(next).filter((k) => JSON.stringify(next[k]) !== JSON.stringify(this.config[k]));
          this.config = { ...this.config, ...next };
          for (const key of changed) {
            this.emit('settings-update', { [key]: next[key] });
            if (key === 'leds') this.emit('leds-update', { leds: next[key] });
          }
          return ok();
        }
        return fail('unknown or missing subcommand');
      case 'instance':
        if (subcommand === 'switchTo') {
          if (params.instance !== 0) return fail('Selected HyperHDR instance isn\'t running');
          return ok();
        }
        return fail('Command not implemented');
      case 'leddevice':
        if (subcommand === 'identify' && params.ledDeviceType === 'blink') {
          const idx = Number((params.params as { blinkIndex?: number } | undefined)?.blinkIndex);
          this.blinks.push(idx);
          console.info(`[mock] blink LED ${idx}`);
          return ok();
        }
        return fail('Unknown or missing subcommand');
      default:
        return fail('Command not implemented');
    }
  }

  private serverInfo() {
    return {
      hostname: 'MOCK-PC',
      currentInstance: 0,
      instance: [{ instance: 0, friendly_name: 'Living room', running: true }],
      components: [
        { name: 'ALL', enabled: true },
        { name: 'SYSTEMGRABBER', enabled: true },
        { name: 'LEDDEVICE', enabled: true },
        { name: 'SMOOTHING', enabled: true },
      ],
      priorities: [
        { priority: 245, componentId: 'SYSTEMGRABBER', origin: 'System', owner: 'DX11_SYSTEM', active: true, visible: true },
      ],
      leds: clone(this.config.leds),
      ledDevices: { available: [{ name: 'wled', group: 'leds_group_2_network' }] },
    };
  }

  private emit(command: string, data: unknown): void {
    setTimeout(() => this.events.emitEvent({ command, data }), 0);
  }

  private setStatus(s: ConnectionStatus): void {
    if (s === this._status) return;
    this._status = s;
    this.events.emitStatus(s);
  }

  private delay(ms: number): Promise<void> {
    return new Promise((r) => setTimeout(r, ms));
  }
}
