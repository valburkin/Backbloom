// "Plan your strips" (docs/guided-setup.md): from the screen's size and the strip type to a cut plan, a power plan
// and a parts checklist. Pure functions, no backend calls.
//
// Coordinates are millimeters on the screen as seen from the seat: x from the left edge, y from the top edge. The
// strip runs on the back of the screen, `insetMm` inside its edges, so seen from behind left and right swap.

export type Volts = 5 | 12 | 24;

/** The facts about a strip type the plan needs. */
export interface StripPreset {
  /** The id `hardware wled-write` takes as an output's `strip`. */
  id: string;
  name: string;
  chip: string;
  volts: Volts;
  /** LEDs per meter it is sold with, counted as Backbloom counts LEDs (one per lit group, see `groupOf`). */
  densities: readonly number[];
  defaultDensity: number;
  /** LEDs between two cut marks. */
  ledsPerCut: number;
  /** Current one LED draws at full white, mA at the strip's volts. */
  maPerLed: number;
  /** How much strip one power feed keeps bright at full white, at `defaultDensity` (m). */
  feedReachM: number;
  /** Wires between two pieces: power, data and ground, plus a backup data line (WS2813, WS2815) or a clock (APA102). */
  wires: 3 | 4;
  /** The strip has a white LED in each group (RGBW). */
  white: boolean;
  /** Physical LEDs that light together as one (12 V and 24 V strips with one chip per group); 1 otherwise. */
  groupOf: number;
  /** A clock wire besides the data wire: the board needs a second pin for it (`clockPin`). */
  clock?: boolean;
  /** Supplies it runs on when not only `volts` (sold for 12 and 24 V alike, driven the same). */
  voltsAccepted?: readonly Volts[];
  /**
   * COB: a continuous line of light without visible dots. Its listing gives the LEDs per meter (hundreds), but it lights
   * in segments, one chip each (the IC/m or pixels/m): `densities` and every count are in segments.
   */
  cob?: boolean;
  /** One sentence for the picker. */
  hint: string;
}

// Rules of thumb behind the numbers (for a strip at full white, the worst case the power plan sizes for):
// - Current: a 5 V RGB LED draws about 20 mA per color, so 60 mA at full white (18 W/m at 60 LEDs/m, as WS2812B
//   data sheets give). RGBW adds the white LED (about 80 mA). 12 V and 24 V strips put LEDs in series, so the
//   current per lit group is lower for the same light: WS2811 groups of 3 at 12 V about 60 mA (14.4 W/m at 60
//   LEDs/m), WS2815 about 20 mA per LED (14.4 W/m), WS2814 groups of 6 at 24 V about 80 mA (19.2 W/m).
// - Power feeds: thin copper on the strip drops voltage along it; at 5 V the far end of 2–3 m (150 LEDs at 60/m)
//   turns orange or dim, so feed power again about every 2.5 m (less at higher densities: the reach scales with the
//   current per meter). 12 V reaches about 5 m and 24 V about 10 m for the same light.
// - Power supply: the next standard size with 20% headroom over full white; the board's current limit is set to 85%
//   of the supply, so it never runs at its maximum.
export const STRIP_PRESETS: readonly StripPreset[] = [
  {
    id: 'ws2812b',
    name: 'WS2812B',
    chip: 'WS2812B',
    volts: 5,
    densities: [30, 60, 96, 144],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 2.5,
    wires: 3,
    white: false,
    groupOf: 1,
    hint: 'The most common strip: 5 V, three wires, cut after any LED.',
  },
  {
    id: 'sk6812-rgbw',
    name: 'SK6812 RGBW',
    chip: 'SK6812',
    volts: 5,
    densities: [30, 60, 144],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 80,
    feedReachM: 2,
    wires: 3,
    white: true,
    groupOf: 1,
    hint: '5 V with a white LED in each one, for clean whites.',
  },
  {
    id: 'ws2813',
    name: 'WS2813',
    chip: 'WS2813',
    volts: 5,
    densities: [30, 60, 144],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 2.5,
    wires: 4,
    white: false,
    groupOf: 1,
    hint: '5 V with a backup data wire: one broken LED doesn’t stop the rest.',
  },
  {
    id: 'apa102',
    name: 'APA102 (SK9822)',
    chip: 'APA102',
    volts: 5,
    densities: [30, 60, 144],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 2.5,
    wires: 4,
    white: false,
    groupOf: 1,
    clock: true,
    hint: '5 V with a clock wire besides the data wire: it needs two pins on the board.',
  },
  {
    id: 'ws2811-12v',
    voltsAccepted: [12, 24],
    name: 'WS2811 12 V',
    chip: 'WS2811',
    volts: 12,
    densities: [20, 10],
    defaultDensity: 20,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 5,
    wires: 3,
    white: false,
    groupOf: 3,
    hint: '12 V: the LEDs light in groups of 3, and each group counts as one LED.',
  },
  {
    id: 'ws2815',
    name: 'WS2815',
    chip: 'WS2815',
    volts: 12,
    densities: [30, 60, 144],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 20,
    feedReachM: 5,
    wires: 4,
    white: false,
    groupOf: 1,
    hint: '12 V with each LED on its own and a backup data wire: good for long runs.',
  },
  {
    id: 'ws2814-rgbw',
    voltsAccepted: [12, 24],
    name: 'WS2814 RGBW 24 V',
    chip: 'WS2814',
    volts: 24,
    densities: [10, 20],
    defaultDensity: 10,
    ledsPerCut: 1,
    maPerLed: 80,
    feedReachM: 10,
    wires: 3,
    white: true,
    groupOf: 6,
    hint: '24 V with white: the LEDs light in groups of 6, and each group counts as one LED.',
  },
  // COB strips (the backend's ws2812-cob, ws2811-cob, ws2814-cob-rgbw): counted in segments, one chip each. The current
  // per segment is the chip's own non-COB value (WS2812 60 mA, WS2811 60 mA, WS2814 80 mA), as the backend expects.
  {
    id: 'ws2812-cob',
    name: 'WS2812 COB',
    chip: 'WS2812',
    volts: 5,
    densities: [60, 90, 100, 160],
    defaultDensity: 90,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 2,
    wires: 3,
    white: false,
    groupOf: 1,
    cob: true,
    hint: '5 V COB: a continuous line of light that lights in groups of LEDs.',
  },
  {
    id: 'ws2811-cob',
    name: 'WS2811 COB',
    chip: 'WS2811',
    volts: 24,
    voltsAccepted: [12, 24],
    densities: [36, 60, 72, 90],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 60,
    feedReachM: 8,
    wires: 3,
    white: false,
    groupOf: 1,
    cob: true,
    hint: '12 or 24 V COB: a continuous line of light that lights in groups of LEDs.',
  },
  {
    id: 'ws2814-cob-rgbw',
    name: 'WS2814 COB RGBW',
    chip: 'WS2814',
    volts: 24,
    voltsAccepted: [12, 24],
    densities: [36, 60, 72],
    defaultDensity: 60,
    ledsPerCut: 1,
    maPerLed: 80,
    feedReachM: 8,
    wires: 3,
    white: true,
    groupOf: 1,
    cob: true,
    hint: '12 or 24 V COB with white: a continuous line of light that lights in groups of LEDs.',
  },
];

/** The strip runs on a supply of `volts`. */
export function acceptsVolts(preset: StripPreset, volts: number): boolean {
  return (preset.voltsAccepted ?? [preset.volts]).includes(volts as Volts);
}

export function presetById(id: string): StripPreset | undefined {
  return STRIP_PRESETS.find((p) => p.id === id);
}

/** How far one power feed reaches on this strip at this density (m). */
export function feedReachM(preset: StripPreset, density: number): number {
  return (preset.feedReachM * preset.defaultDensity) / Math.max(1, density);
}

// ---- screen ----

export interface ScreenSize {
  widthMm: number;
  heightMm: number;
}

export const ASPECTS = [
  { id: '16:9', w: 16, h: 9 },
  { id: '16:10', w: 16, h: 10 },
  // 3440 × 1440 and 2560 × 1080 are sold as 21:9 (the panels are closer to 2.39:1)
  { id: '21:9', w: 43, h: 18 },
  { id: '32:9', w: 32, h: 9 },
  { id: '4:3', w: 4, h: 3 },
] as const;

export type AspectId = (typeof ASPECTS)[number]['id'];

/** Width and height of a screen from its diagonal (inches, as screens are sold) and aspect ratio. */
export function screenFromDiagonal(inches: number, aspect: AspectId): ScreenSize {
  const a = ASPECTS.find((x) => x.id === aspect) ?? ASPECTS[0];
  const d = inches * 25.4;
  const k = d / Math.hypot(a.w, a.h);
  return { widthMm: Math.round(a.w * k), heightMm: Math.round(a.h * k) };
}

/** The diagonal in inches (rounded to a half), for showing a measured screen the way it is sold. */
export function diagonalInches(size: ScreenSize): number {
  return Math.round((Math.hypot(size.widthMm, size.heightMm) / 25.4) * 2) / 2;
}

// ---- the plan ----

export type Side = 'top' | 'right' | 'bottom' | 'left';
export type Corner = 'top-left' | 'top-right' | 'bottom-right' | 'bottom-left';
/** Where the strip starts (where the board's data wire comes in). `bottom-middle`: next to the stand. */
export type StartPoint = Corner | 'bottom-middle';
/** How a piece turns a corner into the next: folded at 45°, a clip-on corner connector, or soldered wires. */
export type CornerJoin = 'fold' | 'connector' | 'wire';
export type Arrow = 'up' | 'down' | 'left' | 'right';

export interface Point {
  x: number;
  y: number;
}

export interface PlanInput {
  screen: ScreenSize;
  /** 3: left, top and right (the usual for a monitor on a stand); 4: all around. */
  sides: 3 | 4;
  /** Four sides: the width the stand takes in the middle of the bottom edge, mm (0: none, a wall mount). */
  standGapMm: number;
  /** How far inside the screen's edge the strip runs on its back, mm. */
  insetMm: number;
  start: StartPoint;
  /** The direction the data runs, as seen from the seat (three sides: follows from the start). */
  clockwise: boolean;
  preset: string;
  /** LEDs per meter (one of the preset's densities). */
  density: number;
  corners: CornerJoin;
  /** The power supply the user has, if they have one already. */
  supply: { volts: number; amps: number } | null;
}

export const DEFAULT_PLAN_INPUT: PlanInput = {
  screen: screenFromDiagonal(27, '16:9'),
  sides: 3,
  standGapMm: 200,
  insetMm: 20,
  start: 'bottom-left',
  clockwise: true,
  preset: 'ws2812b',
  density: 60,
  corners: 'connector',
  supply: null,
};

/** One piece of strip between two corners (or a corner and the stand). LEDs numbered from 1 along the whole run. */
export interface PlanPiece {
  side: Side;
  /** "Left", "Top", "Bottom, right of the stand"… */
  label: string;
  from: Point;
  to: Point;
  /** Room along the edge between its ends, mm. */
  spaceMm: number;
  leds: number;
  /** The piece's length when cut at the cut mark after its last LED, mm. */
  cutMm: number;
  firstLed: number;
  lastLed: number;
  /** The data direction, as seen from the seat. */
  arrow: Arrow;
  /**
   * Folded corners: where the slot of the piece's first LED starts, mm from the piece's start (negative: it began on
   * the side before; the LED counts here as its middle is here). 0 for cut pieces.
   */
  offsetMm: number;
}

/** Where two pieces meet: a corner, or the wire across the stand ("jumper") that carries the data past it. */
export interface PlanJoint {
  kind: 'corner' | 'jumper';
  at: Point;
  corner: Corner | null;
  /** The last LED before the joint. */
  afterLed: number;
  join: CornerJoin | 'wire';
  /** Jumpers: the wire's length, mm. */
  wireMm: number;
}

/** A place where the supply's + and − go into the strip. The first one is at LED 1 (through or next to the board). */
export interface PowerFeed {
  led: number;
  at: Point;
  /** Where it is in words: "LED 1 (start)", "top-right corner"… */
  where: string;
}

export interface PlanWarning {
  tone: 'bad' | 'warn';
  text: string;
}

export interface ChecklistItem {
  group: 'Parts' | 'Cut' | 'Connect';
  text: string;
}

export interface StripPlan {
  input: PlanInput;
  preset: StripPreset;
  pieces: PlanPiece[];
  joints: PlanJoint[];
  totalLeds: number;
  /** All pieces as cut, mm. */
  stripMm: number;
  /** Current at full white, A. */
  fullWhiteA: number;
  /** The supply to buy: next standard size with 20% headroom (the strip's volts). */
  suggestedSupply: SupplySuggestion;
  /** The board's current limit (85% of the supply the user has, else of the suggested one), mA. */
  limitMa: number;
  feeds: PowerFeed[];
  warnings: PlanWarning[];
  checklist: ChecklistItem[];
}

/** Power supply sizes sold, in A, per voltage. */
export const SUPPLY_SIZES: Record<Volts, readonly number[]> = {
  5: [2, 3, 4, 6, 8, 10, 15, 20, 30, 40, 60],
  12: [1, 2, 3, 5, 6, 8, 10, 12.5, 15, 20, 30],
  24: [1, 1.5, 2.5, 3, 5, 6.5, 10, 15, 20],
};

/** The board's current limit for a supply: 85% of what it gives, mA. */
export function currentLimitMa(amps: number): number {
  return Math.round(amps * 850);
}

/** Power supplies to get: `count` of `each` A (one, unless more than the largest size is needed); `amps`, `watts` in all. */
export interface SupplySuggestion {
  volts: Volts;
  amps: number;
  watts: number;
  count: number;
  each: number;
}

/**
 * The next standard supply with 20% headroom over `amps`. Above the largest size sold: as few supplies of one size as
 * cover it, each feeding its own part of the strip (their + kept apart, the − joined).
 */
export function suggestSupply(volts: Volts, amps: number): SupplySuggestion {
  const need = amps * 1.2;
  const sizes = SUPPLY_SIZES[volts];
  const largest = sizes[sizes.length - 1];
  const count = Math.max(1, Math.ceil(need / largest - 1e-9));
  const each = sizes.find((x) => x * count >= need - 1e-9) ?? largest;
  return { volts, amps: each * count, watts: Math.round(each * count * volts), count, each };
}

/** A supply suggestion in words: "5 V, at least 6 A (30 W)" or "2 supplies of 5 V, 40 A each (400 W in all)". */
export function supplyText(s: SupplySuggestion): string {
  return s.count === 1
    ? `${s.volts} V, at least ${s.amps} A (${s.watts} W)`
    : `${s.count} supplies of ${s.volts} V, ${s.each} A each (${s.watts} W in all), each powering its own part of the strip`;
}

/** Wire thickness for a power feed carrying `amps`. */
export function wireGauge(amps: number): string {
  if (amps <= 3) return '20 AWG (0.5 mm²)';
  if (amps <= 5) return '18 AWG (0.75 mm²)';
  if (amps <= 10) return '16 AWG (1.5 mm²)';
  return '14 AWG (2.5 mm²)';
}

/** The starts that make sense: three sides start at an open end; four sides at any corner, or next to the stand. */
export function startOptions(sides: 3 | 4): StartPoint[] {
  return sides === 3 ? ['bottom-left', 'bottom-right'] : ['bottom-left', 'bottom-middle', 'bottom-right', 'top-left', 'top-right'];
}

/** The input with a start and direction that fit its sides (three sides: the direction follows from the start). */
export function normalizeInput(input: PlanInput): PlanInput {
  const starts = startOptions(input.sides);
  const start = starts.includes(input.start) ? input.start : starts[0];
  const clockwise = input.sides === 3 ? start === 'bottom-left' : input.clockwise;
  const preset = presetById(input.preset) ?? STRIP_PRESETS[0];
  const density = preset.densities.includes(input.density) ? input.density : preset.defaultDensity;
  return { ...input, start, clockwise, preset: preset.id, density };
}

export const CORNER_LABELS: Record<Corner, string> = {
  'top-left': 'top-left corner',
  'top-right': 'top-right corner',
  'bottom-right': 'bottom-right corner',
  'bottom-left': 'bottom-left corner',
};

export const START_LABELS: Record<StartPoint, string> = {
  'bottom-left': 'Bottom left',
  'bottom-middle': 'Next to the stand',
  'bottom-right': 'Bottom right',
  'top-left': 'Top left',
  'top-right': 'Top right',
};

/** Where the strip starts, in a sentence ("Start at the bottom left"). */
const START_PHRASES: Record<StartPoint, string> = {
  'bottom-left': 'at the bottom left',
  'bottom-middle': 'next to the stand',
  'bottom-right': 'at the bottom right',
  'top-left': 'at the top left',
  'top-right': 'at the top right',
};

interface Edge {
  a: string;
  b: string;
  side: Side;
  label: string;
  gap?: boolean;
}

function arrowOf(a: Point, b: Point): Arrow {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  if (Math.abs(dx) >= Math.abs(dy)) return dx >= 0 ? 'right' : 'left';
  return dy >= 0 ? 'down' : 'up';
}

/** The pieces in data order: which edge, from where to where (node names). */
function runEdges(input: PlanInput, hasGap: boolean): Edge[] {
  if (input.sides === 3) {
    const u: Edge[] = [
      { a: 'BL', b: 'TL', side: 'left', label: 'Left' },
      { a: 'TL', b: 'TR', side: 'top', label: 'Top' },
      { a: 'TR', b: 'BR', side: 'right', label: 'Right' },
    ];
    return input.start === 'bottom-right' ? u.reverse().map((e) => ({ ...e, a: e.b, b: e.a })) : u;
  }
  // all around, clockwise as seen from the seat
  const split = hasGap || input.start === 'bottom-middle';
  const ring: Edge[] = [
    { a: 'TL', b: 'TR', side: 'top', label: 'Top' },
    { a: 'TR', b: 'BR', side: 'right', label: 'Right' },
    ...(split
      ? [
          { a: 'BR', b: 'GR', side: 'bottom' as const, label: 'Bottom, right of the stand' },
          { a: 'GR', b: 'GL', side: 'bottom' as const, label: 'Stand', gap: true },
          { a: 'GL', b: 'BL', side: 'bottom' as const, label: 'Bottom, left of the stand' },
        ]
      : [{ a: 'BR', b: 'BL', side: 'bottom' as const, label: 'Bottom' }]),
    { a: 'BL', b: 'TL', side: 'left', label: 'Left' },
  ];
  const directed = input.clockwise ? ring : ring.reverse().map((e) => ({ ...e, a: e.b, b: e.a }));
  const startNode =
    input.start === 'bottom-middle' ? (input.clockwise ? 'GL' : 'GR') : ({ 'top-left': 'TL', 'top-right': 'TR', 'bottom-right': 'BR', 'bottom-left': 'BL' } as const)[input.start];
  const i = directed.findIndex((e) => e.a === startNode);
  const rotated = [...directed.slice(i), ...directed.slice(0, i)];
  // starting next to the stand, the stand is where the run begins and ends: no wire across it
  return input.start === 'bottom-middle' ? rotated.filter((e) => !e.gap) : rotated;
}

const CORNER_OF: Record<string, Corner> = { TL: 'top-left', TR: 'top-right', BR: 'bottom-right', BL: 'bottom-left' };

/** `x` rounded down to a multiple of `step` (with a little room for floating point: 2050 mm at 60/m is 123 LEDs). */
function floorTo(x: number, step: number): number {
  return Math.floor(x / step + 1e-9) * step;
}

/** Where LED `led` (from 1) sits on the run. */
export function ledPoint(plan: Pick<StripPlan, 'pieces' | 'input'>, led: number): Point {
  const piece = plan.pieces.find((p) => led >= p.firstLed && led <= p.lastLed) ?? plan.pieces[plan.pieces.length - 1];
  if (!piece) return { x: 0, y: 0 };
  // folded corners: the piece's first LED sits where the run's spacing puts it, not at its corner
  const along = ((led - piece.firstLed + 0.5) * 1000) / plan.input.density + piece.offsetMm;
  const t = piece.spaceMm > 0 ? Math.max(0, Math.min(1, along / piece.spaceMm)) : 0;
  return { x: piece.from.x + (piece.to.x - piece.from.x) * t, y: piece.from.y + (piece.to.y - piece.from.y) * t };
}

function roundCm(mm: number): string {
  const cm = Math.round(mm / 10);
  return `${cm} cm`;
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** The cut plan, power plan and checklist for a screen and a strip type; null when the screen has no usable size. */
export function planStrips(raw: PlanInput): StripPlan | null {
  const input = normalizeInput(raw);
  const preset = presetById(input.preset)!;
  const W = input.screen.widthMm;
  const H = input.screen.heightMm;
  const i = Math.max(0, input.insetMm);
  if (!(W > 2 * i + 20) || !(H > 2 * i + 20)) return null;

  const gapMm = input.sides === 4 ? Math.max(0, Math.min(input.standGapMm, W - 2 * i - 40)) : 0;
  const nodes: Record<string, Point> = {
    TL: { x: i, y: i },
    TR: { x: W - i, y: i },
    BR: { x: W - i, y: H - i },
    BL: { x: i, y: H - i },
    GR: { x: W / 2 + gapMm / 2, y: H - i },
    GL: { x: W / 2 - gapMm / 2, y: H - i },
  };
  const edges = runEdges(input, gapMm > 0);

  const pieces: PlanPiece[] = [];
  const joints: PlanJoint[] = [];
  let led = 0;
  let pendingJumper: { at: Point; wireMm: number } | null = null;
  const fold = input.corners === 'fold';
  const lengthOf = (e: { a: string; b: string }) => Math.hypot(nodes[e.b].x - nodes[e.a].x, nodes[e.b].y - nodes[e.a].y);
  // Folded corners keep the strip in one piece up to the stand (or its end): LEDs follow each other along the run, so a
  // side gets the LEDs whose places fall on it, and only the run's end is cut (at a cut mark). Otherwise every piece is
  // cut at the last cut mark that fits on its side.
  let runMm = 0;
  let runLeds = 0;
  for (const [k, e] of edges.entries()) {
    const from = nodes[e.a];
    const to = nodes[e.b];
    const space = lengthOf(e);
    if (e.gap) {
      pendingJumper = { at: from, wireMm: Math.ceil((space + 60) / 10) * 10 };
      runMm = 0;
      runLeds = 0;
      continue;
    }
    let leds: number;
    let offsetMm = 0;
    if (fold) {
      // where this piece's first LED slot starts, from the piece's start
      offsetMm = (runLeds * 1000) / input.density - runMm;
      runMm += space;
      const runEnds = k === edges.length - 1 || edges[k + 1].gap === true;
      // up to a corner: the LEDs whose middle is before it; at the run's end: the last cut mark that fits
      const upTo = runEnds ? floorTo((runMm / 1000) * input.density, preset.ledsPerCut) : floorTo((runMm / 1000) * input.density + 0.5, 1);
      leds = Math.max(0, upTo - runLeds);
      runLeds += leds;
    } else leds = Math.max(0, floorTo((space / 1000) * input.density, preset.ledsPerCut));
    if (pieces.length > 0) {
      if (pendingJumper)
        joints.push({ kind: 'jumper', at: pendingJumper.at, corner: null, afterLed: led, join: 'wire', wireMm: pendingJumper.wireMm });
      else joints.push({ kind: 'corner', at: from, corner: CORNER_OF[e.a] ?? null, afterLed: led, join: input.corners, wireMm: 0 });
    }
    pendingJumper = null;
    pieces.push({
      side: e.side,
      label: e.label,
      from,
      to,
      spaceMm: Math.round(space),
      leds,
      cutMm: Math.round((leds * 1000) / input.density),
      firstLed: led + 1,
      lastLed: led + leds,
      arrow: arrowOf(from, to),
      offsetMm,
    });
    led += leds;
  }
  const totalLeds = led;
  const stripMm = pieces.reduce((n, p) => n + p.cutMm, 0);
  const fullWhiteA = (totalLeds * preset.maPerLed) / 1000;
  const suggestedSupply = suggestSupply(preset.volts, fullWhiteA);
  const supply = input.supply && input.supply.volts > 0 && input.supply.amps > 0 ? input.supply : null;
  const limitMa = currentLimitMa(supply ? supply.amps : suggestedSupply.amps);

  const partial = { input, pieces };
  const feeds = planFeeds(partial, joints, totalLeds, feedReachM(preset, input.density) * input.density);
  const warnings = planWarnings(input, preset, pieces, fullWhiteA, supply);
  const plan: StripPlan = { input, preset, pieces, joints, totalLeds, stripMm, fullWhiteA, suggestedSupply, limitMa, feeds, warnings, checklist: [] };
  plan.checklist = planChecklist(plan, supply);
  return plan;
}

/**
 * Power feeds: one at the start, then each one `reachLeds` after the one before (moved back to a joint within a fifth
 * of that, where wires are easy to add, so no stretch is longer than the reach), and one at the far end when the last stretch is longer than three quarters of the reach (a
 * typical monitor strip of 1–1.5 m at 5 V runs fine from one end, the more so with the board's current limit).
 */
export function planFeeds(plan: Pick<StripPlan, 'pieces' | 'input'>, joints: readonly PlanJoint[], total: number, reachLeds: number): PowerFeed[] {
  if (total === 0) return [];
  const feeds: PowerFeed[] = [{ led: 1, at: ledPoint(plan, 1), where: 'LED 1, the start' }];
  const reach = Math.max(1, Math.round(reachLeds));
  const add = (led: number) => {
    const last = feeds[feeds.length - 1].led;
    const near = joints.filter((j) => j.afterLed + 1 <= led && j.afterLed + 1 > last && led - (j.afterLed + 1) <= reach / 5);
    const joint = near.sort((a, b) => b.afterLed - a.afterLed)[0];
    const at = joint ? joint.afterLed + 1 : led;
    if (feeds.some((f) => f.led === at) || at > total) return;
    const piece = plan.pieces.find((p) => at >= p.firstLed && at <= p.lastLed);
    const along = piece ? `along the ${piece.label.charAt(0).toLowerCase()}${piece.label.slice(1)}` : 'along the strip';
    const where = joint ? (joint.corner ? CORNER_LABELS[joint.corner] : 'the stand') : at === total ? 'the end' : along;
    feeds.push({ led: at, at: joint ? joint.at : ledPoint(plan, at), where: `LED ${at}, ${where}` });
  };
  // each round adds a feed further on, or stops (a feed that lands on one there already would loop forever)
  for (let next = feeds[0].led + reach - 1; next < total; ) {
    const before = feeds.length;
    add(next);
    const last = feeds[feeds.length - 1].led;
    if (feeds.length === before || last + reach - 1 <= next) break;
    next = last + reach - 1;
  }
  const last = feeds[feeds.length - 1].led;
  if (total - last > (reach * 3) / 4) add(total);
  return feeds;
}

function planWarnings(input: PlanInput, preset: StripPreset, pieces: readonly PlanPiece[], fullWhiteA: number, supply: PlanInput['supply']): PlanWarning[] {
  const out: PlanWarning[] = [];
  if (supply && !acceptsVolts(preset, supply.volts) && supply.volts > preset.volts)
    out.push({ tone: 'bad', text: `This supply gives ${supply.volts} V, but the strip takes ${preset.volts} V: it would burn the LEDs out. Use a ${preset.volts} V supply.` });
  else if (supply && !acceptsVolts(preset, supply.volts) && supply.volts < preset.volts)
    out.push({ tone: 'bad', text: `This supply gives ${supply.volts} V, but the strip takes ${preset.volts} V: the LEDs would stay dark or flicker. Use a ${preset.volts} V supply.` });
  else if (supply && supply.amps < fullWhiteA)
    out.push({
      tone: 'warn',
      text: `Full white needs ${fullWhiteA.toFixed(1)} A and this supply gives ${supply.amps} A. The board limits the LEDs to ${(supply.amps * 0.85).toFixed(1)} A, so full white is a little dimmer.`,
    });
  const tiny = pieces.filter((p) => p.leds === 0);
  if (tiny.length) out.push({ tone: 'warn', text: `${tiny.map((p) => p.label).join(', ')}: too short for one LED at ${input.density} LEDs/m.` });
  return out;
}

function planChecklist(plan: StripPlan, supply: PlanInput['supply']): ChecklistItem[] {
  const { preset, input, pieces, joints, feeds } = plan;
  const items: ChecklistItem[] = [];
  const meters = Math.ceil((plan.stripMm * 1.1) / 100) / 10;
  const reels = Math.ceil(meters / 5);
  items.push({
    group: 'Parts',
    text: `${preset.name} strip, ${input.density} LEDs/m, at least ${meters.toFixed(1)} m (${reels === 1 ? 'one 5 m reel' : `${reels} reels of 5 m`})`,
  });
  const corners = joints.filter((j) => j.kind === 'corner');
  if (corners.length && input.corners === 'connector')
    items.push({ group: 'Parts', text: `${plural(corners.length, `L-shaped corner connector`)} for this strip (${preset.wires}-pin)` });
  const wired = corners.length && input.corners === 'wire';
  const jumpers = joints.filter((j) => j.kind === 'jumper');
  if (wired || jumpers.length) items.push({ group: 'Parts', text: `${preset.wires}-core wire for the joints, and solder or solderless connectors` });
  const s = supply ?? plan.suggestedSupply;
  items.push({
    group: 'Parts',
    text: supply
      ? `Your ${supply.volts} V, ${supply.amps} A power supply`
      : plan.suggestedSupply.count > 1
        ? capitalize(supplyText(plan.suggestedSupply))
        : `A ${s.volts} V power supply, at least ${s.amps} A (${Math.round(s.amps * s.volts)} W)`,
  });
  const extra = feeds.slice(1);
  if (extra.length) items.push({ group: 'Parts', text: `Power wire for ${plural(extra.length, 'extra power feed')}: ${wireGauge(plan.fullWhiteA / feeds.length)}, red and black` });
  items.push({ group: 'Parts', text: 'A WLED board and a USB cable that carries data (some only charge)' });

  if (input.corners === 'fold' || pieces.length < 2) {
    // one piece up to the stand's wire and one after it, or the whole run
    const runs: PlanPiece[][] = [];
    for (const p of pieces) {
      const jumper = joints.some((j) => j.kind === 'jumper' && j.afterLed === p.firstLed - 1);
      if (!runs.length || jumper) runs.push([p]);
      else runs[runs.length - 1].push(p);
    }
    if (runs.length === 1) items.push({ group: 'Cut', text: `Cut the strip after LED ${plan.totalLeds} (${roundCm(plan.stripMm)})` });
    else
      for (const run of runs) {
        const first = run[0].firstLed;
        const last = run[run.length - 1].lastLed;
        const mm = run.reduce((n, p) => n + p.cutMm, 0);
        items.push({ group: 'Cut', text: `${run[0].label} to ${run[run.length - 1].label.toLowerCase()}: one piece of ${plural(last - first + 1, 'LED')} (${roundCm(mm)}), LEDs ${first}–${last}` });
      }
  } else {
    for (const p of pieces) items.push({ group: 'Cut', text: `${p.label}: ${plural(p.leds, 'LED')} (${roundCm(p.cutMm)}), LEDs ${p.firstLed}–${p.lastLed}` });
  }
  items.push({ group: 'Cut', text: 'Cut only across the copper pads at a cut mark (usually a line or scissors symbol)' });

  items.push({ group: 'Connect', text: `Start ${START_PHRASES[input.start]}, with the arrows on the strip pointing away from the board` });
  for (const j of joints) {
    if (j.kind === 'jumper') items.push({ group: 'Connect', text: `Across the stand: ${roundCm(j.wireMm)} of ${preset.wires}-core wire from LED ${j.afterLed} to LED ${j.afterLed + 1}` });
    else if (j.corner) {
      const how = j.join === 'fold' ? 'fold the strip at 45°' : j.join === 'connector' ? 'a corner connector' : `${preset.wires} short wires`;
      items.push({ group: 'Connect', text: `${capitalize(CORNER_LABELS[j.corner])}: ${how}, LED ${j.afterLed} to LED ${j.afterLed + 1}` });
    }
  }
  items.push({ group: 'Connect', text: 'Keep the data wire from the board to LED 1 short (under 1 m)' });
  for (const f of extra) items.push({ group: 'Connect', text: `Power feed at ${f.where}: + and − from the supply to the strip’s + and −` });
  return items;
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** The joint closest to where the lights stop (a broken connection there makes every LED after it dark). */
export function jointNear(plan: Pick<StripPlan, 'joints'>, lastLit: number): PlanJoint | null {
  let best: PlanJoint | null = null;
  for (const j of plan.joints) if (!best || Math.abs(j.afterLed - lastLit) < Math.abs(best.afterLed - lastLit)) best = j;
  return best && Math.abs(best.afterLed - lastLit) <= 2 ? best : null;
}

/** Words for a joint: "the top-right corner (between LED 37 and LED 38)". */
export function jointText(j: PlanJoint): string {
  const where = j.kind === 'jumper' ? 'the wire across the stand' : j.corner ? `the ${CORNER_LABELS[j.corner]}` : 'a joint';
  return `${where} (between LED ${j.afterLed} and LED ${j.afterLed + 1})`;
}
