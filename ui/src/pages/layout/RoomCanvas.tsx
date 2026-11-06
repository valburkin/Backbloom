import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { Check, Focus, Maximize, Minus, Plus, Scan, Undo2, X } from 'lucide-react';
import {
  compileStrip,
  darkMask,
  furnitureTop,
  isGap,
  nearestOutline,
  sectionAt,
  shortWay,
  ledInUnfolded,
  pathStations,
  pieceInUnfolded,
  placeLeds,
  stripLedCount,
  toUnfolded,
  type LayoutSpec,
  type LedRect,
  type PathPoint,
  type Room,
  type Section,
  type Strip,
} from '../../layout';
import { stripColor } from '../../editor/colors';
import type { EditorAction, Selection } from '../../editor/state';
import { buttonClass, cx } from '../../components/ui';
import { Scenery } from './Scenery';
import { RoleChooser } from './RoleChooser';
import type { Mode } from '../../control/modes';
import { insertPoint, removePoint as withoutPoint, setSection } from '../../editor/sections';
import { unpinPoint } from '../../editor/strips';
import { ledColor, mappingJoints, moveJoint, roleLabel, stationColor, type MappingEnd } from '../../editor/mapping';
import { stripLedRanges } from '../../live/layoutView';
import {
  appendPoint,
  fitView,
  outlinePath,
  snapOutline,
  frontFocus,
  screenRect,
  snapAt,
  surfaceRect,
  tidy,
  viewBox,
  wholeRoom,
  zoomAt,
  type View,
} from './canvasMath';

export interface FoundLed {
  stripId: string;
  local: number;
}

interface Props {
  spec: LayoutSpec;
  selection: Selection;
  dispatch: (a: EditorAction) => void;
  /** Drawing a new path for this strip: clicks add points. */
  drawing: string | null;
  /** The drawn path (two or more points), or null when drawing was canceled. */
  onDrawn(path: PathPoint[] | null): void;
  found: FoundLed | null;
  /** The bend being matched with the real lights. */
  matching?: { stripId: string; pointIndex: number } | null;
  /** Effect names the backend runs (for parts that play one). */
  effects: readonly string[];
  /** Only what strips show can change (Customize, or a profile is edited): no moving strips, bends or the screen. */
  rolesOnly?: boolean;
  /** Show the dots on the screen's edge that adjust what the selected strip's parts follow (default: shown). */
  showMapping?: boolean;
  /** A part can be made a gap (not while a profile is edited: gaps belong to the strip). Default: unless rolesOnly. */
  allowGaps?: boolean;
  /** What a strip's sections do changed (`path` carries them); the page saves it to the layout or the profile. */
  onRoles(stripId: string, path: PathPoint[], coalesce?: string, targets?: number[]): void;
  /** The LEDs' live colors (a flat [r, g, b, ...] list in the engine's order): every strip is drawn lit with them. */
  liveColors?: ArrayLike<number> | null;
  /** What the lights show now (null: unknown), for the role chooser of a part. */
  showing?: Mode | null;
}

/** Pixels within which points snap. */
const SNAP_PX = 12;
/** Pixels a press may move and still count as a click. */
const CLICK_PX = 4;

type Drag =
  | { kind: 'pan'; x: number; y: number; view: View; moved: boolean }
  | { kind: 'point'; stripId: string; index: number; key: string }
  | { kind: 'screen'; dx: number; dy: number; key: string }
  | { kind: 'screen-size'; key: string }
  | { kind: 'joint'; stripId: string; ends: MappingEnd[]; key: string };

let dragCounter = 0;

/** What the canvas shows first: the screen, the furniture and every strip, within the front wall's view. */
function startArea(room: Room, strips: readonly Strip[]) {
  const scr = screenRect(room);
  const front = surfaceRect(room, 'front');
  const top = furnitureTop(room);
  const points = [
    { x: scr.x, y: scr.y },
    { x: scr.x + scr.w, y: scr.y + scr.h },
    ...(top ? [{ x: front.x + top.x, y: front.y + top.y }, { x: front.x + top.x + top.width, y: front.y + top.y + 0.2 }] : []),
    ...strips.flatMap((s) => s.path.map((p) => toUnfolded(room, p))),
  ];
  const b = boundsOf(points, 1.2);
  const pad = Math.max(0.25, 0.15 * Math.max(b.w, b.h));
  const area = { x: b.x - pad, y: b.y - pad, w: b.w + 2 * pad, h: b.h + 2 * pad };
  const max = frontFocus(room);
  return area.w > max.w && area.h > max.h ? max : area;
}

/** Bounding box of points, at least `min` meters on each side. */
function boundsOf(points: ReadonlyArray<{ x: number; y: number }>, min: number) {
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  const x0 = Math.min(...xs);
  const y0 = Math.min(...ys);
  const w = Math.max(min, Math.max(...xs) - x0);
  const h = Math.max(min, Math.max(...ys) - y0);
  return { x: (x0 + Math.max(...xs)) / 2 - w / 2, y: (y0 + Math.max(...ys)) / 2 - h / 2, w, h };
}

function median(xs: number[]): number {
  if (!xs.length) return 0;
  const s = xs.slice().sort((a, b) => a - b);
  return s[Math.floor(s.length / 2)];
}

// ---- strips ----

interface StripGeometry {
  leds: Array<{ x: number; y: number; dark: boolean; piece: number }>;
  pieces: Array<{ a: { x: number; y: number }; b: { x: number; y: number }; jump: boolean; gap: boolean }>;
  points: Array<{ x: number; y: number }>;
  stations: number[];
  spacing: number;
  rects: LedRect[] | null;
  /** What each piece does. */
  roles: Section[];
}

function stripGeometry(room: Room, strip: Strip): StripGeometry {
  const path = strip.path;
  const points = path.map((p) => toUnfolded(room, p));
  const pieces = path.slice(1).map((b, i) => {
    const a = path[i];
    const drawn = pieceInUnfolded(room, a, b);
    const gap = isGap(path, i);
    return drawn ? { ...drawn, jump: false, gap } : { a: points[i], b: points[i + 1], jump: true, gap };
  });
  const dark = darkMask(strip);
  const leds = path.length >= 2 ? placeLeds(room, strip).map((led) => ({ ...ledInUnfolded(room, path, led), dark: !!dark[led.index], piece: led.piece })) : [];
  const gaps: number[] = [];
  for (let i = 1; i < leds.length; i++) gaps.push(Math.hypot(leds[i].x - leds[i - 1].x, leds[i].y - leds[i - 1].y));
  let rects: LedRect[] | null = null;
  try {
    rects = compileStrip(strip, room);
  } catch {
    rects = null;
  }
  const roles = path.slice(1).map((_, i) => sectionAt(room, path, i));
  return { leds, pieces, points, stations: pathStations(room, path, stripLedCount(strip)), spacing: median(gaps.filter((g) => g > 1e-6)) || 0.02, rects, roles };
}

function Arrow({ a, b, size, color }: { a: { x: number; y: number }; b: { x: number; y: number }; size: number; color: string }) {
  const len = Math.hypot(b.x - a.x, b.y - a.y);
  if (len < size * 4) return null;
  const ang = (Math.atan2(b.y - a.y, b.x - a.x) * 180) / Math.PI;
  return (
    <path
      d={`M ${-size} ${-size * 0.8} L ${size * 0.9} 0 L ${-size} ${size * 0.8} Z`}
      transform={`translate(${(a.x + b.x) / 2} ${(a.y + b.y) / 2}) rotate(${ang})`}
      fill={color}
      pointerEvents="none"
    />
  );
}

const StripLayer = memo(function StripLayer({
  strip,
  index,
  geo,
  u,
  selected,
  selectedSection,
  dimmed,
  hoverLed,
  foundLed,
  onLedEnter,
  onLedLeave,
  onSelect,
  live,
}: {
  strip: Strip;
  index: number;
  geo: StripGeometry;
  u: number;
  selected: boolean;
  /** The selected section, drawn stronger. */
  selectedSection: number | null;
  /** Opacity of a strip that isn't selected while another one is (1: not dimmed). */
  dimmed: number;
  hoverLed: number | null;
  foundLed: number | null;
  onLedEnter(stripId: string, led: number): void;
  onLedLeave(): void;
  /** Select the strip, or a section of it once it is selected. */
  onSelect(stripId: string, section: number | null): void;
  /** Live colors and where this strip's LEDs are in them; unlit while a color is missing. */
  live?: { colors: ArrayLike<number>; first: number; length: number };
}) {
  const color = stripColor(index);
  const r = Math.min(5 * u, Math.max(1.6 * u, geo.spacing * 0.36));
  const start = geo.points[0];
  return (
    <g opacity={dimmed} style={{ transition: 'opacity 150ms' }} data-strip={strip.id}>
      {geo.pieces.map((p, i) => {
        const strong = selected && selectedSection === i;
        return (
          <line
            key={`l${i}`}
            x1={p.a.x}
            y1={p.a.y}
            x2={p.b.x}
            y2={p.b.y}
            // A gap is a thin wire; a jump between surfaces that don't touch in the drawing is dashed.
            stroke={p.gap ? '#8a93a8' : strong ? '#ffffff' : color}
            strokeOpacity={strong ? 0.7 : p.gap ? 0.6 : selected ? 0.55 : 0.35}
            strokeWidth={(p.gap ? 1 : strong ? 7 : selected ? 2.5 : 2) * u}
            strokeDasharray={p.gap ? `${1.5 * u} ${3 * u}` : p.jump ? `${4 * u} ${4 * u}` : undefined}
            strokeLinecap="round"
          />
        );
      })}
      {/* A wide invisible line, so the strip is easy to click. */}
      {geo.pieces.map((p, i) => (
        <line
          key={`h${i}`}
          x1={p.a.x}
          y1={p.a.y}
          x2={p.b.x}
          y2={p.b.y}
          stroke="transparent"
          strokeWidth={12 * u}
          className="cursor-pointer"
          onPointerDown={(e) => {
            e.stopPropagation();
            onSelect(strip.id, i);
          }}
        >
          <title>{p.gap ? `${strip.name}: no LEDs here (gap)` : `${strip.name}: part ${i + 1}`}</title>
        </line>
      ))}
      {geo.leds.map((led, i) => (
        <circle
          key={i}
          cx={led.x}
          cy={led.y}
          r={hoverLed === i ? r * 1.5 : r}
          fill={led.dark ? '#0b0d12' : live ? liveColor(live, i) : ledFill(geo.roles[led.piece], selected ? ledColor(i, geo.leds.length) : color)}
          stroke={led.dark ? '#6a7285' : hoverLed === i ? '#fff' : 'none'}
          strokeWidth={1.2 * u}
          className="cursor-pointer"
          onPointerEnter={() => onLedEnter(strip.id, i)}
          onPointerLeave={onLedLeave}
          onPointerDown={(e) => {
            e.stopPropagation();
            onSelect(strip.id, led.piece);
          }}
        >
          <title>{`${strip.name} · LED ${i + 1}${led.dark ? ' · dark' : ''}`}</title>
        </circle>
      ))}
      {geo.pieces.map((p, i) => (p.jump || p.gap ? null : <Arrow key={`a${i}`} a={p.a} b={p.b} size={4.5 * u} color={color} />))}
      {/* What parts that don't follow the screen do, once per run of parts doing the same. */}
      {geo.pieces.map((p, i) => {
        const label = roleLabel(geo.roles[i]);
        if (!label || geo.roles[i].kind === 'gap' || (i > 0 && JSON.stringify(geo.roles[i - 1]) === JSON.stringify(geo.roles[i]))) return null;
        const x = (p.a.x + p.b.x) / 2;
        const y = (p.a.y + p.b.y) / 2 - 12 * u;
        const width = (label.length * 6.2 + 14) * u;
        return (
          <g key={`r${i}`} transform={`translate(${x} ${y})`} pointerEvents="none">
            <rect x={-width / 2} y={-8 * u} width={width} height={16 * u} rx={8 * u} fill="#0b0d12" fillOpacity={0.9} stroke={color} strokeOpacity={0.6} strokeWidth={u} />
            <text textAnchor="middle" dominantBaseline="central" fontSize={10 * u} fill="#e8eaf0">
              {label}
            </text>
          </g>
        );
      })}
      {foundLed !== null && geo.leds[foundLed] && (
        <g pointerEvents="none">
          <circle cx={geo.leds[foundLed].x} cy={geo.leds[foundLed].y} r={r * 2.2} fill="none" stroke="#fff" strokeWidth={2 * u} />
          <circle cx={geo.leds[foundLed].x} cy={geo.leds[foundLed].y} r={r * 2.2} fill="none" stroke="#fff" strokeWidth={2 * u} className="led-ping" />
        </g>
      )}
      {start && (
        <g
          transform={`translate(${start.x} ${start.y})`}
          className="cursor-pointer"
          onPointerDown={(e) => {
            if (selected) return; // the handle underneath takes it
            e.stopPropagation();
            onSelect(strip.id, null);
          }}
        >
          <circle r={9 * u} fill={color} stroke="#0b0d12" strokeWidth={2.5 * u} />
          <text textAnchor="middle" dominantBaseline="central" fontSize={10.5 * u} fontWeight={700} fill="#0b0d12">
            {index + 1}
          </text>
          <title>{`${strip.name} starts here (LED 1)`}</title>
        </g>
      )}
    </g>
  );
});

/** LED `i` of a strip in the live colors, or unlit when it isn't driven or no color arrived for it. */
function liveColor(live: { colors: ArrayLike<number>; first: number; length: number }, i: number): string {
  const o = (live.first + i) * 3;
  if (i >= live.length || o + 2 >= live.colors.length) return '#1e222e';
  return `rgb(${live.colors[o]},${live.colors[o + 1]},${live.colors[o + 2]})`;
}

/** An LED's look by what its part does: its own color, a pale dot for an effect, dim when off; `screen` otherwise. */
function ledFill(role: Section | undefined, screen: string): string {
  switch (role?.kind) {
    case 'color':
      return `rgb(${role.color.join(',')})`;
    case 'effect':
      return '#e8eaf0';
    case 'none':
      return '#2a3142';
    case 'whole':
      return '#c9cfdb';
    default:
      return screen;
  }
}

// ---- the canvas ----

export function RoomCanvas({ spec, selection, dispatch, drawing, onDrawn, found, matching, effects, rolesOnly = false, showMapping = true, allowGaps, onRoles, liveColors, showing }: Props) {
  const ranges = useMemo(() => stripLedRanges(spec), [spec]);
  const gaps = allowGaps ?? !rolesOnly;
  const { room } = spec;
  const box = useRef<HTMLDivElement>(null);
  const svg = useRef<SVGSVGElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  const [view, setView] = useState<View | null>(null);
  const [hover, setHover] = useState<{ stripId: string; led: number } | null>(null);
  const [draft, setDraft] = useState<PathPoint[]>([]);
  const [cursor, setCursor] = useState<PathPoint | null>(null);
  /** Where the point being moved or drawn snapped to, shown as a ring. */
  const [snapMark, setSnapMark] = useState<{ x: number; y: number } | null>(null);
  const drag = useRef<Drag | null>(null);
  const press = useRef<{ x: number; y: number } | null>(null);

  // Track the canvas size; the first size fits the front wall.
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = () => {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) setSize({ w: r.width, h: r.height });
    };
    measure();
    if (typeof ResizeObserver !== 'function') return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const w = size.w || 800;
  const h = size.h || 500;
  useEffect(() => {
    if (!view && size.w > 0) setView(fitView(startArea(room, spec.strips), size.w, size.h));
  }, [view, size, room, spec.strips]);
  const v = view ?? fitView(startArea(room, spec.strips), w, h);
  const vb = viewBox(v, w, h);
  const u = 1 / v.scale;

  const toRoom = useCallback(
    (clientX: number, clientY: number) => {
      const r = box.current?.getBoundingClientRect();
      const px = r ? clientX - r.left : 0;
      const py = r ? clientY - r.top : 0;
      return { x: vb.x + px * u, y: vb.y + py * u, px, py };
    },
    [vb.x, vb.y, u],
  );

  // Wheel zoom (a native listener: React's is passive and can't stop the page from scrolling).
  const viewRef = useRef(v);
  viewRef.current = v;
  useEffect(() => {
    const el = svg.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      const factor = Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0015));
      setView(zoomAt(viewRef.current, factor, e.clientX - r.left, e.clientY - r.top, r.width, r.height));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const geometries = useMemo(() => spec.strips.map((s) => stripGeometry(room, s)), [spec.strips, room]);
  const selIndex = spec.strips.findIndex((s) => s.id === selection.stripId);
  const selected = selIndex >= 0 ? spec.strips[selIndex] : undefined;
  const selGeo = selIndex >= 0 ? geometries[selIndex] : undefined;

  // ---- drawing a path ----
  useEffect(() => {
    setDraft([]);
    setCursor(null);
  }, [drawing]);
  const finishDrawing = useCallback(() => {
    // A fresh path: plain points, nothing pinned, every section automatic.
    onDrawn(draft.length >= 2 ? draft.map(({ surface, x, y }) => tidy({ surface, x, y })) : null);
    setDraft([]);
    setCursor(null);
  }, [draft, onDrawn]);

  // ---- keyboard ----
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return;
      if (drawing) {
        if (e.key === 'Enter') finishDrawing();
        else if (e.key === 'Escape') {
          if (draft.length) setDraft((d) => d.slice(0, -1));
          else onDrawn(null);
        } else if (e.key === 'Backspace') setDraft((d) => d.slice(0, -1));
        return;
      }
      // (not while a profile is edited: the strip shows the profile's sections, which mustn't go into the layout)
      if ((e.key === 'Delete' || e.key === 'Backspace') && !rolesOnly && selected && selection.pointIndex !== null && selected.path.length > 2) {
        e.preventDefault();
        removePoint(selected, selection.pointIndex);
      } else if (e.key === 'Escape' && selection.pointIndex !== null) {
        dispatch({ type: 'selectPoint', index: null });
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  function removePoint(strip: Strip, index: number) {
    if (strip.path.length <= 2) return;
    dispatch({ type: 'updateStrip', stripId: strip.id, patch: { path: withoutPoint(strip.path, index) } });
    dispatch({ type: 'selectPoint', index: null });
  }

  const snapTolerance = SNAP_PX * u;

  /** Snap for a moving point of `strip` (other points of every strip, aligned with its neighbors). */
  const snapFor = (ux: number, uy: number, strip: Strip | null, index: number | null, free: boolean, fallback?: PathPoint) => {
    const others: PathPoint[] = [];
    // Not the point being moved, nor the old path of a strip being drawn again (it's hidden); the points drawn so far count.
    spec.strips.forEach((s) => s.id !== drawing && s.path.forEach((p, i) => (s !== strip || i !== index) && others.push(p)));
    if (drawing) others.push(...draft);
    const align: PathPoint[] = [];
    if (strip && index !== null) {
      if (strip.path[index - 1]) align.push(strip.path[index - 1]);
      if (strip.path[index + 1]) align.push(strip.path[index + 1]);
    } else if (draft.length) {
      align.push(draft[draft.length - 1]);
    }
    return snapAt(room, ux, uy, snapTolerance, { points: others, align, fallback: fallback?.surface, free });
  };

  // ---- pointer handling ----
  const onPointerDown = (e: ReactPointerEvent<SVGSVGElement>) => {
    if (e.button !== 0 && e.button !== 1) return;
    (e.currentTarget as Element).setPointerCapture?.(e.pointerId);
    press.current = { x: e.clientX, y: e.clientY };
    if (!drag.current) drag.current = { kind: 'pan', x: e.clientX, y: e.clientY, view: v, moved: false };
  };

  const startDrag = (e: ReactPointerEvent, d: Drag) => {
    if (e.button !== 0) return;
    e.stopPropagation();
    svg.current?.setPointerCapture?.(e.pointerId);
    press.current = { x: e.clientX, y: e.clientY };
    drag.current = d;
  };

  const onPointerMove = (e: ReactPointerEvent<SVGSVGElement>) => {
    const pos = toRoom(e.clientX, e.clientY);
    if (drawing) {
      const snap = snapFor(pos.x, pos.y, null, null, e.altKey);
      setCursor(tidy(snap.point));
      setSnapMark(snap.to ? toUnfolded(room, snap.point) : null);
    }
    const d = drag.current;
    if (!d) return;
    switch (d.kind) {
      case 'pan': {
        const dx = e.clientX - d.x;
        const dy = e.clientY - d.y;
        if (!d.moved && Math.hypot(dx, dy) < CLICK_PX) return;
        d.moved = true;
        setView({ ...d.view, cx: d.view.cx - dx / d.view.scale, cy: d.view.cy - dy / d.view.scale });
        return;
      }
      case 'point': {
        const strip = spec.strips.find((s) => s.id === d.stripId);
        if (!strip) return;
        const old = strip.path[d.index];
        const snap = snapFor(pos.x, pos.y, strip, d.index, e.altKey, old);
        setSnapMark(snap.to ? toUnfolded(room, snap.point) : null);
        const { surface, x, y } = tidy(snap.point);
        const snapped: PathPoint = { ...old, surface, x, y };
        if (snapped.surface === old.surface && snapped.x === old.x && snapped.y === old.y) return;
        const path = strip.path.map((p, i) => (i === d.index ? snapped : p));
        dispatch({ type: 'updateStrip', stripId: strip.id, patch: { path }, coalesce: d.key });
        return;
      }
      case 'joint': {
        const strip = spec.strips.find((s) => s.id === d.stripId);
        const first = d.ends[0];
        if (!strip || !first) return;
        const current = sectionAt(room, strip.path, first.index);
        if (current.kind !== 'edge') return;
        const f = surfaceRect(room, 'front');
        const raw = nearestOutline(room, pos.x - f.x, pos.y - f.y);
        // Keep moving the same way round: the turn nearest to where the dot was.
        const value = snapOutline(shortWay(current[first.end], raw), e.altKey ? 0 : 0.04);
        if (value === current[first.end]) return;
        onRoles(strip.id, moveJoint(room, strip.path, d.ends, value), d.key);
        return;
      }
      case 'screen': {
        const f = surfaceRect(room, 'front');
        const x = Math.round((pos.x - f.x - d.dx) * 1000) / 1000;
        const y = Math.round((pos.y - f.y - d.dy) * 1000) / 1000;
        dispatch({ type: 'updateRoom', patch: { screen: { ...room.screen, x, y } }, coalesce: d.key });
        return;
      }
      case 'screen-size': {
        // Bottom-right corner; the screen keeps its shape.
        const f = surfaceRect(room, 'front');
        const s = room.screen;
        const aspect = s.width / s.height;
        const width = Math.max(0.15, Math.min(room.width - s.x, pos.x - f.x - s.x, (room.height - s.y) * aspect));
        const r3 = (x: number) => Math.round(x * 1000) / 1000;
        dispatch({ type: 'updateRoom', patch: { screen: { ...s, width: r3(width), height: r3(width / aspect) } }, coalesce: d.key });
        return;
      }
    }
  };

  const onPointerUp = (e: ReactPointerEvent<SVGSVGElement>) => {
    const d = drag.current;
    drag.current = null;
    if (!drawing) setSnapMark(null);
    const p = press.current;
    press.current = null;
    const click = p && Math.hypot(e.clientX - p.x, e.clientY - p.y) < CLICK_PX;
    if (d?.kind === 'pan' && click) {
      if (drawing) {
        const pos = toRoom(e.clientX, e.clientY);
        const next = tidy(snapFor(pos.x, pos.y, null, null, e.altKey).point);
        const last = draft[draft.length - 1];
        if (last && last.surface === next.surface && Math.hypot(last.x - next.x, last.y - next.y) < 1e-3) return;
        setDraft((dr) => appendPoint(room, dr, next).map(tidy));
      } else if (selection.pointIndex !== null) {
        dispatch({ type: 'selectPoint', index: null });
      } else if (selection.stripId) {
        dispatch({ type: 'selectStrip', stripId: null });
      }
    }
  };

  const selectedId = selection.stripId;
  const onSelectStrip = useCallback(
    (stripId: string, section: number | null) => {
      if (stripId === selectedId && section !== null) dispatch({ type: 'selectSection', index: section });
      else dispatch({ type: 'selectStrip', stripId });
    },
    [dispatch, selectedId],
  );
  const onLedEnter = useCallback((stripId: string, led: number) => setHover({ stripId, led }), []);
  const onLedLeave = useCallback(() => setHover(null), []);

  const scr = screenRect(room);
  const hoverRect = hover && selected && hover.stripId === selected.id ? selGeo?.rects?.[hover.led] : undefined;
  const hoverLedPos = hover && selected && hover.stripId === selected.id ? selGeo?.leds[hover.led] : undefined;
  const draftPoints = cursor && drawing ? appendPoint(room, draft, cursor) : draft;

  const buttons: Array<{ label: string; icon: ReactNode; onClick(): void; disabled?: boolean }> = [
    {
      label: 'Zoom to the selected strip',
      icon: <Focus size={15} />,
      disabled: !selGeo || selGeo.points.length === 0,
      onClick: () => selGeo && setView(fitView(boundsOf([...selGeo.points, ...selGeo.leds], 0.15), w, h, 48)),
    },
    { label: 'Show the front wall', icon: <Scan size={15} />, onClick: () => setView(fitView(frontFocus(room), w, h)) },
    { label: 'Show the whole room', icon: <Maximize size={15} />, onClick: () => setView(fitView(wholeRoom(room), w, h)) },
    { label: 'Zoom out', icon: <Minus size={15} />, onClick: () => setView(zoomAt(v, 1 / 1.4, w / 2, h / 2, w, h)) },
    { label: 'Zoom in', icon: <Plus size={15} />, onClick: () => setView(zoomAt(v, 1.4, w / 2, h / 2, w, h)) },
  ];

  return (
    <div ref={box} className={cx('relative h-full w-full touch-none select-none', drawing && 'cursor-crosshair')}>
      <svg
        ref={svg}
        width="100%"
        height="100%"
        viewBox={`${vb.x} ${vb.y} ${vb.w} ${vb.h}`}
        className="block"
        role="application"
        aria-label={`Room with ${spec.strips.length} LED strip${spec.strips.length === 1 ? '' : 's'}${selected ? `; ${selected.name} selected` : ''}`}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={() => {
          drag.current = null;
          press.current = null;
        }}
        onPointerLeave={() => drawing && setCursor(null)}
        onDoubleClick={() => drawing && finishDrawing()}
      >
        <rect x={vb.x} y={vb.y} width={vb.w} height={vb.h} fill="#0a0c11" />
        <Scenery room={room} u={u} />

        {/* The screen: drag to move, the corner handle to resize. */}
        <g>
          <rect
            x={scr.x}
            y={scr.y}
            width={scr.w}
            height={scr.h}
            rx={0.008}
            fill="#0b0e15"
            stroke="#4a5470"
            strokeWidth={1.5 * u}
            className={drawing || rolesOnly ? undefined : 'cursor-move'}
            onPointerDown={(e) => {
              if (drawing || rolesOnly) return;
              const pos = toRoom(e.clientX, e.clientY);
              startDrag(e, { kind: 'screen', dx: pos.x - scr.x, dy: pos.y - scr.y, key: `screen-${++dragCounter}` });
            }}
          >
            <title>Your screen. Drag to move it.</title>
          </rect>
          {selGeo?.rects && !drawing && !liveColors && (
            <g pointerEvents="none">
              {/* What each LED repeats, in the LED's own rainbow color. */}
              {selGeo.rects.map((r, i) =>
                r.disabled ? null : (
                  <rect
                    key={i}
                    x={scr.x + r.hmin * scr.w}
                    y={scr.y + r.vmin * scr.h}
                    width={Math.max(0, (r.hmax - r.hmin) * scr.w)}
                    height={Math.max(0, (r.vmax - r.vmin) * scr.h)}
                    fill={ledColor(i, selGeo.rects!.length)}
                    fillOpacity={0.28}
                  />
                ),
              )}
            </g>
          )}
          {!selected && (
            <text x={scr.x + scr.w / 2} y={scr.y + scr.h / 2} textAnchor="middle" dominantBaseline="central" fontSize={11 * u} fill="#5b6478" pointerEvents="none">
              SCREEN
            </text>
          )}
          {!drawing && !rolesOnly && (
            <rect
              x={scr.x + scr.w - 5 * u}
              y={scr.y + scr.h - 5 * u}
              width={10 * u}
              height={10 * u}
              fill="#4a5470"
              className="cursor-nwse-resize"
              onPointerDown={(e) => startDrag(e, { kind: 'screen-size', key: `screen-size-${++dragCounter}` })}
            >
              <title>Drag to resize the screen</title>
            </rect>
          )}
        </g>

        {/* Strips; the selected one on top. */}
        {spec.strips
          .map((s, i) => ({ s, i }))
          .filter(({ s }) => s.id !== drawing)
          .sort((a, b) => Number(a.s.id === selection.stripId) - Number(b.s.id === selection.stripId))
          .map(({ s, i }) => (
            <StripLayer
              key={s.id}
              strip={s}
              index={i}
              geo={geometries[i]}
              u={u}
              selected={s.id === selection.stripId}
              selectedSection={s.id === selection.stripId ? selection.sectionIndex : null}
              // With live colors the others are only slightly dimmed, so the room still reads as it is lit.
              dimmed={selection.stripId && s.id !== selection.stripId ? (liveColors ? 0.75 : 0.4) : 1}
              hoverLed={hover && hover.stripId === s.id ? hover.led : null}
              foundLed={found && found.stripId === s.id ? found.local : null}
              onLedEnter={onLedEnter}
              onLedLeave={onLedLeave}
              onSelect={onSelectStrip}
              live={liveColors && ranges.has(s.id) ? { colors: liveColors, ...ranges.get(s.id)! } : undefined}
            />
          ))}

        {/* What the hovered LED shows. */}
        {hoverRect && hoverLedPos && !hoverRect.disabled && (
          <g pointerEvents="none">
            <line
              x1={hoverLedPos.x}
              y1={hoverLedPos.y}
              x2={scr.x + ((hoverRect.hmin + hoverRect.hmax) / 2) * scr.w}
              y2={scr.y + ((hoverRect.vmin + hoverRect.vmax) / 2) * scr.h}
              stroke="#fff"
              strokeOpacity={0.5}
              strokeWidth={u}
              strokeDasharray={`${3 * u} ${3 * u}`}
            />
            <rect
              x={scr.x + hoverRect.hmin * scr.w}
              y={scr.y + hoverRect.vmin * scr.h}
              width={Math.max(0, (hoverRect.hmax - hoverRect.hmin) * scr.w)}
              height={Math.max(0, (hoverRect.vmax - hoverRect.vmin) * scr.h)}
              fill="#fff"
              fillOpacity={0.3}
              stroke="#fff"
              strokeWidth={u}
            />
          </g>
        )}

        {/* Dots on the screen's edge where the strip's parts start and end: drag them to change what the parts repeat. */}
        {selected && !drawing && showMapping &&
          mappingJoints(room, selected).map((j, k) => {
            const p = outlinePath(room, j.pos, j.pos, 12 * u)[0];
            return (
              <g
                key={k}
                transform={`translate(${p.x} ${p.y})`}
                className="cursor-grab"
                onPointerDown={(e) => startDrag(e, { kind: 'joint', stripId: selected.id, ends: j.ends, key: `joint-${++dragCounter}` })}
              >
                <circle r={11 * u} fill="transparent" />
                <circle r={6.5 * u} fill={stationColor(j.station, stripLedCount(selected))} stroke="#ffffff" strokeWidth={2 * u} />
                <title>Drag along the screen’s edge to change which part of the screen these LEDs follow</title>
              </g>
            );
          })}

        {snapMark && <circle cx={snapMark.x} cy={snapMark.y} r={10 * u} fill="none" stroke="#fbbf24" strokeWidth={2 * u} pointerEvents="none" />}

        {/* Handles of the selected strip. */}
        {selected && selGeo && !drawing && !rolesOnly && (
          <g>
            {selGeo.pieces.map((p, i) => {
              const mx = (p.a.x + p.b.x) / 2;
              const my = (p.a.y + p.b.y) / 2;
              if (p.jump || Math.hypot(p.b.x - p.a.x, p.b.y - p.a.y) < 44 * u) return null;
              return (
                <g
                  key={`m${i}`}
                  transform={`translate(${mx} ${my})`}
                  className="cursor-copy"
                  onPointerDown={(e) => {
                    // Insert a bend here and drag it.
                    const a = selected.path[i];
                    const pos = toRoom(e.clientX, e.clientY);
                    const point = tidy(snapAt(room, pos.x, pos.y, 0, { fallback: a.surface }).point);
                    const path = insertPoint(room, selected.path, i, point);
                    const key = `insert-${++dragCounter}`;
                    dispatch({ type: 'updateStrip', stripId: selected.id, patch: { path }, coalesce: key });
                    dispatch({ type: 'selectPoint', index: i + 1 });
                    startDrag(e, { kind: 'point', stripId: selected.id, index: i + 1, key });
                  }}
                >
                  <circle r={6 * u} fill="#0b0d12" fillOpacity={0.85} stroke="#9ba3b5" strokeWidth={u} />
                  <path d={`M ${-3 * u} 0 H ${3 * u} M 0 ${-3 * u} V ${3 * u}`} stroke="#e8eaf0" strokeWidth={1.4 * u} />
                  <title>Drag to add a bend here</title>
                </g>
              );
            })}
            {selGeo.points.map((p, i) => {
              const isSel = selection.pointIndex === i;
              const isMatch = matching && matching.stripId === selected.id && matching.pointIndex === i;
              const at = selected.path[i].at;
              return (
                <g
                  key={`p${i}`}
                  transform={`translate(${p.x} ${p.y})`}
                  className="cursor-grab"
                  onPointerDown={(e) => {
                    dispatch({ type: 'selectPoint', index: i });
                    startDrag(e, { kind: 'point', stripId: selected.id, index: i, key: `point-${++dragCounter}` });
                  }}
                  onContextMenu={(e) => {
                    e.preventDefault();
                    // A matched bend forgets its match first; one without a match is removed.
                    if (selected.path[i].at !== undefined) dispatch({ type: 'updateStrip', stripId: selected.id, patch: { path: unpinPoint(selected.path, i) } });
                    else removePoint(selected, i);
                  }}
                >
                  {isMatch && <circle r={14 * u} fill="none" stroke="#fbbf24" strokeWidth={2 * u} className="led-ping" />}
                  <circle
                    r={(isSel ? 7.5 : 6) * u}
                    fill={isSel ? '#a89cff' : '#0b0d12'}
                    stroke={isMatch ? '#fbbf24' : at !== undefined ? '#34d399' : '#e8eaf0'}
                    strokeWidth={2 * u}
                  />
                  {at !== undefined && (
                    // A pin mark: this bend is matched to an LED.
                    <path
                      d={`M ${4 * u} ${-11 * u} l ${4 * u} ${4 * u} l ${-2 * u} ${2 * u} l ${-3 * u} ${-1 * u} l ${-3 * u} ${3 * u} l ${-1 * u} ${-1 * u} l ${3 * u} ${-3 * u} l ${-1 * u} ${-3 * u} Z`}
                      fill="#34d399"
                      pointerEvents="none"
                    />
                  )}
                  {at !== undefined && (isSel || isMatch) && (
                    <text y={-12 * u} textAnchor="middle" fontSize={10 * u} fill="#34d399" fontWeight={600} stroke="#0b0d12" strokeWidth={3 * u} paintOrder="stroke">
                      {`LED ${Math.floor(at) + (at % 1 === 0 ? 0 : 1)}`}
                    </text>
                  )}
                  <title>
                    {i === 0 ? 'Start of the strip' : i === selected.path.length - 1 ? 'End of the strip' : `Bend ${i}`}
                    {at !== undefined
                      ? ` · matched to LED ${Math.floor(at) + 1}. Drag to move; right-click to forget the match.`
                      : ` · about LED ${Math.max(1, Math.round(selGeo.stations[i] ?? 0))}. Drag to move; right-click or Delete to remove.`}
                  </title>
                </g>
              );
            })}
          </g>
        )}

        {/* The path being drawn. */}
        {drawing && draftPoints.length > 0 && (
          <g pointerEvents="none">
            {draftPoints.slice(1).map((b, i) => {
              const a = draftPoints[i];
              const drawn = pieceInUnfolded(room, a, b);
              const pa = drawn?.a ?? toUnfolded(room, a);
              const pb = drawn?.b ?? toUnfolded(room, b);
              return (
                <line
                  key={i}
                  x1={pa.x}
                  y1={pa.y}
                  x2={pb.x}
                  y2={pb.y}
                  stroke="#a89cff"
                  strokeWidth={2.5 * u}
                  strokeDasharray={!drawn || i >= draft.length - 1 ? `${5 * u} ${4 * u}` : undefined}
                />
              );
            })}
            {draftPoints.map((p, i) => {
              const q = toUnfolded(room, p);
              return <circle key={i} cx={q.x} cy={q.y} r={(i === 0 ? 6 : 4.5) * u} fill={i === 0 ? '#a89cff' : '#0b0d12'} stroke="#a89cff" strokeWidth={2 * u} />;
            })}
          </g>
        )}
      </svg>

      {drawing && (
        <div className="absolute bottom-3 left-1/2 flex -translate-x-1/2 items-center gap-2 rounded-xl border border-line-strong bg-surface/95 p-1.5 pl-3 text-[13px] shadow-xl backdrop-blur">
          <span className="text-ink-muted">
            {draft.length === 0 ? 'Click where the strip starts' : 'Click at each bend'}
          </span>
          <button
            type="button"
            disabled={!draft.length}
            onClick={() => setDraft((d) => d.slice(0, -1))}
            className="inline-flex h-7 items-center gap-1.5 rounded-md px-2 text-ink-muted hover:bg-white/[0.06] hover:text-ink disabled:opacity-40"
          >
            <Undo2 size={14} /> Undo
          </button>
          <button
            type="button"
            disabled={draft.length < 2}
            onClick={finishDrawing}
            className={buttonClass('primary', 'xs')}
          >
            <Check size={14} /> Finish
          </button>
        </div>
      )}

      {/* What the clicked part of the strip does: the exceptions to "the screen next to it". */}
      {/* (not on a gap while gaps can't be changed: they belong to the strip) */}
      {selected && selGeo && selection.sectionIndex !== null && !drawing && selGeo.pieces[selection.sectionIndex] && !(!gaps && selGeo.pieces[selection.sectionIndex].gap) && (() => {
        const i = selection.sectionIndex;
        const piece = selGeo.pieces[i];
        const left = ((piece.a.x + piece.b.x) / 2 - vb.x) / u;
        const top = ((piece.a.y + piece.b.y) / 2 - vb.y) / u;
        const own = selected.path[i].next;
        return (
          <div
            className="absolute z-10 flex -translate-x-1/2 items-start gap-1 rounded-xl border border-line-strong bg-surface/95 p-1 text-xs shadow-xl backdrop-blur"
            style={{ left: Math.min(Math.max(left, 220), w - 220), top: top < 110 ? top + 24 : top - (own?.kind === 'none' || own?.kind === 'gap' ? 52 : 92) }}
            onPointerDown={(e) => e.stopPropagation()}
          >
            <RoleChooser
              value={own?.kind === 'edge' ? undefined : own}
              effects={effects}
              withGap={gaps}
              compact
              showing={showing}
              onChange={(section) => onRoles(selected.id, setSection(selected.path, i, section), undefined, [i])}
            />
            <button
              type="button"
              aria-label="Close"
              onClick={() => dispatch({ type: 'selectSection', index: null })}
              className="inline-flex h-7 w-7 items-center justify-center rounded-md text-ink-muted hover:bg-white/[0.06] hover:text-ink"
            >
              <X size={14} />
            </button>
          </div>
        );
      })()}

      <div className="absolute right-3 top-3 flex gap-1 rounded-lg border border-line bg-surface/90 p-1 backdrop-blur">
        {buttons.map((b) => (
          <button
            key={b.label}
            type="button"
            title={b.label}
            aria-label={b.label}
            onClick={b.onClick}
            disabled={b.disabled}
            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-ink-muted hover:bg-white/[0.06] hover:text-ink disabled:opacity-30 disabled:hover:bg-transparent"
          >
            {b.icon}
          </button>
        ))}
      </div>
    </div>
  );
}
