// The "New WLED strip" wizard's logic (docs/guided-setup.md): pure helpers, tested without a board.
import type { ColorOrder, WledChange, WledOutput, WledOutputChange, WledState } from '../api/types';
import type { Controller, PathPoint, Room } from '../layout';
import { shapePath, slugifyHost } from '../layout';
import { unpinned } from '../editor/strips';
import { acceptsVolts, currentLimitMa, presetById, type StripPlan, type Volts } from './plan';
import { withRole } from './roles';

/** The host part of an address a board reports ("http://192.168.1.50/" → "192.168.1.50"). */
export function hostOf(address: string): string {
  return address.trim().replace(/^https?:\/\//i, '').replace(/\/.*$/, '');
}


/** A color the user saw on the strip in the color check. */
export type SeenColor = 'red' | 'green' | 'blue' | 'mixed' | 'none';

const LETTER: Record<'red' | 'green' | 'blue', 'R' | 'G' | 'B'> = { red: 'R', green: 'G', blue: 'B' };

/**
 * The strip's real color order from what it showed for red and then green, sent with the order `setting`. The board
 * puts red where `setting` has R; the strip shows the color its own order has at that place. Null when the answers
 * can't come from a color order (the same color twice, mixed colors, nothing lit).
 */
export function colorOrderFromAnswers(setting: ColorOrder, red: SeenColor, green: SeenColor): ColorOrder | null {
  if (red === 'mixed' || red === 'none' || green === 'mixed' || green === 'none' || red === green) return null;
  const order: Array<'R' | 'G' | 'B' | null> = [null, null, null];
  order[setting.indexOf('R')] = LETTER[red];
  order[setting.indexOf('G')] = LETTER[green];
  const rest = (['R', 'G', 'B'] as const).find((c) => !order.includes(c))!;
  order[setting.indexOf('B')] = rest;
  return order.join('') as ColorOrder;
}

/** The supply's current in A from what is printed on it: amps, or watts at its volts. */
export function supplyAmps(volts: number, value: number, unit: 'A' | 'W'): number {
  if (!(value > 0) || !(volts > 0)) return 0;
  return unit === 'A' ? value : Math.round((value / volts) * 10) / 10;
}

/**
 * What shows that a board is set up already (a name of its own, or outputs other than WLED's first-start one of 30
 * LEDs), in words for the confirmation ("“Desk”, 2 outputs, 168 LEDs"); null for a board as WLED starts it.
 */
export function setUpAlready(b: WledState): string | null {
  const name = b.name.trim();
  const named = name !== '' && name !== 'WLED';
  const leds = b.outputs.reduce((n, o) => n + o.length, 0);
  const configured = b.outputs.length > 1 || b.outputs.some((o) => o.length !== 30);
  if (!named && !configured) return null;
  const outputs = b.outputs.length === 1 ? '1 output' : `${b.outputs.length} outputs`;
  return [named ? `“${name}”` : null, outputs, `${leds} LEDs`].filter(Boolean).join(', ');
}

/**
 * GPIOs to suggest for LED outputs, per chip, best first: WLED's own default first (ESP32, S2, S3 16; ESP8266, C3 2;
 * then 2 on ESP32, a common choice and fine as an output), never the
 * serial console (ESP32 and ESP8266 1 and 3, S3 43 and 44, C3 20 and 21), the flash (ESP32 and ESP8266 6–11, S3
 * 26–37, C3 12–17), boot straps that break starting (ESP32 0 and 12, ESP8266 0 and 15) or input-only pins (ESP32 34–39).
 */
export const SAFE_PINS: Record<'esp32' | 'esp8266' | 'esp32-s3' | 'esp32-c3' | 'esp32-s2', readonly number[]> = {
  esp32: [16, 2, 17, 18, 19, 21, 22, 23, 25, 26, 27, 32, 33, 4, 5, 13, 14],
  esp8266: [2, 4, 5, 14, 12, 13],
  'esp32-s3': [16, 17, 18, 8, 9, 10, 11, 12, 13, 14, 21, 4, 5, 6, 7],
  'esp32-s2': [16, 17, 18, 33, 34, 35, 36, 37, 38, 39, 40, 4, 5, 6, 7],
  'esp32-c3': [2, 4, 5, 6, 7, 10, 3, 1, 0],
};

function pinsFor(chip: string | undefined): readonly number[] {
  const c = (chip ?? '').toLowerCase().replace(/\s+/g, '');
  if (c.includes('8266')) return SAFE_PINS.esp8266;
  if (c.includes('s3')) return SAFE_PINS['esp32-s3'];
  if (c.includes('s2')) return SAFE_PINS['esp32-s2'];
  if (c.includes('c3')) return SAFE_PINS['esp32-c3'];
  return SAFE_PINS.esp32;
}

/** The first safe pin for the chip that `used` doesn't hold (a free GPIO from the list's end when all are taken). */
export function safePin(chip: string | undefined, used: readonly number[]): number {
  const pins = pinsFor(chip);
  return pins.find((p) => !used.includes(p)) ?? pins[pins.length - 1];
}

/** Where a strip is: what the layout places it by (`stripPath`). */
export type StripPlace = 'screen' | 'ceiling' | 'desk' | 'wall' | 'other';

/** The places, with a name for the strip ("Monitor") and in words ("behind the monitor"), and how to mount a strip there. */
export const STRIP_PLACES: ReadonlyArray<{ value: StripPlace; label: string; name: string; words: string; tips: readonly string[] }> = [
  {
    value: 'screen',
    label: 'Behind the monitor or TV',
    name: 'Monitor',
    words: 'behind the monitor',
    tips: [
      'On the back of the screen, 3–5 cm in from its edges, so the light reaches the wall around it.',
      'Along 3 sides (left, top, right) for a screen on a stand, or all 4 for one on the wall.',
      'Clean the back with a little alcohol first: the strip’s tape holds better.',
      'Not cut yet? “Plan the cuts and power” works out the pieces for your screen.',
    ],
  },
  {
    value: 'ceiling',
    label: 'Ceiling',
    name: 'Ceiling',
    words: 'on the ceiling',
    tips: ['In a cove or behind a ledge, facing up, so you see the glow and not the LEDs.', 'Or along the edge where the wall meets the ceiling, facing the wall.'],
  },
  {
    value: 'desk',
    label: 'Desk',
    name: 'Desk',
    words: 'along the desk',
    tips: ['Along the back edge of the desk, facing the wall.', 'Under the edge works too: the light falls on the floor and the wall.'],
  },
  {
    value: 'wall',
    label: 'Wall or shelf',
    name: 'Wall',
    words: 'on the wall',
    tips: ['Behind the shelf or the panel, facing the wall, so only the glow shows.', 'Keep it a few centimeters from the wall for a softer light.'],
  },
  { value: 'other', label: 'Somewhere else', name: 'Strip', words: 'somewhere else', tips: ['Facing a wall or surface works best: you see the glow instead of the LEDs.'] },
];

/** A strip's places in words: "along the desk and behind the monitor". */
export function placesWords(places: readonly StripPlace[]): string {
  return places.map((p) => STRIP_PLACES.find((x) => x.value === p)?.words ?? p).join(' and ');
}

/** One strip on one output of the board. */
export interface BoardStrip {
  /**
   * Every place it passes (one strip may run along the desk, around the monitor and on along the desk), a set in
   * STRIP_PLACES' order; empty until the user says (no guess: every setup is different). Which way it runs comes from
   * tracing it.
   */
  places: StripPlace[];
  /** The strip type (a preset id); null until the user picks it (a required answer). */
  preset: string | null;
  /** LEDs per meter, when a product name told it (for counting the LEDs from the length). */
  density?: number | null;
  /** Null: not known yet (the last LED check finds it, starting from `DEFAULT_LEDS`). */
  ledCount: number | null;
  pin: number;
  /** The clock pin of two-wire strips (APA102); null: a safe free pin. */
  clockPin: number | null;
  /** Null: the strip type's usual order (the board picks it, the color check corrects it). */
  colorOrder: ColorOrder | null;
  /** The cut plan made for it ("Not cut yet? Plan the cuts and power"), placing it around the screen as planned. */
  plan: StripPlan | null;
}

/** What "What’s connected" writes to a board: its strips (one per output) and its power supply. */
export interface BoardSettings {
  /** The board's own name (WLED's default "WLED" is replaced by the strips' places). */
  name: string;
  /** The board's chip (`wled-read`): which pins are safe to suggest. */
  chip?: string;
  strips: BoardStrip[];
  /** What powers the board's strips; null until the user says (Save waits for it). */
  supply: SupplyChoice | null;
}

/** LEDs written for a strip whose count isn't known yet: the last LED check starts there and finds the real one. */
export const DEFAULT_LEDS = 60;

/**
 * The power supply (a required answer): as printed on it (volts and amps), or powered over USB, where the board keeps
 * WLED's own USB-safe limit (`USB_SAFE_MA`): a USB port or phone charger gives 0.5–1 A.
 */
export type SupplyChoice = { kind: 'printed'; volts: Volts; amps: number } | { kind: 'usb' };

/** WLED's own default limit, safe for a USB port: 850 mA. */
export const USB_SAFE_MA = 850;

/** The most common supplies sold with LED strips, for one click. */
export const COMMON_SUPPLIES: ReadonlyArray<{ volts: Volts; amps: number }> = [
  { volts: 5, amps: 2 },
  { volts: 5, amps: 4 },
  { volts: 5, amps: 10 },
  { volts: 12, amps: 5 },
  { volts: 24, amps: 5 },
];

/** The supply's volts when known (over USB: 5 V). */
export function supplyVolts(supply: SupplyChoice | null): Volts | null {
  return supply?.kind === 'printed' ? supply.volts : supply?.kind === 'usb' ? 5 : null;
}


/** An output as the wizard plans it: where it is in the board's LEDs, and what `wled-write` gets for it. */
export type PlannedOutput = WledOutputChange & { index: number; start: number; pin: number };

/** The pins the strips use (data and the clock pins set). */
function usedPins(strips: readonly BoardStrip[]): number[] {
  return strips.flatMap((s) => [s.pin, ...(s.clockPin !== null ? [s.clockPin] : [])]);
}

/** The outputs one after the other, each strip on its pin (and clock pin: a safe free one unless set). */
export function boardOutputs(b: BoardSettings): PlannedOutput[] {
  const used = usedPins(b.strips);
  let start = 0;
  return b.strips.map((s, index) => {
    let clockPin: number | undefined;
    if (s.preset && presetById(s.preset)?.clock) {
      clockPin = s.clockPin ?? safePin(b.chip, used);
      used.push(clockPin);
    }
    const out: PlannedOutput = {
      index,
      // a strip whose type isn't asked yet (the ones after the strip set up now) goes out as WS2812B, the usual
      // type, so it keeps its pin; its own type step writes the real one (a restart only when that differs)
      strip: s.preset ?? 'ws2812b',
      pin: s.pin,
      ...(clockPin !== undefined ? { clockPin } : {}),
      length: Math.max(1, Math.round(s.ledCount ?? DEFAULT_LEDS)),
      start,
      ...(s.colorOrder ? { colorOrder: s.colorOrder } : {}),
      reverse: false,
    };
    start += out.length;
    return out;
  });
}

/** The board's current limit: 85% of the supply (within what WLED takes, 250–65000 mA), else WLED's USB-safe one. */
export function supplyLimitMa(supply: SupplyChoice | null): number {
  return supply?.kind === 'printed' ? Math.min(65000, Math.max(250, currentLimitMa(supply.amps))) : USB_SAFE_MA;
}

export function boardLimitMa(b: Pick<BoardSettings, 'supply'>): number {
  return supplyLimitMa(b.supply);
}

/**
 * The `wled-write` for a board: every output, its limit, the current per LED (the most any of its strip types draws;
 * WS2815 only: the board's own model) and, for a board still called "WLED", a name from its strips.
 */
export function boardChange(b: BoardSettings): WledChange & { outputs: WledOutputChange[]; maxCurrent: number } {
  const currents = b.strips.flatMap((s) => (s.preset && s.preset !== 'ws2815' ? [presetById(s.preset)?.maPerLed ?? 55] : []));
  return {
    ...(b.name.trim() === '' || b.name.trim() === 'WLED' ? { name: boardName(b) } : {}),
    outputs: boardOutputs(b).map(({ index: _index, ...o }) => o),
    maxCurrent: boardLimitMa(b),
    ...(currents.length ? { ledCurrent: Math.min(254, Math.max(...currents)) } : {}),
  };
}

/** A strip as the board's own output (to light or write just that strip's length). */
export function withStrip(b: BoardSettings, i: number, patch: Partial<BoardStrip>): BoardSettings {
  return { ...b, strips: b.strips.map((s, k) => (k === i ? { ...s, ...patch } : s)) };
}

/**
 * Strip `i`'s name from its places ("Monitor", "Desk and monitor"), numbered when two strips have the same; "Strip 2"
 * without one.
 */
export function stripName(b: BoardSettings, i: number): string {
  const places = b.strips[i].places.filter((p) => p !== 'other');
  if (!places.length) return `Strip ${i + 1}`;
  const names = places.map((p) => STRIP_PLACES.find((x) => x.value === p)?.name ?? 'Strip');
  const name = names.length === 1 ? names[0] : `${names[0]} and ${names.slice(1).join(' and ').toLowerCase()}`;
  const key = places.join(',');
  const same = b.strips.filter((s) => s.places.filter((p) => p !== 'other').join(',') === key);
  return same.length > 1 ? `${name} ${same.indexOf(b.strips[i]) + 1}` : name;
}

/** The device's name: the board's own, else its strips' places ("Monitor and ceiling"). */
export function boardName(b: BoardSettings): string {
  const own = b.name.trim();
  if (own && own !== 'WLED') return own;
  const names = [...new Set(b.strips.map((_, i) => stripName(b, i).replace(/ \d+$/, '')))];
  if (names.includes('Strip')) return 'WLED';
  if (names.length === 1) return names[0].includes(' and ') ? names[0] : `${names[0]} lights`;
  if (names.length === 2 && !names.some((n) => n.includes(' and '))) return `${names[0]} and ${names[1].toLowerCase()}`;
  return 'WLED';
}

/** A strip for a new output of a board: its pin (the board's, else a safe free one), the usual type, no place yet. */
function newStrip(chip: string | undefined, used: readonly number[], had?: WledOutput): BoardStrip {
  return {
    places: [],
    preset: had?.strip && presetById(had.strip) ? had.strip : null,
    ledCount: had ? had.length : null,
    pin: had?.pin ?? safePin(chip, used),
    clockPin: had?.clockPin ?? null,
    colorOrder: had?.colorOrder ?? null,
    plan: null,
  };
}

/**
 * Starting values for a board: a board that is set up keeps its strips (count, LEDs, pins, types); a new one gets one
 * strip on its own pin with the LED count left to the check.
 */
export function initialBoard(read: WledState): BoardSettings {
  const setUp = setUpAlready(read) !== null;
  const strips: BoardStrip[] = [];
  if (setUp) read.outputs.forEach((o) => strips.push(newStrip(read.chip, usedPins(strips), o)));
  // a new board's own output says nothing about the strip: its type and LEDs are asked
  else strips.push({ ...newStrip(read.chip, [], read.outputs[0]), ledCount: null, preset: null, colorOrder: null });
  return { name: read.name, chip: read.chip, strips, supply: null };
}

/**
 * The board's strips as Find your strips found them: one per found connection, in the order found, on its pin and
 * running where the user said; type and LEDs still to be asked.
 */
export function stripsFromFound(b: BoardSettings, found: ReadonlyArray<{ candidate: { pin: number }; places: StripPlace[] }>): BoardSettings {
  return {
    ...b,
    strips: found.map((f) => ({ places: [...f.places], preset: null, ledCount: null, pin: f.candidate.pin, clockPin: null, colorOrder: null, plan: null })),
  };
}


/**
 * How many strip outputs WLED drives on a chip: its digital channels (WLED_MAX_DIGITAL_CHANNELS in wled00/const.h,
 * the same in 0.15 and 16: ESP32 16, S2 and S3 12, ESP8266 3, C3 2); 16 when the chip isn't known (WLED refuses
 * more than it has).
 */
export function maxOutputs(chip: string | undefined): number {
  const c = (chip ?? '').toLowerCase().replace(/\s+/g, '');
  if (c.includes('8266')) return 3;
  if (c.includes('c3')) return 2;
  if (c.includes('s3') || c.includes('s2')) return 12;
  return 16;
}

/** The strips whose place is still to be chosen (indexes). */
export function unplacedStrips(b: Pick<BoardSettings, 'strips'>): number[] {
  return b.strips.flatMap((s, i) => (s.places.length === 0 ? [i] : []));
}

/**
 * Why strip `i` can't be written yet (its type, or a type the supply's volts don't fit), or null; the strips after it
 * are asked about later.
 */
export function stripProblem(b: BoardSettings, i: number): string | null {
  const s = b.strips[i];
  if (!s?.preset) return 'Pick the strip’s type.';
  const volts = supplyVolts(b.supply);
  const p = presetById(s.preset);
  if (p && volts !== null && !acceptsVolts(p, volts))
    return `The supply gives ${volts} V, but a ${p.name} strip takes ${p.volts} V: ${volts > p.volts ? 'it would burn the LEDs out' : 'the LEDs would stay dark'}. Use a ${p.volts} V supply.`;
  const pins = boardOutputs(b).flatMap((o) => [o.pin, ...(o.clockPin !== undefined ? [o.clockPin] : [])]);
  if (new Set(pins).size !== pins.length) return 'Two strips are on the same pin: check the pins under More options.';
  return null;
}

/** The settings with the color orders the board has now (after a write that let it pick them). */
export function withBoardOrder(b: BoardSettings, read: WledState): BoardSettings {
  return { ...b, strips: b.strips.map((s, i) => (read.outputs[i]?.colorOrder ? { ...s, colorOrder: read.outputs[i].colorOrder } : s)) };
}

/** LEDs per meter and a length (cm) as an LED count: "Count them" made easy (More options). */
export function ledsFromLength(cm: number, perMeter: number): number | null {
  return cm > 0 && perMeter > 0 ? Math.max(1, Math.floor((cm / 100) * perMeter + 1e-9)) : null;
}

/**
 * Runs calls one after the other, in the order they were made (each starts when the one before ended, failed or not),
 * so writes to a board never land out of order.
 */
export function createSerial(): <T>(fn: () => Promise<T>) => Promise<T> {
  let tail: Promise<unknown> = Promise.resolve();
  return <T>(fn: () => Promise<T>) => {
    const run = tail.then(fn, fn);
    tail = run.catch(() => undefined);
    return run;
  };
}

/**
 * Waits for a restarting board: calls `read` every `intervalMs` until it answers or `timeoutMs` passed (WLED takes
 * 3–10 s to restart). Resolves with what it read, or rejects with the last error.
 */
export async function waitForBoard<T>(
  read: () => Promise<T>,
  { timeoutMs = 30000, intervalMs = 1500, sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms)), now = () => Date.now(), signal }: {
    timeoutMs?: number;
    intervalMs?: number;
    sleep?: (ms: number) => Promise<void>;
    now?: () => number;
    signal?: AbortSignal;
  } = {},
): Promise<T> {
  const end = now() + timeoutMs;
  let last: unknown = new Error('The board didn’t come back after restarting.');
  await sleep(intervalMs);
  while (!signal?.aborted) {
    try {
      return await read();
    } catch (e) {
      last = e;
    }
    if (now() >= end) break;
    await sleep(intervalMs);
  }
  throw last instanceof Error ? last : new Error(String(last));
}

/**
 * A board's outputs as it reports them (`wled-read`): where each really starts and how long it is, for a board added as
 * it is (its outputs needn't follow each other).
 */
export function outputsAsRead(read: WledState): Array<{ index: number; start: number; length: number; pin: number }> {
  return read.outputs.map((o) => ({ index: o.index, start: o.start, length: o.length, pin: o.pin }));
}

/** The device the wizard adds to the layout: WLED at its host, one output per strip. */
export function wledController(host: string, name: string, outputs: ReadonlyArray<{ index: number; start: number; length: number; pin: number }>): Controller {
  return {
    id: slugifyHost(host) || 'wled',
    name: name.trim() || 'WLED',
    kind: 'wled',
    host,
    outputs: outputs.map((o) => ({ index: o.index, start: o.start, length: o.length, pin: o.pin })),
    device: { type: 'wled', host },
  };
}

/** The screen sized as planned (meters, centered where it was), for the layout's room. */
export function plannedScreen(plan: StripPlan, room: Room): Room['screen'] {
  const width = plan.input.screen.widthMm / 1000;
  const height = plan.input.screen.heightMm / 1000;
  const cx = room.screen.x + room.screen.width / 2;
  const bottom = room.screen.y + room.screen.height;
  return { x: cx - width / 2, y: Math.max(0, bottom - height), width, height };
}

/**
 * The planned run as a strip path on the front wall around `screen` (as seen from the seat, like the plan): a point
 * at each corner matched to the LED there, and a gap across the stand.
 */
export function plannedPath(plan: StripPlan, screen: Room['screen']): PathPoint[] {
  const { widthMm: W, heightMm: H } = plan.input.screen;
  const at = (p: { x: number; y: number }) => ({ surface: 'front' as const, x: screen.x + (p.x / W) * screen.width, y: screen.y + (p.y / H) * screen.height });
  const path: PathPoint[] = [];
  plan.pieces.forEach((piece, i) => {
    const joint = plan.joints.find((j) => j.afterLed === piece.firstLed - 1);
    if (i === 0) path.push(at(piece.from));
    else if (joint?.kind === 'jumper') {
      // the end of the piece before was pushed already: make it a gap to this piece's start
      path[path.length - 1] = { ...path[path.length - 1], next: { kind: 'gap' } };
      path.push({ ...at(piece.from), at: piece.firstLed - 1 });
    }
    const last = i === plan.pieces.length - 1;
    path.push(last ? at(piece.to) : { ...at(piece.to), at: piece.lastLed });
  });
  return path;
}

/** One place's part of a strip in the layout. */
function placePart(place: StripPlace, strip: BoardStrip, room: Room, ledCount: number, screen: Room['screen'], alone: boolean): PathPoint[] {
  switch (place) {
    case 'screen': {
      if (!strip.plan || strip.plan.totalLeds === 0) return shapePath({ ...room, screen }, 'around-screen');
      const path = plannedPath(strip.plan, screen);
      // matched at the corners only while the strip is just the planned one
      return alone && ledCount === strip.plan.totalLeds ? path : unpinned(path);
    }
    case 'ceiling':
      return shapePath(room, 'ceiling-front');
    case 'desk':
      return shapePath(room, 'behind-furniture');
    default:
      return shapePath(room, 'line');
  }
}

/**
 * Where a strip goes in the layout, by where the user said it runs: through each of its places (around the
 * screen, as planned when there is a plan; across the ceiling's front edge; behind the desk; a line on the wall to drag
 * into place), the way from one place to the next a gap without LEDs. Layout's matching refines where each part ends.
 * Every part shows the screen's average (roles.ts): which way the strip really runs isn't known until it is traced.
 */
export function placedPath(strip: BoardStrip, room: Room, ledCount: number, screen: Room['screen'] = room.screen): PathPoint[] {
  const places = strip.places.length ? strip.places : (['other'] as StripPlace[]);
  const path: PathPoint[] = [];
  places.forEach((place, k) => {
    const part = withRole(placePart(place, strip, room, ledCount, screen, places.length === 1), 'soft');
    if (k > 0 && path.length) path[path.length - 1] = { ...path[path.length - 1], next: { kind: 'gap' } };
    path.push(...part.map((p) => ({ ...p })));
  });
  return path;
}
