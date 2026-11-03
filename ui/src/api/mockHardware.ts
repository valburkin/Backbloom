import type { ColorOrder, FoundBoard, HardwareProgressStep, HueBridgeState, ScreenInfo, UsbBoard, WifiNetwork, WledOutput, WledOutputChange, WledState } from './types';

/** The color order the backend starts a strip type with (docs/guided-setup.md, the preset table). */
const PRESET_ORDER: Record<string, ColorOrder> = { 'ws2811-12v': 'BRG', 'ws2814-rgbw': 'RGB', apa102: 'BGR' };

// The mock backend's hardware (docs/guided-setup.md): WLED boards to find and set up, and room lights to find and pair.
// Boards: a new one on USB (COM5), a new one that only opens its hotspot (WLED-AP, seen by this PC's Wi-Fi), and the
// owner's board on the home network (two strips, 91 and 77 LEDs, which Backbloom streams to in the demo). New boards
// join Wi-Fi over Improv or from their hotspot, restart after settings that need it, and light test patterns.
// Failures to try out:
// - Wi-Fi password "wrongpass" (or shorter than 8 characters): "couldn’t join <ssid>: check the password"
// - the network "Garage": the board can't join it (too far away)
// - an address with "fail" or "offline" in it doesn't answer; while a board restarts it doesn't answer either
// - Hue: the first Connect fails (the button wasn't pressed), the next one pairs
// - Home Assistant: the token "wrong" is refused
// - `usb: false` (`?mock=nousb`), `hotspot: false`, `locationBlocked` (`?mock=location`): what Find then finds

export interface MockHardwareOptions {
  /** A new board is plugged in over USB (default true). */
  usb?: boolean;
  /** A new board opens its hotspot nearby (default true). */
  hotspot?: boolean;
  /** The owner's board (two strips) is on the home network (default true). */
  existing?: boolean;
  /** Where the owner's board answers (default MOCK_EXISTING_HOST, the demo layout's). */
  existingHost?: string;
  /** Windows keeps nearby Wi-Fi networks from Backbloom (location access off): hotspots aren't seen. */
  locationBlocked?: boolean;
  /** How long a board takes to restart, ms (default 4000). */
  restartMs?: number;
  /** How long each step of joining from a hotspot takes, ms (default 900). */
  stepMs?: number;
  /** How long a board takes to apply LED settings that need no restart, ms (default 700): `wled-write` waits for it. */
  applyMs?: number;
  /** Time source (tests). */
  now?: () => number;
  /** Pushes an event (`hardware-progress-update`). */
  emit?: (command: string, data: unknown) => void;
  /** The device of the layout that streams to a host right now, if any (such boards refuse writes and tests). */
  streamedBy?: (host: string) => string | null;
}

/** Where the new USB board ends up on Wi-Fi. */
export const MOCK_BOARD_HOST = '192.168.1.77';
export const MOCK_BOARD_PORT = 'COM5';
/** Where the new hotspot board ends up on Wi-Fi. */
export const MOCK_HOTSPOT_HOST = '192.168.1.78';
/** The owner's board on the network. */
export const MOCK_EXISTING_HOST = 'wled-mock.local';

const NETWORKS: WifiNetwork[] = [
  { ssid: 'Home Wi-Fi', signal: 92, secure: true },
  { ssid: 'Home Wi-Fi 5G', signal: 71, secure: true },
  { ssid: 'Neighbors', signal: 38, secure: true },
  { ssid: 'Garage', signal: 12, secure: false },
];

const HUE_HOST = '192.168.1.20';
const NANOLEAF_HOST = '192.168.1.31:16021';
const HA_HOST = '192.168.1.30:8123';

function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v)) as T;
}

/** A board as WLED starts it: one output of 30 LEDs. */
function freshWled(mac: string): WledState {
  return {
    name: 'WLED',
    version: '0.15.0',
    chip: 'ESP32',
    mac,
    ledCount: 30,
    outputs: [{ index: 0, strip: 'ws2812b', pin: 16, length: 30, start: 0, colorOrder: 'GRB', reverse: false }],
    maxCurrent: 850,
    ledCurrent: 55,
    wifi: { ssid: '' },
  };
}

interface MockBoard {
  id: string;
  /** How it is found before it has Wi-Fi. */
  route: 'usb' | 'hotspot' | 'network';
  port?: string;
  hotspot?: string;
  /** Its address once on Wi-Fi. */
  host: string;
  onWifi: boolean;
  state: UsbBoard['state'];
  restartUntil: number;
  wled: WledState;
  /** The strips really wired to it (the pins they're on): what lights up while Find your strips probes. */
  wired: Array<{ pin: number; leds: number }>;
  /** The outputs from before a probe, put back by `wled-probe-end keep: false`. */
  beforeProbe?: WledOutput[];
}

/** The likely pins Find your strips tries on an ESP32: round 1 the common ones, round 2 the others. */
export const PROBE_PINS: Record<1 | 2, readonly number[]> = { 1: [16, 2, 17, 4, 5, 18], 2: [19, 21, 22, 23, 25, 26, 27, 32, 33, 13, 14] };

/** LEDs each probed connection gets (the backend: 60–300, so a strip's later LEDs go black too), and how many light up. */
export const PROBE_LEDS = 300;
export const PROBE_LIT = 20;

/** Outputs `next` leave LEDs of `had` behind: one is shorter, or fewer are left. */
function shorter(had: readonly WledOutput[], next: readonly WledOutput[]): boolean {
  return next.length < had.length || next.some((o, i) => had[i] !== undefined && o.length < had[i].length);
}

export class MockHardware {
  private readonly restartMs: number;
  private readonly stepMs: number;
  private readonly applyMs: number;
  private readonly now: () => number;
  private readonly emit: (command: string, data: unknown) => void;
  private readonly streamedBy: (host: string) => string | null;
  private readonly locationBlocked: boolean;
  private readonly boards: MockBoard[] = [];
  private huePairTries = 0;
  private hue: HueBridgeState;
  /** Test patterns lit (newest last) and settings written, for tests and the console. */
  readonly tests: Array<Record<string, unknown>> = [];
  readonly writes: Array<Record<string, unknown>> = [];
  readonly identified: Array<{ type: string; params: Record<string, unknown> }> = [];

  constructor(options: MockHardwareOptions = {}) {
    this.restartMs = options.restartMs ?? 4000;
    this.stepMs = options.stepMs ?? 900;
    this.applyMs = options.applyMs ?? 700;
    this.now = options.now ?? (() => Date.now());
    this.emit = options.emit ?? (() => undefined);
    this.streamedBy = options.streamedBy ?? (() => null);
    this.locationBlocked = options.locationBlocked ?? false;
    if (options.usb ?? true)
      // the owner's case: a strip of 91 LEDs on GPIO 16, and a 720 LEDs/m COB strip (77 segments) on GPIO 2
      this.boards.push({
        id: 'usb:COM5',
        route: 'usb',
        port: MOCK_BOARD_PORT,
        host: MOCK_BOARD_HOST,
        onWifi: false,
        state: 'ready',
        restartUntil: 0,
        wled: freshWled('a0b765c3d2e1'),
        wired: [
          { pin: 16, leds: 91 },
          { pin: 2, leds: 77 },
        ],
      });
    if (options.hotspot ?? true)
      this.boards.push({
        id: 'hotspot:WLED-AP',
        route: 'hotspot',
        hotspot: 'WLED-AP',
        host: MOCK_HOTSPOT_HOST,
        onWifi: false,
        state: 'ready',
        restartUntil: 0,
        wled: freshWled('c8f09e4b7a10'),
        wired: [{ pin: 16, leds: 60 }],
      });
    if (options.existing ?? true)
      this.boards.push({
        id: `network:${options.existingHost ?? MOCK_EXISTING_HOST}`,
        route: 'network',
        host: options.existingHost ?? MOCK_EXISTING_HOST,
        onWifi: true,
        state: 'provisioned',
        restartUntil: 0,
        wired: [
          { pin: 16, leds: 91 },
          { pin: 17, leds: 77 },
        ],
        wled: {
          ...freshWled('3c71bf9a0b2e'),
          name: 'Desk',
          ledCount: 168,
          outputs: [
            { index: 0, strip: 'ws2812b', pin: 16, length: 91, start: 0, colorOrder: 'GRB', reverse: false },
            { index: 1, strip: 'ws2812b', pin: 17, length: 77, start: 91, colorOrder: 'GRB', reverse: false },
          ],
          maxCurrent: 8500,
          wifi: { ssid: 'Home Wi-Fi' },
        },
      });
    // API v2: four lights (the hallway one can't stream), their entertainment services, one area of two
    const light = (n: number, name: string) => ({ id: `light-${n}`, id_v1: `/lights/${n}`, metadata: { name } });
    const service = (n: number, renderer = true) => ({ id: `ent-${n}`, renderer, renderer_reference: { rid: `light-${n}`, rtype: 'light' } });
    this.hue = {
      lights: [light(1, 'TV left'), light(2, 'Hallway'), light(3, 'TV right'), light(4, 'Play bar')],
      services: [service(1), service(2, false), service(3), service(4)],
      configurations: [
        {
          id: '9f2c5d1e-0000-4000-8000-000000000001',
          metadata: { name: 'TV area' },
          channels: [3, 1].map((n, i) => ({ channel_id: i, members: [{ service: { rid: `ent-${n}`, rtype: 'entertainment' } }] })),
        },
      ],
    };
  }

  private get usbBoard(): MockBoard | undefined {
    return this.boards.find((b) => b.route === 'usb');
  }

  /** The board answering at `host`: on Wi-Fi and not restarting (4.3.2.1: a hotspot board without Wi-Fi). */
  private at(host: string): MockBoard | undefined {
    const b = host === '4.3.2.1' ? this.boards.find((x) => x.route === 'hotspot' && !x.onWifi) : this.boards.find((x) => x.onWifi && x.host === host);
    return b && this.now() >= b.restartUntil ? b : undefined;
  }

  private restart(b: MockBoard): void {
    b.restartUntil = this.now() + this.restartMs;
  }

  /** WLED boards a network search finds: those on Wi-Fi (the owner's is listed by the demo itself). */
  wledOnNetwork(): Array<{ value: string; name: string }> {
    return this.boards
      .filter((b) => b.onWifi && b.route !== 'network')
      .map((b) => ({ value: b.host, name: `${b.host} (wled-${b.wled.mac.slice(-6)}.local)` }));
  }

  private joinCheck(ssid: string, password: string, fail: (e: string) => never): void {
    const net = NETWORKS.find((n) => n.ssid === ssid);
    if (!ssid) fail('Enter the name of your Wi-Fi');
    if (ssid === 'Garage') fail(`couldn’t join ${ssid}: the board is too far from it`);
    if ((net?.secure ?? true) && (password.length < 8 || password === 'wrongpass')) fail(`couldn’t join ${ssid}: check the password`);
  }

  private sleep(ms: number): Promise<void> {
    return ms > 0 ? new Promise((r) => setTimeout(r, ms)) : Promise.resolve();
  }

  /** `hardware <subcommand>`. */
  async handle(subcommand: string | undefined, params: Record<string, unknown>, fail: (e: string) => never): Promise<unknown> {
    const host = String(params.host ?? '');
    const needBoard = (): MockBoard => {
      const b = /fail|offline/i.test(host) ? undefined : this.at(host);
      if (!b) return fail(`Couldn’t reach ${host}: check that the board is on and on the same network as this PC.`);
      return b;
    };
    const notStreamed = () => {
      const device = this.streamedBy(host);
      if (device) fail(`Backbloom is sending colors to this board (the device “${device}”): turn that device off first`);
    };
    switch (subcommand) {
      case 'screens': {
        const screens: ScreenInfo[] = [
          { name: 'DELL U2723QE', primary: true, widthMm: 597, heightMm: 336 },
          { name: 'Generic PnP Monitor', primary: false, widthMm: null, heightMm: null },
        ];
        return { screens };
      }
      case 'find': {
        await this.sleep(this.stepMs);
        const found: FoundBoard[] = [];
        for (const b of this.boards) {
          // like the backend: setUp is a guess (a name or LED count other than WLED's), isNew the opposite; a board on USB
          // that is on Wi-Fi is one network entry with its port; Improv doesn't tell a MAC
          const setUp = b.wled.name !== 'WLED' || b.wled.ledCount !== 30;
          if (b.onWifi)
            found.push({
              id: `network:${b.wled.mac.toUpperCase()}`,
              via: 'network',
              name: b.wled.name,
              isNew: !setUp,
              host: b.host,
              ...(b.route === 'usb' ? { port: b.port } : {}),
              mac: b.wled.mac,
              version: b.wled.version,
              ledCount: b.wled.ledCount,
              mdnsName: `wled-${b.wled.mac.slice(-6)}`,
              setUp,
            });
          else if (b.route === 'usb')
            found.push({
              id: b.id,
              via: 'usb',
              name: 'WLED',
              isNew: true,
              port: b.port,
              portName: `Silicon Labs CP210x (${b.port})`,
              chip: b.wled.chip,
              firmware: 'WLED',
              version: b.wled.version,
            });
          else if (b.route === 'hotspot' && !this.locationBlocked)
            found.push({ id: b.id, via: 'hotspot', name: 'WLED', isNew: true, hotspot: b.hotspot, mac: b.wled.mac, signal: 88 });
        }
        return {
          boards: found,
          wifi: {
            adapter: true,
            currentSsid: this.locationBlocked ? null : 'Home Wi-Fi',
            blocked: this.locationBlocked ? 'location' : null,
            // Windows has the home Wi-Fi's password saved
            savedPassword: !this.locationBlocked,
          },
          usbSilent: [],
        };
      }
      case 'connect-wifi': {
        const b = this.boards.find((x) => x.id === params.id && !x.onWifi);
        if (!b) return fail('Search again: that board isn’t there any more');
        const ssid = String(params.ssid ?? '').trim();
        // the password saved on this PC (the UI never sees it) is the right one
        const password = params.useSavedPassword === true ? 'saved-on-this-pc' : String(params.password ?? '');
        const step = (s: HardwareProgressStep) => this.emit('hardware-progress-update', { tan: 0, id: b.id, step: s });
        if (b.route === 'hotspot') {
          step('joining-hotspot');
          await this.sleep(this.stepMs);
          step('sending');
          await this.sleep(this.stepMs);
          // a wrong password shows when the board can't join: this PC is back on its Wi-Fi by then
          // the board can't join: this PC is back on its Wi-Fi by then, and the backend says so like this
          try {
            this.joinCheck(ssid, password, fail);
          } catch {
            return fail(`The board didn’t show up on ${ssid}: check the Wi-Fi password, and that ${ssid} is a 2.4 GHz network (WLED boards can’t join 5 GHz)`);
          }
          step('rejoining');
          await this.sleep(this.stepMs);
          step('waiting');
          await this.sleep(this.stepMs);
        } else this.joinCheck(ssid, password, fail);
        b.onWifi = true;
        b.state = 'provisioned';
        b.wled = { ...b.wled, wifi: { ssid } };
        return { host: b.host, address: `http://${b.host}`, name: b.wled.name, mac: b.wled.mac };
      }
      case 'wled-identify': {
        needBoard();
        this.identified.push({ type: 'wled', params: { host } });
        return {};
      }
      case 'wled-probe-start': {
        const b = needBoard();
        notStreamed();
        const round = params.round === 2 ? 2 : 1;
        // like the backend: temporary outputs on the likely pins, each long enough for a strip (the first 20 LEDs lit
        // white and dim, the rest black); the board restarts
        b.beforeProbe ??= clone(b.wled.outputs);
        const pins = PROBE_PINS[round];
        b.wled = {
          ...b.wled,
          outputs: pins.map((pin, i) => ({ index: i, strip: 'ws2812b', pin, length: PROBE_LEDS, start: i * PROBE_LEDS, colorOrder: 'GRB', reverse: false })),
          ledCount: pins.length * PROBE_LEDS,
        };
        await this.sleep(this.stepMs * 2);
        this.tests.push({ pattern: 'probe', round });
        return { candidates: pins.map((pin, output) => ({ output, pin })) };
      }
      case 'wled-probe-end': {
        const b = needBoard();
        // like the backend: keep only forgets the settings from before (the UI's write cleared the search's lights);
        // putting them back is a write, and a shorter output turns every LED black first
        if (params.keep !== true && b.beforeProbe) {
          if (shorter(b.wled.outputs, b.beforeProbe)) this.tests.push({ pattern: 'black' });
          b.wled = { ...b.wled, outputs: b.beforeProbe, ledCount: b.beforeProbe.reduce((n, o) => Math.max(n, o.start + o.length), 0) };
        }
        b.beforeProbe = undefined;
        return {};
      }
      case 'usb-scan': {
        const b = this.usbBoard;
        if (!b) return { boards: [], silent: [] };
        const board: UsbBoard = {
          port: MOCK_BOARD_PORT,
          name: 'USB-Enhanced-SERIAL CH9102 (COM5)',
          state: b.state,
          firmware: 'WLED',
          version: b.wled.version,
          chip: b.wled.chip,
          deviceName: b.wled.name,
          address: b.onWifi ? `http://${b.host}` : null,
        };
        return { boards: [board], silent: [] };
      }
      case 'usb-networks':
        if (!this.usbBoard || params.port !== MOCK_BOARD_PORT) return fail(`No board answers on ${String(params.port)}.`);
        return { networks: clone(NETWORKS) };
      case 'usb-wifi': {
        const b = this.usbBoard;
        if (!b || params.port !== MOCK_BOARD_PORT) return fail(`No board answers on ${String(params.port)}.`);
        const ssid = String(params.ssid ?? '');
        this.joinCheck(ssid, String(params.password ?? ''), fail);
        b.onWifi = true;
        b.state = 'provisioned';
        b.wled = { ...b.wled, wifi: { ssid } };
        return { address: `http://${b.host}`, host: b.host };
      }
      case 'wled-read': {
        const b = needBoard();
        return { ...clone(b.wled), address: `http://${b.host}` };
      }
      case 'wled-write': {
        const b = needBoard();
        notStreamed();
        const { host: _h, ...change } = params;
        this.writes.push(clone(change));
        const w = b.wled;
        // like the backend: each output from what the board has for it; a new one needs a pin
        let next = 0;
        const sent = Array.isArray(change.outputs) ? (change.outputs as WledOutputChange[]) : null;
        if (sent?.some((o, i) => o.pin === undefined && !w.outputs[i])) return fail('A new output needs a pin');
        if (sent?.some((o) => o.strip === 'apa102' && o.clockPin === undefined && !w.outputs.some((x) => x.clockPin !== undefined)))
          return fail('Two-wire strips need a clock pin');
        const outputs: WledOutput[] = sent
          ? sent.map((o, i) => {
              const had = w.outputs[i];
              const start = o.start ?? next;
              next = start + o.length;
              return {
                index: i,
                strip: o.strip,
                pin: o.pin ?? had.pin,
                ...(o.clockPin !== undefined ? { clockPin: o.clockPin } : {}),
                length: o.length,
                start,
                colorOrder: o.colorOrder ?? (had && had.strip === o.strip ? had.colorOrder : (PRESET_ORDER[o.strip] ?? 'GRB')),
                reverse: o.reverse ?? false,
              };
            })
          : w.outputs;
        // like WLED: other pins or another LED type need a restart; counts, order and the limit apply at once
        // like the backend: an output that gets shorter or goes turns every LED black first (the LEDs past its new end
        // would keep their colors)
        if (shorter(w.outputs, outputs)) this.tests.push({ pattern: 'black' });
        const restart =
          !!change.wifi ||
          outputs.length !== w.outputs.length ||
          outputs.some((o, i) => o.pin !== w.outputs[i]?.pin || o.strip !== w.outputs[i]?.strip);
        b.wled = {
          ...w,
          ...(typeof change.name === 'string' ? { name: change.name } : {}),
          outputs: clone(outputs),
          ledCount: outputs.reduce((n, o) => Math.max(n, o.start + o.length), 0),
          ...(typeof change.maxCurrent === 'number' ? { maxCurrent: change.maxCurrent } : {}),
          ...(typeof change.ledCurrent === 'number' ? { ledCurrent: change.ledCurrent } : {}),
        };
        if (restart) this.restart(b);
        // like the backend: without a restart it answers once the board rebuilt its outputs (a test lit before that
        // would be wiped)
        else await this.sleep(this.applyMs);
        return { restarting: restart };
      }
      case 'wled-wifi': {
        const b = host === '4.3.2.1' ? this.at(host) : needBoard();
        if (!b) return fail('Couldn’t reach 4.3.2.1: connect this PC to the WLED-AP network first.');
        const ssid = String(params.ssid ?? '');
        if (!ssid) return fail('ssid is required');
        b.onWifi = true;
        b.state = 'provisioned';
        b.wled = { ...b.wled, wifi: { ssid } };
        this.restart(b);
        return { restarting: true };
      }
      case 'wled-test': {
        const b = needBoard();
        notStreamed();
        const { host: _h, ...test } = params;
        this.tests.push(clone(test));
        const o = b.wled.outputs.find((x) => x.index === Number(test.output));
        if (!o) return fail(`The board has no output ${Number(test.output) + 1}.`);
        // like the backend: only LEDs the output has
        if (test.pattern === 'range' && !(Number(test.count) >= 1 && Number(test.count) <= o.length)) return fail(`count must be 1 to ${o.length} (the output's LEDs)`);
        if (test.pattern === 'points') {
          const leds = Array.isArray(test.leds) ? test.leds.map(Number) : [];
          if (leds.length < 1 || leds.length > 64) return fail('leds must list 1 to 64 LEDs');
          if (leds.some((n) => !Number.isInteger(n) || n < 1 || n > o.length)) return fail(`leds must be 1 to ${o.length} (the output's LEDs)`);
        }
        return {};
      }
      default:
        return fail('unknown or missing subcommand');
    }
  }

  /** The strips really wired to the board at `host` (tests: what a user would see light up). */
  wiredAt(host: string): Array<{ pin: number; leds: number }> {
    return this.boards.find((b) => b.host === host)?.wired ?? [];
  }

  /** The current color order of the USB board's first output (tests). */
  get colorOrder(): ColorOrder {
    return (this.usbBoard ?? this.boards[0]).wled.outputs[0].colorOrder;
  }

  /** `leddevice discover` for the room-light types (undefined for the others). */
  discover(type: string): Array<Record<string, unknown>> | undefined {
    switch (type) {
      case 'philipshue':
        return [{ ip: HUE_HOST, port: 80, value: HUE_HOST, name: `Hue bridge ${HUE_HOST} (ecb5fa1a2b3c)` }];
      case 'nanoleaf':
        return [{ ip: '192.168.1.31', port: 16021, value: NANOLEAF_HOST, name: 'Nanoleaf 192.168.1.31' }];
      case 'lifx':
        return [
          { name: 'Lamp #0', ipAddress: '192.168.1.41', macAddress: 'D0:73:D5:11:22:33', value: '192.168.1.41' },
          { name: 'Lamp #1', ipAddress: '192.168.1.42', macAddress: 'D0:73:D5:44:55:66', value: '192.168.1.42' },
        ];
      case 'wiz':
        return [{ name: 'Bulb #0', ipAddress: '192.168.1.45', macAddress: 'A8:BB:50:01:02:03', value: '192.168.1.45' }];
      case 'yeelight':
        return [{ ip: '192.168.1.48', port: 55443, hostname: '192.168.1.48', value: '192.168.1.48:55443', name: 'Yeelight 192.168.1.48' }];
      case 'govee':
        return [{ name: 'Govee H6199', ipAddress: '192.168.1.52', sku: 'H6199', deviceId: '1F:80:C5:32:32:36:72:4E', value: '192.168.1.52' }];
      case 'home_assistant':
        return [{ value: HA_HOST, name: `${HA_HOST} (homeassistant)` }];
      default:
        return undefined;
    }
  }

  /** `leddevice getProperties` for the room-light types (undefined for the others). */
  properties(type: string, p: Record<string, unknown>): Record<string, unknown> | undefined {
    switch (type) {
      case 'philipshue': {
        if (p.host !== HUE_HOST) return { error: `Couldn’t reach the bridge at ${String(p.host)}` };
        if (p.pair === true) {
          // the first try comes before the button was pressed
          if (this.huePairTries++ === 0) return { error: 'link button not pressed' };
          return { username: 'mock-hue-user-0123456789', clientkey: '0123456789ABCDEF0123456789ABCDEF' };
        }
        if (p.user !== 'mock-hue-user-0123456789') return { error: 'unauthorized user' };
        const area = p.createArea as { name?: unknown; lights?: unknown } | undefined;
        if (area) {
          const ids = Array.isArray(area.lights) ? area.lights.map(String) : [];
          if (!ids.length) return { error: 'createArea.lights needs at least one light' };
          const services: string[] = [];
          for (const id of ids) {
            // a v2 light id, or a v1 light number (as the backend takes them)
            const l = this.hue.lights.find((x) => x.id === id || x.id_v1 === `/lights/${id}`);
            const svc = this.hue.services.find((x) => x.renderer && x.renderer_reference?.rid === l?.id);
            if (!svc) return { error: `The bridge has no entertainment light "${id}" (only lights that support Hue Entertainment can be in an area)` };
            services.push(svc.id);
          }
          const id = `9f2c5d1e-0000-4000-8000-${String(this.hue.configurations.length + 1).padStart(12, '0')}`;
          this.hue.configurations.push({
            id,
            metadata: { name: String(area.name ?? 'Backbloom') },
            channels: services.map((rid, i) => ({ channel_id: i, members: [{ service: { rid, rtype: 'entertainment' } }] })),
          });
          return { id };
        }
        const filter = String(p.filter ?? '');
        const v2 = (data: unknown[]) => ({ errors: [], data: clone(data) });
        if (filter === '/clip/v2/resource/entertainment_configuration') return v2(this.hue.configurations);
        if (filter === '/clip/v2/resource/light') return v2(this.hue.lights);
        if (filter === '/clip/v2/resource/entertainment') return v2(this.hue.services);
        return {};
      }
      case 'nanoleaf':
        if (p.pair === true) return { token: 'mock-nanoleaf-token' };
        if (p.token !== 'mock-nanoleaf-token') return { error: 'unauthorized' };
        return { name: 'Shapes 5A1B', model: 'NL42', panelLayout: { layout: { numPanels: 9 } } };
      case 'home_assistant':
        if (p.token === 'wrong' || !p.token) return { error: 'Home Assistant refused the token: create a new one and paste it again' };
        return {
          lights: [
            { id: 'light.desk_lamp', name: 'Desk lamp' },
            { id: 'light.floor_lamp', name: 'Floor lamp' },
            { id: 'light.shelf', name: 'Shelf' },
          ],
        };
      default:
        return undefined;
    }
  }

  /** `leddevice identify` for a room light: remembered (nothing lights in the mock). */
  identify(type: string, params: Record<string, unknown>): void {
    this.identified.push({ type, params: clone(params) });
    // only where it is: the parameters can hold tokens and user names
    console.info(`[mock] ${type} light signals itself (${String(params.host ?? params.ipAddress ?? params.hostname ?? '')})`);
  }
}
