import { furnitureTop, surfaceSize, type Room, type Surface } from '../../layout';

// The furniture of the room's schematic, defined once: Scenery.tsx draws these boxes and furnitureSnaps() turns the
// same boxes into the lines and corners points snap to, so a point snapped to a corner sits on the drawn corner
// (corners are drawn rounded; points snap to the geometric corner).
// Front-wall boxes are in front-wall coordinates (seen from the room: x from the left wall, y down from the ceiling),
// floor boxes in floor coordinates (seen from above: x from the left wall, y from the front wall), in meters.

export interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface FrontFurniture {
  kind: 'desk' | 'tv';
  /** The desk top or the console's top board; its top edge is where it meets the wall (furnitureTop). */
  top: Box;
  /** Desk: the rail under the top between the legs. Console: the cabinet under the top. */
  body: Box;
  /** Console: x of the gaps between its doors. */
  doors: number[];
  legs: Box[];
  /** The monitor's or TV's stand: a neck from behind the screen down to a foot on the top. Null when wall-mounted. */
  stand: { neck: Box; foot: Box } | null;
  /** A wall-mounted TV's bracket, peeking out under it. */
  mount: Box | null;
}

export interface FloorFurniture {
  /** The desk or console against the front wall. */
  footprint: Box;
  /** The monitor or TV seen from above. */
  screen: Box;
  /** The stand's foot seen from above. */
  foot: Box | null;
  /** Where one sits: a chair at the desk, a sofa facing the TV. */
  seat: { kind: 'chair' | 'sofa'; box: Box } | null;
}

export interface FurnitureGeometry {
  front: FrontFurniture;
  floor: FloorFurniture;
}

/** Height of the baseboard along the bottom of every wall. */
export const BASEBOARD = 0.06;

const DESK_TOP = 0.03;
const DESK_DEPTH = 0.7;
const DESK_LEG = 0.05;
const DESK_LEG_INSET = 0.04;
const DESK_APRON = 0.07;
const CONSOLE_TOP = 0.03;
const CONSOLE_DEPTH = 0.42;
const CONSOLE_LEGS = 0.1;
const CONSOLE_LEG = 0.045;
const FOOT = 0.012;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

export function furnitureGeometry(room: Room): FurnitureGeometry | null {
  const t = furnitureTop(room);
  if (!t) return null;
  const H = room.height;
  const s = room.screen;
  const cx = s.x + s.width / 2;
  const bottom = s.y + s.height;
  const onTop = cx > t.x && cx < t.x + t.width;
  if (room.furniture === 'desk') {
    const top = { x: t.x, y: t.y, w: t.width, h: DESK_TOP };
    const legY = t.y + DESK_TOP;
    const legs = [
      { x: t.x + DESK_LEG_INSET, y: legY, w: DESK_LEG, h: Math.max(0, H - legY) },
      { x: t.x + t.width - DESK_LEG_INSET - DESK_LEG, y: legY, w: DESK_LEG, h: Math.max(0, H - legY) },
    ];
    const body = { x: legs[0].x + DESK_LEG, y: legY, w: Math.max(0, legs[1].x - legs[0].x - DESK_LEG), h: Math.min(DESK_APRON, Math.max(0, H - legY)) };
    // A stand when the monitor is just above the desk; higher up, it hangs on the wall.
    const gap = t.y - FOOT - bottom;
    const footW = clamp(s.width * 0.42, 0.16, 0.3);
    const stand =
      onTop && gap > 0 && gap <= 0.5
        ? {
            neck: { x: cx - 0.018, y: bottom - Math.min(0.08, s.height / 2), w: 0.036, h: t.y - FOOT - bottom + Math.min(0.08, s.height / 2) },
            foot: { x: cx - footW / 2, y: t.y - FOOT, w: footW, h: FOOT },
          }
        : null;
    const depth = Math.min(DESK_DEPTH, room.depth);
    const chairY = depth + 0.08;
    const chair = { x: clamp(cx, 0.26, room.width - 0.26) - 0.26, y: chairY, w: 0.52, h: 0.5 };
    return {
      front: { kind: 'desk', top, body, doors: [], legs, stand, mount: null },
      floor: {
        footprint: { x: t.x, y: 0, w: t.width, h: depth },
        screen: { x: s.x, y: Math.min(0.14, depth / 2), w: s.width, h: 0.025 },
        foot: stand ? { x: stand.foot.x, y: Math.min(0.06, depth / 4), w: stand.foot.w, h: Math.min(0.2, depth / 2) } : null,
        seat: chair.y + chair.h <= room.depth ? { kind: 'chair', box: chair } : null,
      },
    };
  }
  // A TV console: a top board on a cabinet with doors, on short legs.
  const top = { x: t.x, y: t.y, w: t.width, h: CONSOLE_TOP };
  const bodyY = t.y + CONSOLE_TOP;
  const bodyBottom = Math.max(bodyY, H - CONSOLE_LEGS);
  const body = { x: t.x + 0.02, y: bodyY, w: Math.max(0, t.width - 0.04), h: bodyBottom - bodyY };
  const doorCount = t.width >= 1.5 ? 3 : 2;
  const doors = Array.from({ length: doorCount - 1 }, (_, i) => body.x + (body.w * (i + 1)) / doorCount);
  const legs = [
    { x: t.x + 0.07, y: bodyBottom, w: CONSOLE_LEG, h: H - bodyBottom },
    { x: t.x + t.width - 0.07 - CONSOLE_LEG, y: bodyBottom, w: CONSOLE_LEG, h: H - bodyBottom },
  ];
  // A TV that nearly sits on the console stands on a pedestal; one higher up hangs on the wall.
  const gap = t.y - FOOT - bottom;
  const footW = clamp(s.width * 0.3, 0.2, 0.45);
  const stand =
    onTop && gap > 0 && gap <= 0.2
      ? { neck: { x: cx - 0.03, y: bottom - 0.05, w: 0.06, h: gap + 0.05 }, foot: { x: cx - footW / 2, y: t.y - FOOT, w: footW, h: FOOT } }
      : null;
  const mountW = clamp(s.width * 0.28, 0.15, 0.4);
  const mount = stand ? null : { x: cx - mountW / 2, y: bottom - 0.02, w: mountW, h: 0.03 };
  const depth = Math.min(CONSOLE_DEPTH, room.depth);
  // A sofa about 2.6 m from the TV, or against the back wall in a smaller room.
  const sofaW = Math.min(2.2, room.width - 0.3);
  const sofaD = 0.92;
  const sofaY = Math.min(2.6, room.depth - sofaD - 0.15);
  const sofa = { x: clamp(cx, sofaW / 2, room.width - sofaW / 2) - sofaW / 2, y: sofaY, w: sofaW, h: sofaD };
  return {
    front: { kind: 'tv', top, body, doors, legs, stand, mount },
    floor: {
      footprint: { x: t.x, y: 0, w: t.width, h: depth },
      screen: stand ? { x: s.x, y: Math.min(0.17, depth / 2), w: s.width, h: 0.04 } : { x: s.x, y: 0.015, w: s.width, h: 0.04 },
      foot: stand ? { x: stand.foot.x, y: Math.min(0.09, depth / 4), w: stand.foot.w, h: Math.min(0.2, depth / 2) } : null,
      seat: sofaW >= 1.2 && sofaY >= depth + 1 ? { kind: 'sofa', box: sofa } : null,
    },
  };
}

export interface SnapSegment {
  ax: number;
  ay: number;
  bx: number;
  by: number;
}

export interface FurnitureSnaps {
  /** Edges a point snaps onto. */
  lines: SnapSegment[];
  /** Corners a point snaps to before lines. */
  corners: Array<{ x: number; y: number }>;
}

const hLine = (x0: number, x1: number, y: number): SnapSegment => ({ ax: x0, ay: y, bx: x1, by: y });
const vLine = (x: number, y0: number, y1: number): SnapSegment => ({ ax: x, ay: y0, bx: x, by: y1 });

function boxSnaps(b: Box, out: FurnitureSnaps) {
  const x1 = b.x + b.w;
  const y1 = b.y + b.h;
  out.lines.push(hLine(b.x, x1, b.y), hLine(b.x, x1, y1), vLine(b.x, b.y, y1), vLine(x1, b.y, y1));
  out.corners.push({ x: b.x, y: b.y }, { x: x1, y: b.y }, { x: x1, y: y1 }, { x: b.x, y: y1 });
}

/**
 * What points snap to on a surface besides its edges and the screen: the furniture's outlines as drawn (the top's
 * edges and corners, the rail or cabinet under it, the stand's neck; on the floor the footprints of the furniture)
 * and the top of the baseboard on the walls. In the surface's coordinates.
 */
export function furnitureSnaps(room: Room, surface: Surface = 'front'): FurnitureSnaps {
  const out: FurnitureSnaps = { lines: [], corners: [] };
  if (surface === 'front' || surface === 'left' || surface === 'right' || surface === 'back') {
    const { w, h } = surfaceSize(room, surface);
    out.lines.push(hLine(0, w, h - BASEBOARD));
    out.corners.push({ x: 0, y: h - BASEBOARD }, { x: w, y: h - BASEBOARD });
  }
  const g = furnitureGeometry(room);
  if (!g) return out;
  if (surface === 'front') {
    const f = g.front;
    boxSnaps(f.top, out);
    // Under the top: the rail's or the cabinet's bottom edge, from end to end.
    const b = f.body;
    out.lines.push(hLine(b.x, b.x + b.w, b.y + b.h));
    out.corners.push({ x: b.x, y: b.y + b.h }, { x: b.x + b.w, y: b.y + b.h });
    if (f.kind === 'tv') out.lines.push(vLine(b.x, b.y, b.y + b.h), vLine(b.x + b.w, b.y, b.y + b.h));
    for (const leg of f.legs) out.lines.push(vLine(leg.x, leg.y, leg.y + leg.h), vLine(leg.x + leg.w, leg.y, leg.y + leg.h));
    if (f.stand) {
      const { neck, foot } = f.stand;
      const nx = neck.x + neck.w / 2;
      out.lines.push(vLine(nx, room.screen.y + room.screen.height, foot.y), hLine(foot.x, foot.x + foot.w, foot.y));
      out.corners.push({ x: nx, y: foot.y });
    }
  } else if (surface === 'floor') {
    boxSnaps(g.floor.footprint, out);
    if (g.floor.seat?.kind === 'sofa') boxSnaps(g.floor.seat.box, out);
  }
  return out;
}
