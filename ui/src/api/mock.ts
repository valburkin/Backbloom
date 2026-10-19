import { BackendEvents } from './emitter';
import type { MockFixtures } from './mockData';
import { UI_PRIORITY } from './rpcApi';
import {
  ConnectionError,
  RpcError,
  type Backend,
  type BackendConfig,
  type CallOptions,
  type ConnectionStatus,
  type JsonSchema,
  type PerformanceReport,
  type PriorityInfo,
  type RpcEvent,
  type RpcReply,
  type ServerInfo,
} from './types';
import { propertyAt, validate } from '../schema/schema';
import { setIn } from '../settings/drafts';

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
const COMPONENTS = ['ALL', 'HDR', 'SMOOTHING', 'BLACKBORDER', 'SYSTEMGRABBER', 'LEDDEVICE'];

type Rgb = [number, number, number];
type Source = { kind: 'color'; rgb: Rgb } | { kind: 'effect'; name: string };

function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T;
}

function hsv(h: number, s: number, v: number): Rgb {
  const f = (n: number) => {
    const k = (n + h / 60) % 6;
    return Math.round(255 * (v - v * s * Math.max(0, Math.min(k, 4 - k, 1))));
  };
  return [f(5), f(3), f(1)];
}

/** What the screen "shows" in demo mode: a slow sunset-to-ocean scene, sampled per LED position. */
function sceneColor(x: number, y: number, t: number): Rgb {
  const h = (200 + 60 * Math.sin(t / 4000 + x * 2.2) + 50 * Math.cos(t / 5300 + y * 1.7) + 360) % 360;
  return hsv(h, 0.75, 0.55 + 0.4 * Math.abs(Math.sin(t / 3000 + x + y)));
}

const LOG_LINES: Array<[string, string, string]> = [
  ['INFO', 'MAIN', 'Starting Overglow (demo)'],
  ['INFO', 'WEBSERVER', 'Started: Overglow WebServer (HTTP) on port 8090'],
  ['INFO', 'LEDDEVICE0_WLED', 'WLED controller wled-mock.local: 168 LEDs, 2 outputs'],
  ['INFO', 'SMOOTHING0', 'Smoothing: HybridRgbInterpolator, 150 ms, 50 Hz'],
  ['DEBUG', 'DX11_GRABBER', 'Capturing \\\\.\\DISPLAY5 at 30 fps, 512 px wide'],
  ['WARNING', 'LEDDEVICE0_WLED', 'Slow answer from wled-mock.local (180 ms)'],
  ['INFO', 'EFFECTENGINE0', 'Effect list refreshed: 54 effects'],
  ['ERROR', 'MQTT', 'Couldn’t reach the MQTT broker (demo message)'],
];

/**
 * In-memory stand-in for Overglow, built from fixtures captured from the real service: one instance driving
 * a two-output WLED controller with the product owner's 168-LED layout. Implements the same Backend interface
 * as WsClient — including auth errors, partial setconfig with silent "fixing" of invalid values, live LED
 * colors, logs and performance reports — so the whole UI runs without a server (`?mock` / VITE_MOCK=1).
 */
export class MockBackend implements Backend {
  readonly mode = 'mock' as const;
  readonly url = 'mock://local';
  private _status: ConnectionStatus = 'idle';
  private events = new BackendEvents();
  private readonly latencyMs: number;
  private readonly requireAuth: boolean;
  private readonly seed: BackendConfig;
  private fx: MockFixtures | null = null;
  private admin = false;
  private password = MOCK_PASSWORD;
  private config: BackendConfig = {};
  private components = new Map(COMPONENTS.map((c) => [c, c !== 'HDR' && c !== 'BLACKBORDER']));
  private source: Source | null = null;
  private autoSelect = true;
  private tokens: Array<{ id: string; comment: string; last_use: string }> = [
    { id: 'a1b2c', comment: 'Stream Deck plugin', last_use: '2026-10-12T19:41:00' },
  ];
  private timers = new Map<string, ReturnType<typeof setInterval>>();
  /** Adjustments applied with the `adjustment` command since the color settings were last saved. */
  private runtimeAdjustment: Record<string, unknown> | null = null;
  private logSeq = 0;
  /** Log of LED indexes blinked, for tests / debugging. */
  readonly blinks: number[] = [];

  constructor(options: MockOptions = {}) {
    this.latencyMs = options.latencyMs ?? 60;
    this.requireAuth = options.requireAuth ?? false;
    this.seed = clone(options.config ?? {});
  }

  get status(): ConnectionStatus {
    return this._status;
  }

  connect(): void {
    if (this._status === 'open' || this._status === 'connecting') return;
    this.setStatus('connecting');
    void import('./mockData').then(({ MOCK_FIXTURES }) => {
      if (!this.fx) {
        this.fx = MOCK_FIXTURES;
        this.config = { ...clone(MOCK_FIXTURES.config), leds: clone(MOCK_FIXTURES.leds), ...clone(this.seed) };
      }
      setTimeout(() => {
        this.admin = !this.requireAuth;
        this.setStatus('open');
      }, this.latencyMs);
    });
  }

  close(): void {
    this.stopTimers();
    this.setStatus('closed');
  }

  /** Simulate a dropped connection (auth state resets, like a real reconnect). */
  simulateDisconnect(): void {
    this.stopTimers();
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
    return clone(this.fx?.wledConfig);
  }

  async call(command: string, subcommand?: string, params: Record<string, unknown> = {}, _options?: CallOptions): Promise<RpcReply> {
    const full = subcommand ? `${command}-${subcommand}` : command;
    if (this._status !== 'open' || !this.fx) throw new ConnectionError('Not connected to the lighting service', full);
    await this.delay(this.latencyMs);
    const ok = (info?: unknown): RpcReply => ({ command: full, success: true, info });
    const fail = (error: string, cmd = full): never => {
      throw new RpcError(error, cmd, { command: cmd, success: false, error });
    };
    const needAdmin = () => {
      if (!this.admin) fail('No Authorization', full);
    };

    switch (command) {
      case 'authorize':
        return ok(this.authorize(subcommand, params, fail));
      case 'serverinfo':
        if (Array.isArray(params.subscribe) && params.subscribe.includes('performance-update')) this.startPerformance();
        return ok(this.serverInfo());
      case 'sysinfo':
        return ok(clone(this.fx.sysInfo));
      case 'config':
        if (subcommand === 'getschema') return ok(clone(this.fx.schema));
        needAdmin();
        if (subcommand === 'getconfig') return ok(clone(this.config));
        if (subcommand === 'setconfig') {
          if (!this.components.get('ALL')) return fail("Saving configuration while Overglow is disabled isn't possible");
          this.writeConfig(clone((params.config ?? {}) as BackendConfig));
          return ok();
        }
        return fail('unknown or missing subcommand');
      case 'instance':
        if (subcommand === 'switchTo') return params.instance === 0 ? ok() : fail("Selected Overglow instance isn't running");
        return fail('Command not implemented');
      case 'componentstate': {
        const { component, state } = (params.componentstate ?? {}) as { component?: string; state?: boolean };
        if (!component || !this.components.has(component)) return fail('invalid component name');
        this.components.set(component, !!state);
        this.emit('components-update', { name: component, enabled: !!state });
        this.emitPriorities();
        return ok();
      }
      case 'color':
        this.source = { kind: 'color', rgb: (params.color as Rgb).slice(0, 3) as Rgb };
        this.autoSelect = true;
        this.emitPriorities();
        return ok();
      case 'effect': {
        const name = (params.effect as { name?: string } | undefined)?.name ?? '';
        if (!this.fx.serverInfo.effects?.some((e) => e.name === name)) return fail(`Effect ${name} not found`);
        this.source = { kind: 'effect', name };
        this.autoSelect = true;
        this.emitPriorities();
        return ok();
      }
      case 'clear':
        if (params.priority === UI_PRIORITY || params.priority === -1) this.source = null;
        this.emitPriorities();
        return ok();
      case 'sourceselect':
        this.autoSelect = params.auto === true;
        this.emitPriorities();
        return ok();
      case 'adjustment': {
        // Like the backend: applied right away but not stored (getconfig keeps the saved values), while the
        // settings-update it broadcasts carries the adjusted color section.
        const color = this.config.color as { channelAdjustment: Array<Record<string, unknown>> };
        this.runtimeAdjustment = { ...this.activeAdjustment(), ...(params.adjustment as object) };
        this.emit('settings-update', { color: { ...color, channelAdjustment: [this.runtimeAdjustment] } });
        this.emit('adjustment-update', [this.runtimeAdjustment]);
        return ok();
      }
      case 'ledcolors':
        if (subcommand === 'ledstream-start') this.startLedStream();
        else if (subcommand === 'ledstream-stop') this.stopTimer('leds');
        return ok();
      case 'logging':
        needAdmin();
        if (subcommand === 'start') this.startLogs();
        else this.stopTimer('logs');
        return ok();
      case 'performance-counters':
        this.emitPerformance();
        return ok();
      case 'leddevice':
        return ok(this.ledDevice(subcommand, params, fail));
      case 'save-db':
        needAdmin();
        return ok({
          version: 'Overglow_export_format_v1',
          instances: [{ instance: 0, friendly_name: 'First LED instance', enabled: 1 }],
          settings: Object.entries(this.config).map(([type, c]) => ({ type, config: JSON.stringify(c), instance: 0 })),
        });
      case 'load-db':
        needAdmin();
        setTimeout(() => this.simulateDisconnect(), 300);
        return ok();
      default:
        return fail('Command not implemented');
    }
  }

  // ---- command handlers ----

  private authorize(subcommand: string | undefined, params: Record<string, unknown>, fail: (e: string) => never): unknown {
    switch (subcommand) {
      case 'tokenRequired':
        return { required: false };
      case 'adminRequired':
        return { adminRequired: !this.admin };
      case 'newPasswordRequired':
        return { newPasswordRequired: false };
      case 'logout':
        this.admin = !this.requireAuth;
        return undefined;
      case 'login': {
        if (typeof params.token === 'string') {
          if (params.token !== MOCK_TOKEN) return fail('No Authorization');
          this.admin = true;
          return undefined;
        }
        const pw = String(params.password ?? '');
        if (pw.length < 8) return fail('Password too short');
        if (pw !== this.password) return fail('No Authorization');
        this.admin = true;
        return { token: MOCK_TOKEN };
      }
      case 'newPassword':
        if (!this.admin) return fail('No Authorization');
        if (params.password !== this.password) return fail('Failed to change password');
        this.password = String(params.newPassword);
        return undefined;
      case 'getTokenList':
        if (!this.admin) return fail('No Authorization');
        return clone(this.tokens);
      case 'createToken': {
        if (!this.admin) return fail('No Authorization');
        const id = Math.random().toString(36).slice(2, 7);
        const comment = String(params.comment ?? '');
        this.tokens.push({ id, comment, last_use: '' });
        return { id, comment, token: `${id}-${'0'.repeat(31)}` };
      }
      case 'deleteToken':
        if (!this.admin) return fail('No Authorization');
        this.tokens = this.tokens.filter((t) => t.id !== params.id);
        return undefined;
      default:
        return fail('Command not implemented');
    }
  }

  private ledDevice(subcommand: string | undefined, params: Record<string, unknown>, fail: (e: string) => never): unknown {
    const type = String(params.ledDeviceType ?? '');
    switch (subcommand) {
      case 'identify':
        if (type === 'blink') {
          const idx = Number((params.params as { blinkIndex?: number } | undefined)?.blinkIndex);
          this.blinks.push(idx);
          console.info(`[mock] blink LED ${idx}`);
        }
        return undefined;
      case 'discover': {
        const serial = ['adalight', 'sp616e', 'skydimo', 'synclight'].includes(type);
        const devices = serial
          ? [
              { value: 'auto', name: 'Auto' },
              { value: 'COM3', name: 'USB-SERIAL CH340 (\\\\.\\COM3)' },
            ]
          : type === 'wled'
            ? [
                { value: MOCK_WLED_HOST, name: `192.168.1.40 (${MOCK_WLED_HOST})` },
                { value: 'wled-shelf.local', name: '192.168.1.57 (wled-shelf.local)' },
              ]
            : [];
        return { ledDeviceType: type, devices };
      }
      case 'getProperties':
        return { properties: {} };
      default:
        return fail('Unknown or missing subcommand');
    }
  }

  /** Partial setconfig: replace the sent sections; like the backend, reset invalid values to their defaults. */
  private writeConfig(next: BackendConfig): void {
    const schema = this.fx!.schema;
    for (const [section, value] of Object.entries(next)) {
      let fixed = value;
      const s = schema.properties[section] as JsonSchema | undefined;
      if (s && section !== 'layout' && section !== 'leds') {
        for (const issue of validate(s, value)) {
          const def = propertyAt(s, ...issue.path.split('.').map((p) => (/^\d+$/.test(p) ? '[]' : p)))?.default;
          if (def !== undefined) fixed = setIn(fixed, issue.path.split('.').map((p) => (/^\d+$/.test(p) ? Number(p) : p)), clone(def));
        }
      }
      const changed = JSON.stringify(fixed) !== JSON.stringify(this.config[section]);
      this.config[section] = fixed;
      if (!changed) continue;
      this.emit('settings-update', { [section]: fixed });
      if (section === 'leds') this.emit('leds-update', { leds: fixed });
      if (section === 'color') {
        this.runtimeAdjustment = null;
        this.emit('adjustment-update', (fixed as { channelAdjustment: unknown }).channelAdjustment);
      }
    }
  }

  // ---- state ----

  private priorities(): PriorityInfo[] {
    const list: PriorityInfo[] = [];
    if (this.source) {
      list.push({
        priority: UI_PRIORITY,
        componentId: this.source.kind === 'color' ? 'COLOR' : 'EFFECT',
        origin: 'Overglow UI@127.0.0.1',
        owner: this.source.kind === 'effect' ? this.source.name : undefined,
        active: true,
        visible: false,
        value: this.source.kind === 'color' ? { RGB: this.source.rgb } : undefined,
      });
    }
    if (this.components.get('SYSTEMGRABBER'))
      list.push({ priority: 245, componentId: 'SYSTEMGRABBER', origin: 'System', owner: 'DirectX11', active: true, visible: false });
    if (list.length) list[0].visible = true;
    return list;
  }

  private serverInfo(): ServerInfo {
    const fx = this.fx!;
    const color = this.config.color as { channelAdjustment?: unknown[]; imageToLedMappingType?: string };
    return {
      ...clone(fx.serverInfo),
      components: [...this.components].map(([name, enabled]) => ({ name, enabled })),
      priorities: this.priorities(),
      priorities_autoselect: this.autoSelect,
      activeEffects: this.source?.kind === 'effect' ? [{ name: this.source.name, priority: UI_PRIORITY, timeout: -1 }] : [],
      adjustment: [clone(this.activeAdjustment())] as ServerInfo['adjustment'],
      imageToLedMappingType: color.imageToLedMappingType,
      leds: clone(this.config.leds) as ServerInfo['leds'],
    };
  }

  private activeAdjustment(): Record<string, unknown> {
    return this.runtimeAdjustment ?? (this.config.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
  }

  private emitPriorities(): void {
    this.emit('priorities-update', { priorities: this.priorities(), priorities_autoselect: this.autoSelect });
  }

  // ---- streams ----

  private startLedStream(): void {
    if (this.timers.has('leds')) return;
    const started = Date.now();
    this.timers.set(
      'leds',
      setInterval(() => {
        const leds = (this.config.leds ?? []) as Array<{ hmin: number; hmax: number; vmin: number; vmax: number }>;
        const scale = Math.min(1, Number(this.activeAdjustment().scaleOutput ?? 1));
        const t = Date.now() - started;
        const on = this.components.get('ALL') && this.components.get('LEDDEVICE');
        const out: number[] = [];
        leds.forEach((r, i) => {
          const x = (r.hmin + r.hmax) / 2;
          const y = (r.vmin + r.vmax) / 2;
          let c: Rgb = [0, 0, 0];
          if (!on) c = [0, 0, 0];
          else if (this.source?.kind === 'color') c = this.source.rgb;
          else if (this.source?.kind === 'effect') c = hsv((i * 360) / Math.max(1, leds.length) + t / 12, 1, 1);
          else if (this.components.get('SYSTEMGRABBER')) c = sceneColor(x, y, t);
          out.push(Math.round(c[0] * scale), Math.round(c[1] * scale), Math.round(c[2] * scale));
        });
        this.emit('ledcolors-ledstream-update', { leds: out });
      }, 100),
    );
  }

  private startLogs(): void {
    if (this.timers.has('logs')) return;
    const line = () => {
      const [levelString, loggerName, message] = LOG_LINES[this.logSeq++ % LOG_LINES.length];
      return { levelString, loggerName, message, utime: String(Date.now()), fileName: 'mock.cpp', line: String(this.logSeq), function: 'demo' };
    };
    this.emit('logging-update', { messages: Array.from({ length: 6 }, line) });
    this.timers.set('logs', setInterval(() => this.emit('logging-update', { messages: [line()] }), 1500));
  }

  private startPerformance(): void {
    if (this.timers.has('perf')) return;
    this.timers.set('perf', setInterval(() => this.emitPerformance(), 4000));
  }

  private emitPerformance(): void {
    const jitter = () => Math.round(Math.random() * 10) / 10;
    const reports: PerformanceReport[] = [
      { type: 2, id: 0, name: 'First LED instance', param1: 29.8 + jitter(), param2: 0, param3: 0, param4: 0, token: 1, remove: 0 },
      { type: 3, id: 0, name: 'wled', param1: 49.6 + jitter(), param2: 2980, param3: 1790, param4: 0, token: 1, remove: 0 },
      {
        type: 4,
        id: -1,
        name: JSON.stringify({ cores: [0.12, 0.08, 0.2, 0.05], total: 6 + Math.round(Math.random() * 3) }),
        param1: 0,
        param2: 0,
        param3: 0,
        param4: 0,
        token: 0,
        remove: 0,
      },
      { type: 5, id: -1, name: JSON.stringify({ aspect: 36, takenMem: 23475, totalPhysMem: 64932 }), param1: 0, param2: 0, param3: 0, param4: 0, token: 0, remove: 0 },
    ];
    this.emit('performance-update', { broadcast: reports });
  }

  private stopTimer(name: string): void {
    const t = this.timers.get(name);
    if (t) clearInterval(t);
    this.timers.delete(name);
  }

  private stopTimers(): void {
    for (const name of [...this.timers.keys()]) this.stopTimer(name);
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
