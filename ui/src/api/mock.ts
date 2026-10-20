import { BackendEvents } from './emitter';
import type { MockFixtures } from './mockData';
import { UI_PRIORITY } from './rpcApi';
import {
  ConnectionError,
  RpcError,
  type Backend,
  type BackendConfig,
  type CallOptions,
  type CaptureHdr,
  type ConnectionStatus,
  type ControllerInfo,
  type JsonSchema,
  type PerformanceReport,
  type PriorityInfo,
  type RpcEvent,
  type RpcReply,
  type ServerInfo,
  type SystemGrabbers,
} from './types';
import { validate, type Issue } from '../schema/schema';
import { compileLayout, type DriverSettings, type LayoutSpec } from '../layout';

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
const COMPONENTS = ['ALL', 'SMOOTHING', 'BLACKBORDER', 'SYSTEMGRABBER', 'LEDDEVICE'];

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
  ['INFO', 'Overglow.Server.WebServer', 'Listening on http://127.0.0.1:18290'],
  ['INFO', 'Overglow.Devices.LedDeviceRunner', 'WLED controller wled-mock.local: 168 LEDs, 2 outputs'],
  ['INFO', 'Overglow.Engine.OutputStage', 'Smoothing: HybridRgbInterpolator, 150 ms, 50 Hz'],
  ['DEBUG', 'Overglow.Capture.Windows.DxgiScreenSource', 'Screen capture started: \\\\.\\DISPLAY5, 491x205 frames at up to 30 fps'],
  ['WARNING', 'Overglow.Devices.LedDeviceRunner', 'Slow answer from wled-mock.local (180 ms)'],
  ['INFO', 'Overglow.Engine.EffectEngine', 'Effect started: Rainbow swirl fast'],
  ['ERROR', 'Overglow.Mqtt.MqttConnection', 'Couldn’t reach the MQTT broker (demo message)'],
];

/**
 * In-memory stand-in for Overglow, built from fixtures captured from the .NET app (see __fixtures__): a two-output
 * WLED controller with the product owner's 168-LED layout. Implements the same Backend interface as WsClient,
 * with the app's behavior: auth errors, partial setconfig that rejects invalid values with per-field `errors`,
 * stored adjustments, restore while running, live LED colors, logs, performance reports and the screen capture's
 * HDR state, so the whole UI runs without a server (`?mock` / VITE_MOCK=1).
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
  private components = new Map(COMPONENTS.map((c) => [c, c !== 'BLACKBORDER']));
  private source: Source | null = null;
  private autoSelect = true;
  private tokens: Array<{ id: string; comment: string; last_use: string }> = [
    { id: 'a1b2c', comment: 'Stream Deck plugin', last_use: '2026-10-12T19:41:00' },
  ];
  private timers = new Map<string, ReturnType<typeof setInterval>>();
  private logSeq = 0;
  /** Log of LEDs blinked (controller id, or none for an index over all LEDs), for tests / debugging. */
  readonly blinks: Array<{ index: number; controller?: string }> = [];

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
    const fail = (error: string, cmd = full, errors?: Issue[]): never => {
      throw new RpcError(error, cmd, { command: cmd, success: false, error, ...(errors?.length ? { errors } : {}) });
    };
    const reject = (errors: Issue[]): never => fail(`Invalid settings: ${errors.map((e) => `${e.path}: ${e.message}`).join('; ')}`, full, errors);
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
          const sent = clone((params.config ?? {}) as BackendConfig);
          const errors = this.checkConfig(sent);
          if (errors.length) return reject(errors);
          this.writeConfig(sent);
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
        if (component === 'SYSTEMGRABBER') this.emit('capture-update', this.systemGrabbers());
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
        // Like the backend: applied right away and stored in color.channelAdjustment (admin only).
        needAdmin();
        const color = this.config.color as { channelAdjustment: Array<Record<string, unknown>> };
        const next = { ...color, channelAdjustment: [{ ...this.activeAdjustment(), ...(params.adjustment as object) }, ...color.channelAdjustment.slice(1)] };
        const errors = this.checkConfig({ color: next });
        if (errors.length) return reject(errors);
        this.writeConfig({ color: next });
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
      case 'load-db': {
        // Like the backend: applied while running; the reply lists what was adapted to this version.
        needAdmin();
        const rows = (params.config as { settings?: Array<{ type?: string; config?: string; instance?: number }> } | undefined)?.settings;
        if (!Array.isArray(rows)) return fail('The backup has no settings list.');
        const restored: BackendConfig = {};
        for (const row of rows) {
          if (typeof row.type !== 'string' || typeof row.config !== 'string' || (row.type === 'device' && row.instance)) continue;
          restored[row.type] = JSON.parse(row.config) as unknown;
        }
        const errors = this.checkConfig(restored);
        if (errors.length) return reject(errors);
        this.writeConfig(restored);
        return ok({ changes: [] });
      }
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
          const p = (params.params ?? {}) as { blinkIndex?: number; controller?: string };
          const index = Number(p.blinkIndex);
          const controller = this.controllers().find((c) => (p.controller === undefined ? index >= c.start && index < c.start + c.ledCount : c.id === p.controller));
          const local = p.controller === undefined && controller ? index - controller.start : index;
          if (!controller || local < 0 || local >= controller.ledCount) return fail(`There is no LED ${index}${p.controller ? ` on controller "${p.controller}"` : ''}`);
          this.blinks.push(p.controller === undefined ? { index } : { index, controller: p.controller });
          console.info(`[mock] blink LED ${local} of ${controller.name}`);
          return { controller: controller.id, blinkIndex: local };
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

  /**
   * Like the backend: a saved layout is the source of truth. Each controller keeps its driver settings (by id, else
   * the legacy `device` for the first one), `leds` becomes every controller's LEDs and `device` the first one's.
   */
  private applyLayout(next: BackendConfig): BackendConfig {
    const spec = next.layout as LayoutSpec | undefined;
    if (!spec || !Array.isArray(spec.controllers) || spec.controllers.length === 0) return next;
    const previous = (this.config.layout as LayoutSpec | undefined)?.controllers ?? [];
    const controllers = spec.controllers.map((c, i) => ({
      ...c,
      device: c.device ?? previous.find((p) => p.id === c.id)?.device ?? (i === 0 && !this.config.layout ? (this.config.device as DriverSettings) : { type: 'file', output: '/dev/null' }),
    }));
    const layout = { ...spec, controllers };
    return { ...next, layout, leds: compileLayout(layout).flatMap((c) => c.leds), device: controllers[0].device };
  }

  /** Like the backend's strict validation: every invalid value, with the section in its path (unknown sections too). */
  private checkConfig(sent: BackendConfig): Issue[] {
    const schema = this.fx!.schema;
    return Object.entries(sent).flatMap(([section, value]) => {
      const s = schema.properties[section] as JsonSchema | undefined;
      if (!s || section === 'alldevices') return [{ path: section, message: 'unknown settings section' }];
      return section === 'layout' || section === 'leds' ? [] : validate(s, value, section);
    });
  }

  /** Partial setconfig of validated sections: replace the sent sections, announce the changed ones. */
  private writeConfig(sent: BackendConfig): void {
    const next = this.applyLayout(sent);
    for (const [section, value] of Object.entries(next)) {
      const changed = JSON.stringify(value) !== JSON.stringify(this.config[section]);
      this.config[section] = value;
      if (!changed) continue;
      this.emit('settings-update', { [section]: value });
      if (section === 'leds') this.emit('leds-update', { leds: value });
      if (section === 'color') this.emit('adjustment-update', (value as { channelAdjustment: unknown }).channelAdjustment);
      if (section === 'systemGrabber') this.emit('capture-update', this.systemGrabbers());
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
      controllers: this.controllers(),
      systemGrabbers: this.systemGrabbers(),
    };
  }

  /** `systemGrabbers`: the demo screen is an SDR monitor, so only tone mapping forced on applies. */
  private systemGrabbers(): SystemGrabbers {
    const mode = String((this.config.systemGrabber as { hdrToneMapping?: string } | undefined)?.hdrToneMapping ?? 'auto') as CaptureHdr['mode'];
    return {
      ...clone(this.fx!.serverInfo.systemGrabbers),
      hdr: { mode, capturing: true, monitorHdr: false, toneMapping: mode === 'on' },
    };
  }

  /** `serverinfo.controllers`: the layout's controllers, else the one legacy controller. */
  private controllers(): ControllerInfo[] {
    const spec = this.config.layout as LayoutSpec | undefined;
    const running = !!this.components.get('ALL') && !!this.components.get('LEDDEVICE');
    const list = spec?.controllers?.length
      ? spec.controllers.map((c) => ({
          id: c.id,
          name: c.name,
          type: String(c.device?.type ?? c.kind),
          enabled: c.enabled !== false,
          ledCount: c.outputs.reduce((n, o) => Math.max(n, o.start + o.length), 0),
          strips: spec.strips
            .filter((s) => s.controllerId === c.id)
            .map((s) => {
              const o = c.outputs.find((x) => x.index === s.output);
              return { id: s.id, name: s.name, start: o?.start ?? 0, length: o?.length ?? 0, brightness: s.brightness ?? 1 };
            }),
        }))
      : [{ id: 'controller-1', name: 'LED controller', type: String((this.config.device as { type?: string })?.type ?? ''), enabled: true, ledCount: ((this.config.leds as unknown[]) ?? []).length, strips: [] }];
    let start = 0;
    return list.map((c) => {
      const info: ControllerInfo = { ...c, running: c.enabled, state: running && c.enabled ? 'ready' : 'off', status: null, start };
      start += c.ledCount;
      return info;
    });
  }

  private activeAdjustment(): Record<string, unknown> {
    return (this.config.color as { channelAdjustment: Array<Record<string, unknown>> }).channelAdjustment[0];
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
    const leds: PerformanceReport[] = this.controllers().map((c, i) => ({
      type: 3,
      id: i,
      name: c.type,
      param1: c.running ? 49.6 + jitter() : 0,
      param2: 2980,
      param3: 1790,
      param4: 0,
      token: 1,
      remove: 0,
      controller: c.id,
      controllerName: c.name,
      ledCount: c.ledCount,
      latencyMs: 0.4 + jitter() / 10,
    }));
    const reports: PerformanceReport[] = [
      { type: 2, id: 0, name: 'First LED instance', param1: 29.8 + jitter(), param2: 0, param3: 0, param4: 0, token: 1, remove: 0 },
      ...leds,
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
