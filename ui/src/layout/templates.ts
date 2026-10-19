import { DEFAULT_BLEND } from './compile';
import type { Area, Edge, EdgeSegment, Segment } from './types';

export type TemplateValue = number | string | boolean;

export interface TemplateParam {
  key: string;
  label: string;
  type: 'number' | 'select' | 'boolean';
  default: TemplateValue;
  min?: number;
  max?: number;
  /** Suggested increment for number inputs (counts use 1). */
  step?: number;
  options?: { value: string; label: string }[];
  help?: string;
}

export interface LayoutTemplate {
  id: string;
  name: string;
  description: string;
  params: TemplateParam[];
  build(values: Record<string, TemplateValue>): Segment[];
}

// ---------------------------------------------------------------------------
// Aspect-ratio helper

/** Common screen shapes for the "I have N LEDs" helper. */
export const ASPECT_PRESETS: { label: string; w: number; h: number }[] = [
  { label: '16:9', w: 16, h: 9 },
  { label: '16:10', w: 16, h: 10 },
  { label: '21:9', w: 21, h: 9 },
  { label: '32:9', w: 32, h: 9 },
  { label: '4:3', w: 4, h: 3 },
];

const ALL_SIDES: Edge[] = ['top', 'right', 'bottom', 'left'];

/**
 * Split a total LED count across screen sides in proportion to their length
 * (top/bottom = aspectW, left/right = aspectH). Uses largest-remainder rounding so the
 * result always adds up to `total`; sides not listed get 0.
 */
export function splitByAspect(
  total: number,
  aspectW: number,
  aspectH: number,
  sides: Edge[] = ALL_SIDES,
): Record<Edge, number> {
  if (!(aspectW > 0) || !(aspectH > 0)) throw new RangeError('Aspect ratio must be positive.');
  const result: Record<Edge, number> = { top: 0, right: 0, bottom: 0, left: 0 };
  const used = ALL_SIDES.filter((s) => sides.includes(s));
  const n = Number.isFinite(total) ? Math.max(0, Math.floor(total)) : 0;
  if (used.length === 0 || n === 0) return result;

  const len = (s: Edge) => (s === 'top' || s === 'bottom' ? aspectW : aspectH);
  const perimeter = used.reduce((t, s) => t + len(s), 0);
  const exact = used.map((s) => ({ s, x: (n * len(s)) / perimeter }));
  for (const { s, x } of exact) result[s] = Math.floor(x);
  let left = n - used.reduce((t, s) => t + result[s], 0);
  // Ties keep side order (top, right, bottom, left), so an odd extra LED lands on top/right.
  const byRemainder = [...exact].sort((a, b) => b.x - Math.floor(b.x) - (a.x - Math.floor(a.x)));
  for (let i = 0; left > 0; i = (i + 1) % byRemainder.length, left--) result[byRemainder[i].s]++;
  return result;
}

// ---------------------------------------------------------------------------
// Param helpers

type Values = Record<string, TemplateValue>;

function num(params: TemplateParam[], values: Values, key: string): number {
  const p = params.find((x) => x.key === key)!;
  const raw = values[key];
  let v = typeof raw === 'number' ? raw : typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : NaN;
  if (!Number.isFinite(v)) v = p.default as number;
  if (p.step === 1) v = Math.round(v);
  if (p.min !== undefined) v = Math.max(p.min, v);
  if (p.max !== undefined) v = Math.min(p.max, v);
  return v;
}

function pick(params: TemplateParam[], values: Values, key: string): string {
  const p = params.find((x) => x.key === key)!;
  const raw = values[key];
  return p.options?.some((o) => o.value === raw) ? (raw as string) : (p.default as string);
}

const r6 = (x: number) => Math.round(x * 1e6) / 1e6 || 0; // no -0

function edgeRun(edge: Edge, count: number, from: number, to: number, depth: number, blend: number): EdgeSegment {
  return { kind: 'edge', count, edge, from: r6(from), to: r6(to), depth, blend };
}

const count = (key: string, label: string, def: number, help?: string): TemplateParam => ({
  key,
  label,
  type: 'number',
  default: def,
  min: 0,
  max: 1000,
  step: 1,
  help,
});

const depthParam = (def: number): TemplateParam => ({
  key: 'depth',
  label: 'Sampling depth',
  type: 'number',
  default: def,
  min: 0.01,
  max: 1,
  step: 0.01,
  help: 'How far into the picture each LED looks, as a fraction of the screen (0.1 = 10%).',
});

const blendParam = (def: number): TemplateParam => ({
  key: 'blend',
  label: 'Smoothing',
  type: 'number',
  default: def,
  min: 0.5,
  max: 10,
  step: 0.1,
  help: 'How much neighboring LEDs overlap. 1 = each LED sees only its own slice; higher = softer.',
});

// ---------------------------------------------------------------------------
// "Around the screen"

type Slot = { kind: 'led'; edge: Edge; i: number; n: number } | { kind: 'gap' };

/** Native clockwise direction of each side (as seen from the front): [from, to]. */
const CW_SPAN: Record<Edge, [number, number]> = {
  top: [0, 1],
  right: [0, 1],
  bottom: [1, 0],
  left: [1, 0],
};

const RING_PARAMS: TemplateParam[] = [
  count('top', 'Top LEDs', 36),
  count('right', 'Right LEDs', 20),
  count('bottom', 'Bottom LEDs', 36, 'All LEDs along the bottom, including any hidden behind a stand.'),
  count('left', 'Left LEDs', 20),
  {
    key: 'start',
    label: 'First LED',
    type: 'select',
    default: 'bottom-center',
    options: [
      { value: 'bottom-center', label: 'Bottom center' },
      { value: 'bottom-left', label: 'Bottom-left corner' },
      { value: 'bottom-right', label: 'Bottom-right corner' },
      { value: 'top-left', label: 'Top-left corner' },
      { value: 'top-right', label: 'Top-right corner' },
      { value: 'top-center', label: 'Top center' },
    ],
    help: 'Where the strip is connected to the controller.',
  },
  {
    key: 'direction',
    label: 'Direction',
    type: 'select',
    default: 'clockwise',
    options: [
      { value: 'clockwise', label: 'Clockwise' },
      { value: 'counterclockwise', label: 'Counterclockwise' },
    ],
    help: 'As seen from the front of the screen.',
  },
  count('gap', 'Stand gap (LEDs)', 0, 'LEDs in the middle of the bottom that sit behind a stand.'),
  {
    key: 'gapMode',
    label: 'Stand gap LEDs',
    type: 'select',
    default: 'off',
    options: [
      { value: 'off', label: 'Stay dark' },
      { value: 'fixed', label: 'Show the area above the stand' },
    ],
  },
  depthParam(0.1),
  blendParam(DEFAULT_BLEND),
];

function buildRing(values: Values): Segment[] {
  const P = RING_PARAMS;
  const n: Record<Edge, number> = {
    top: num(P, values, 'top'),
    right: num(P, values, 'right'),
    bottom: num(P, values, 'bottom'),
    left: num(P, values, 'left'),
  };
  const gap = Math.min(num(P, values, 'gap'), n.bottom);
  const gapMode = pick(P, values, 'gapMode');
  const start = pick(P, values, 'start');
  const ccw = pick(P, values, 'direction') === 'counterclockwise';
  const depth = num(P, values, 'depth');
  const blend = num(P, values, 'blend');

  // Every LED in clockwise order, starting at the top-left corner.
  const g0 = Math.floor((n.bottom - gap) / 2);
  const slots: Slot[] = [];
  for (const edge of ALL_SIDES) {
    for (let i = 0; i < n[edge]; i++) {
      const inGap = edge === 'bottom' && i >= g0 && i < g0 + gap;
      slots.push(inGap ? { kind: 'gap' } : { kind: 'led', edge, i, n: n[edge] });
    }
  }
  if (slots.length === 0) return [];

  // Rotate so the first LED sits at the start point; counterclockwise walks back from there.
  const startIndex: Record<string, number> = {
    'top-left': 0,
    'top-center': Math.floor(n.top / 2),
    'top-right': n.top,
    'bottom-right': n.top + n.right,
    'bottom-center': n.top + n.right + Math.floor(n.bottom / 2),
    'bottom-left': n.top + n.right + n.bottom,
  };
  const r = startIndex[start] % slots.length;
  const rotated = [...slots.slice(r), ...slots.slice(0, r)];
  const order = ccw ? rotated.reverse() : rotated;

  const gapArea: Area = {
    hmin: r6(1 - (g0 + gap) / Math.max(1, n.bottom)),
    hmax: r6(1 - g0 / Math.max(1, n.bottom)),
    vmin: r6(1 - depth),
    vmax: 1,
  };
  const pos = (s: Extract<Slot, { kind: 'led' }>, i: number) => {
    const [f, t] = CW_SPAN[s.edge];
    return f + (i * (t - f)) / s.n;
  };

  // Group consecutive slots into segments.
  const out: Segment[] = [];
  for (let k = 0; k < order.length; ) {
    const s = order[k];
    let j = k + 1;
    if (s.kind === 'gap') {
      while (j < order.length && order[j].kind === 'gap') j++;
      out.push(gapMode === 'fixed' ? { kind: 'fixed', count: j - k, area: gapArea } : { kind: 'off', count: j - k });
    } else {
      const step = ccw ? -1 : 1;
      while (j < order.length) {
        const x = order[j];
        if (x.kind !== 'led' || x.edge !== s.edge || x.i !== s.i + (j - k) * step) break;
        j++;
      }
      const lastI = s.i + (j - k - 1) * step;
      const [from, to] = ccw ? [pos(s, s.i + 1), pos(s, lastI)] : [pos(s, s.i), pos(s, lastI + 1)];
      out.push(edgeRun(s.edge, j - k, from, to, depth, blend));
    }
    k = j;
  }
  return out;
}

// ---------------------------------------------------------------------------
// "Top and sides"

const TOP_SIDES_PARAMS: TemplateParam[] = [
  count('left', 'Left LEDs', 20),
  count('top', 'Top LEDs', 36),
  count('right', 'Right LEDs', 20),
  {
    key: 'start',
    label: 'First LED',
    type: 'select',
    default: 'bottom-left',
    options: [
      { value: 'bottom-left', label: 'Bottom of the left side' },
      { value: 'bottom-right', label: 'Bottom of the right side' },
    ],
    help: 'The strip runs up that side, across the top, and down the other side.',
  },
  depthParam(0.1),
  blendParam(DEFAULT_BLEND),
];

function buildTopSides(values: Values): Segment[] {
  const P = TOP_SIDES_PARAMS;
  const depth = num(P, values, 'depth');
  const blend = num(P, values, 'blend');
  const left = num(P, values, 'left');
  const top = num(P, values, 'top');
  const right = num(P, values, 'right');
  const runs: EdgeSegment[] =
    pick(P, values, 'start') === 'bottom-left'
      ? [
          edgeRun('left', left, 1, 0, depth, blend),
          edgeRun('top', top, 0, 1, depth, blend),
          edgeRun('right', right, 0, 1, depth, blend),
        ]
      : [
          edgeRun('right', right, 1, 0, depth, blend),
          edgeRun('top', top, 1, 0, depth, blend),
          edgeRun('left', left, 0, 1, depth, blend),
        ];
  return runs.filter((s) => s.count > 0);
}

// ---------------------------------------------------------------------------
// Single runs: bottom underglow, light bar above

const horizontalDirection: TemplateParam = {
  key: 'direction',
  label: 'Direction',
  type: 'select',
  default: 'left-to-right',
  options: [
    { value: 'left-to-right', label: 'Left to right' },
    { value: 'right-to-left', label: 'Right to left' },
  ],
  help: 'Which way the strip runs from its first LED, as seen from the front.',
};

function singleRun(edge: Edge, params: TemplateParam[], values: Values): Segment[] {
  const n = num(params, values, 'count');
  if (n === 0) return [];
  const ltr = pick(params, values, 'direction') === 'left-to-right';
  return [edgeRun(edge, n, ltr ? 0 : 1, ltr ? 1 : 0, num(params, values, 'depth'), num(params, values, 'blend'))];
}

const BOTTOM_PARAMS: TemplateParam[] = [
  count('count', 'LEDs', 40),
  horizontalDirection,
  depthParam(0.15),
  blendParam(1.5),
];

const LIGHT_BAR_PARAMS: TemplateParam[] = [
  count('count', 'LEDs', 60),
  horizontalDirection,
  depthParam(0.25),
  blendParam(5),
];

// ---------------------------------------------------------------------------
// "Two light bars"

const verticalDirection = (key: string, label: string): TemplateParam => ({
  key,
  label,
  type: 'select',
  default: 'bottom-to-top',
  options: [
    { value: 'bottom-to-top', label: 'Bottom to top' },
    { value: 'top-to-bottom', label: 'Top to bottom' },
  ],
});

const TWO_BARS_PARAMS: TemplateParam[] = [
  count('count', 'LEDs per bar', 20),
  {
    key: 'order',
    label: 'First bar',
    type: 'select',
    default: 'left-first',
    options: [
      { value: 'left-first', label: 'Left bar first' },
      { value: 'right-first', label: 'Right bar first' },
    ],
  },
  verticalDirection('leftDirection', 'Left bar runs'),
  verticalDirection('rightDirection', 'Right bar runs'),
  depthParam(0.2),
  blendParam(2),
];

function buildTwoBars(values: Values): Segment[] {
  const P = TWO_BARS_PARAMS;
  const n = num(P, values, 'count');
  if (n === 0) return [];
  const depth = num(P, values, 'depth');
  const blend = num(P, values, 'blend');
  const bar = (edge: 'left' | 'right', dirKey: string) => {
    const up = pick(P, values, dirKey) === 'bottom-to-top';
    return edgeRun(edge, n, up ? 1 : 0, up ? 0 : 1, depth, blend);
  };
  const left = bar('left', 'leftDirection');
  const right = bar('right', 'rightDirection');
  return pick(P, values, 'order') === 'left-first' ? [left, right] : [right, left];
}

// ---------------------------------------------------------------------------

export const TEMPLATES: LayoutTemplate[] = [
  {
    id: 'around-screen',
    name: 'Around the screen',
    description: 'A full ring behind a monitor or TV, with an optional gap for the stand.',
    params: RING_PARAMS,
    build: buildRing,
  },
  {
    id: 'top-and-sides',
    name: 'Top and sides',
    description: 'Up one side, across the top, down the other. Common on monitors on a stand and wall-mounted TVs.',
    params: TOP_SIDES_PARAMS,
    build: buildTopSides,
  },
  {
    id: 'bottom-only',
    name: 'Bottom only / desk underglow',
    description: 'A single strip along the bottom edge, e.g. under the monitor or the desk.',
    params: BOTTOM_PARAMS,
    build: (v) => singleRun('bottom', BOTTOM_PARAMS, v),
  },
  {
    id: 'light-bar-above',
    name: 'Light bar above (ceiling, shelf)',
    description: 'One long run mirroring the top of the picture, with wide blending for a soft wash.',
    params: LIGHT_BAR_PARAMS,
    build: (v) => singleRun('top', LIGHT_BAR_PARAMS, v),
  },
  {
    id: 'two-light-bars',
    name: 'Two light bars (left & right of screen)',
    description: 'Two vertical bars beside the screen, chained on one output.',
    params: TWO_BARS_PARAMS,
    build: buildTwoBars,
  },
];

/** Default value of every param of a template, ready to pass to build(). */
export function templateDefaults(template: LayoutTemplate): Record<string, TemplateValue> {
  return Object.fromEntries(template.params.map((p) => [p.key, p.default]));
}

/** Look up a template by id. */
export function templateById(id: string): LayoutTemplate | undefined {
  return TEMPLATES.find((t) => t.id === id);
}
