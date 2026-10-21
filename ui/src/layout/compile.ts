import { autoSection, outlineEdge, placeLeds, sectionAt, type PlacedLed } from './room';
import type { CompiledController, CompiledZone, LayoutSpec, LedRect, OutlinePosition, Room, Section, Strip, ZoneRole } from './types';

/** Default along-edge sampling width, as a multiple of LED spacing. */
export const DEFAULT_BLEND = 1.2;

/** Default sampling depth into the picture, as a fraction of the screen's height. */
export const DEFAULT_DEPTH = 0.1;

/** Bounds of an LED's half-width along the screen edge (fractions of the edge). */
const MIN_HALF = 0.01;
const MAX_HALF = 0.5;

/**
 * Rect emitted for a dark LED. The engine skips sampling for disabled LEDs entirely
 * (ImageColorAveraging), and LedString accepts a zero-size rect, so a centered point is safe.
 */
export const DISABLED_RECT: Readonly<LedRect> = Object.freeze({
  hmin: 0.5,
  hmax: 0.5,
  vmin: 0.5,
  vmax: 0.5,
  group: 0,
  disabled: true,
});

/** Number of LEDs on a strip. Invalid counts (negative, NaN) give none; fractions are floored. */
export function stripLedCount(strip: Pick<Strip, 'ledCount'>): number {
  const n = strip.ledCount;
  return Number.isFinite(n) ? Math.max(0, Math.floor(n)) : 0;
}

/** Clamp to [0, 1] and round to 4 decimals (the precision the engine config is stored with). */
function coord(x: number): number {
  if (!Number.isFinite(x)) return 0;
  return Math.round(Math.min(1, Math.max(0, x)) * 1e4) / 1e4;
}

function rect(hmin: number, hmax: number, vmin: number, vmax: number): LedRect {
  return { hmin: coord(hmin), hmax: coord(hmax), vmin: coord(vmin), vmax: coord(vmax), group: 0 };
}

/** True for LEDs in one of the strip's dark ranges. */
export function darkMask(strip: Pick<Strip, 'dark' | 'ledCount'>): boolean[] {
  const n = stripLedCount(strip);
  const mask = new Array<boolean>(n).fill(false);
  for (const r of strip.dark ?? []) {
    const start = Math.max(0, Math.floor(r.start));
    const end = Math.min(n, start + Math.max(0, Math.floor(r.count)));
    for (let i = start; i < end; i++) mask[i] = true;
  }
  return mask;
}

// `depth` is in screen heights; on the left and right edges it reaches `side` of the width (the same distance).
function edgeRect(p: OutlinePosition, half: number, depth: number, side: number): LedRect {
  const { edge, pos } = outlineEdge(p);
  const a0 = pos - half;
  const a1 = pos + half;
  switch (edge) {
    case 'top':
      return rect(a0, a1, 0, depth);
    case 'bottom':
      return rect(a0, a1, 1 - depth, 1);
    case 'left':
      return rect(0, side, a0, a1);
    case 'right':
      return rect(1 - side, 1, a0, a1);
  }
}

/** True for the sections whose LEDs play something on their own (a zone) instead of the screen. */
export function isZoneSection(section: Section | undefined): section is ZoneRole {
  return section?.kind === 'effect' || section?.kind === 'color';
}

/** Every LED of a strip placed, with what its section does and the number of LEDs on that section. */
function stripLeds(strip: Strip, room: Room): Array<PlacedLed & { section: Section; inPiece: number }> | null {
  const n = stripLedCount(strip);
  const leds = placeLeds(room, strip);
  if (leds.length !== n) return null;
  const perPiece = new Map<number, number>();
  for (const led of leds) perPiece.set(led.piece, (perPiece.get(led.piece) ?? 0) + 1);
  return leds.map((led) => ({ ...led, section: sectionAt(room, strip.path, led.piece), inPiece: perPiece.get(led.piece) ?? 1 }));
}

/**
 * Compile one strip to per-LED rects, in wiring order. Each LED takes what its section does (room.sectionAt): on an
 * `edge` section the LEDs spread evenly over its stretch of the outline (by where they are in the section), each
 * `depth` screen heights into the picture and as wide along the edge as the LED spacing there times `blend`;
 * `whole` and `area` LEDs show that area; `none` LEDs, dark LEDs and LEDs of a zone (`effect`, `color`, see
 * compileZones) get DISABLED_RECT. With `zone`, LEDs of a zone get the rect 'auto' would give them instead and every
 * other LED DISABLED_RECT (the layout a zone's effect is mapped with).
 */
export function compileStrip(strip: Strip, room: Room, zone = false): LedRect[] {
  const n = stripLedCount(strip);
  if (n === 0) return [];
  const leds = stripLeds(strip, room);
  if (!leds) return Array.from({ length: n }, () => ({ ...DISABLED_RECT }));
  const dark = darkMask(strip);
  const depth = strip.depth ?? DEFAULT_DEPTH;
  const blend = strip.blend ?? DEFAULT_BLEND;
  const side = Math.min(1, (depth * room.screen.height) / room.screen.width);
  return leds.map((led) => {
    if (dark[led.index]) return { ...DISABLED_RECT };
    if (isZoneSection(led.section) !== zone) return { ...DISABLED_RECT };
    const section = zone ? autoSection(room, strip.path[led.piece], strip.path[Math.min(strip.path.length - 1, led.piece + 1)]) : led.section;
    switch (section.kind) {
      case 'edge': {
        const p = section.from + led.t * (section.to - section.from);
        const spacing = Math.abs(section.to - section.from) / led.inPiece;
        const half = Math.min(MAX_HALF, Math.max(MIN_HALF, (blend * spacing) / 2));
        return edgeRect(p, half, depth, side);
      }
      case 'whole':
        return rect(0, 1, 0, 1);
      case 'area':
        return rect(section.area.hmin, section.area.hmax, section.area.vmin, section.area.vmax);
      default:
        return { ...DISABLED_RECT };
    }
  });
}

function nonNegativeInt(x: number): number {
  return Number.isFinite(x) ? Math.max(0, Math.floor(x)) : 0;
}

/**
 * Compile a whole layout: one rect array per controller, covering its full LED index space
 * (max over outputs of start + length). Each strip lands at its output's start; indices no
 * strip covers stay dark, and a strip longer than its output is truncated to fit.
 */
export function compileLayout(spec: LayoutSpec): CompiledController[] {
  return spec.controllers.map((controller) => {
    const size = controller.outputs.reduce(
      (m, o) => Math.max(m, nonNegativeInt(o.start) + nonNegativeInt(o.length)),
      0,
    );
    const leds: LedRect[] = Array.from({ length: size }, () => ({ ...DISABLED_RECT }));

    for (const strip of spec.strips) {
      if (strip.controllerId !== controller.id) continue;
      const output = controller.outputs.find((o) => o.index === strip.output);
      if (!output) continue;
      const start = nonNegativeInt(output.start);
      const rects = compileStrip(strip, spec.room);
      const n = Math.min(rects.length, nonNegativeInt(output.length));
      for (let i = 0; i < n; i++) leds[start + i] = rects[i];
    }

    return { controllerId: controller.id, leds };
  });
}

function roleKey(role: ZoneRole): string {
  return role.kind === 'effect' ? `effect:${role.effect}` : `color:${role.color.join(',')}`;
}

/**
 * The zones of a layout: one per distinct role ('effect' with the same effect, 'color' with the same color), with the
 * engine's index and the zone rect (compileStrip with `zone`) of each of its LEDs, in the order strips and LEDs come.
 * LEDs past their output's length and dark LEDs aren't in any zone.
 */
export function compileZones(spec: LayoutSpec): CompiledZone[] {
  const zones = new Map<string, CompiledZone>();
  let offset = 0;
  for (const controller of spec.controllers) {
    const size = controller.outputs.reduce((m, o) => Math.max(m, nonNegativeInt(o.start) + nonNegativeInt(o.length)), 0);
    for (const strip of spec.strips) {
      if (strip.controllerId !== controller.id) continue;
      const output = controller.outputs.find((o) => o.index === strip.output);
      const leds = output ? stripLeds(strip, spec.room) : null;
      if (!output || !leds) continue;
      const rects = compileStrip(strip, spec.room, true);
      const dark = darkMask(strip);
      const length = nonNegativeInt(output.length);
      for (const led of leds) {
        if (!isZoneSection(led.section) || dark[led.index] || led.index >= length) continue;
        const key = roleKey(led.section);
        let zone = zones.get(key);
        if (!zone) zones.set(key, (zone = { role: led.section, leds: [], rects: [] }));
        zone.leds.push(offset + nonNegativeInt(output.start) + led.index);
        zone.rects.push(rects[led.index]);
      }
    }
    offset += size;
  }
  return [...zones.values()];
}
