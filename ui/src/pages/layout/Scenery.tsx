import { useId, type SVGProps } from 'react';
import { SURFACE_LABELS, SURFACES, type Room, type Surface } from '../../layout';
import { surfaceRect } from './canvasMath';
import { BASEBOARD, furnitureGeometry, type Box, type FloorFurniture, type FrontFurniture } from './furniture';

// The room's schematic drawing: surfaces with their names and baseboards, the furniture on the front wall (seen from
// the room) and on the floor (from above), a ceiling light. Drawing only: the shapes come from furnitureGeometry() in
// furniture.ts, which also gives what points snap to, so the two always match.

const SURFACE_FILL: Record<Surface, string> = {
  front: '#161b27',
  left: '#121620',
  right: '#121620',
  back: '#10131b',
  ceiling: '#11141d',
  floor: '#0f1219',
};

const EDGE = '#2a3142';

// Furniture: a few steps above the walls, calm next to the strips.
const C = {
  top: '#283043',
  topEdge: '#3d4862',
  topSeen: '#222a3b',
  body: '#1d2331',
  leg: '#222939',
  line: '#323b51',
  groove: '#121620',
  handle: '#3a4560',
  stand: '#2a3246',
  standEdge: '#3f4a64',
  soft: '#1b2130',
  softRaised: '#222939',
};

type Shift = { x: number; y: number };

function R({ b, o, r = 0, ...rest }: { b: Box; o: Shift; r?: number } & SVGProps<SVGRectElement>) {
  return <rect x={o.x + b.x} y={o.y + b.y} width={Math.max(0, b.w)} height={Math.max(0, b.h)} rx={Math.min(r, b.w / 2, b.h / 2)} {...rest} />;
}

export function Scenery({ room, u }: { room: Room; u: number }) {
  const id = useId().replace(/:/g, '');
  const front = surfaceRect(room, 'front');
  const floor = surfaceRect(room, 'floor');
  const ceiling = surfaceRect(room, 'ceiling');
  const g = furnitureGeometry(room);
  const label = 11 * u;
  return (
    <g pointerEvents="none">
      <defs>
        <linearGradient id={`${id}-under`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#000" stopOpacity={0.24} />
          <stop offset="0.3" stopColor="#000" stopOpacity={0.1} />
          <stop offset="1" stopColor="#000" stopOpacity={0.03} />
        </linearGradient>
        <linearGradient id={`${id}-top`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#323b52" />
          <stop offset="1" stopColor={C.top} />
        </linearGradient>
        <clipPath id={`${id}-floor`}>
          <rect x={floor.x} y={floor.y} width={floor.w} height={floor.h} />
        </clipPath>
      </defs>
      {SURFACES.map((s) => {
        const r = surfaceRect(room, s);
        const wall = s !== 'ceiling' && s !== 'floor';
        return (
          <g key={s}>
            <rect x={r.x} y={r.y} width={r.w} height={r.h} fill={SURFACE_FILL[s]} />
            {wall && (
              <>
                <rect x={r.x} y={r.y + r.h - BASEBOARD} width={r.w} height={BASEBOARD} fill="#fff" fillOpacity={0.022} />
                <line x1={r.x} x2={r.x + r.w} y1={r.y + r.h - BASEBOARD} y2={r.y + r.h - BASEBOARD} stroke="#262d3d" strokeWidth={u} />
              </>
            )}
            <rect x={r.x} y={r.y} width={r.w} height={r.h} fill="none" stroke={EDGE} strokeWidth={u} />
          </g>
        );
      })}
      <CeilingLight cx={ceiling.x + ceiling.w / 2} cy={ceiling.y + ceiling.h / 2} u={u} />
      {g && <FrontView f={g.front} o={front} floorY={front.y + front.h} u={u} id={id} />}
      {g && <FloorView f={g.floor} o={floor} u={u} clip={`url(#${id}-floor)`} />}
      {/* Names last, so furniture never hides them. */}
      {SURFACES.map((s) => {
        const r = surfaceRect(room, s);
        return (
          <text
            key={s}
            x={r.x + 8 * u}
            y={r.y + 16 * u}
            fontSize={label}
            fill="#5b6478"
            fontWeight={600}
            letterSpacing={u}
            stroke={SURFACE_FILL[s]}
            strokeWidth={3 * u}
            paintOrder="stroke"
          >
            {SURFACE_LABELS[s].toUpperCase()}
          </text>
        );
      })}
    </g>
  );
}

function CeilingLight({ cx, cy, u }: { cx: number; cy: number; u: number }) {
  return (
    <g>
      <circle cx={cx} cy={cy} r={0.2} fill="#141823" stroke={EDGE} strokeWidth={u} />
      <circle cx={cx} cy={cy} r={0.13} fill="none" stroke="#232a3a" strokeWidth={u} />
      <circle cx={cx} cy={cy} r={0.035} fill="#2a3142" />
    </g>
  );
}

/** The desk or TV console seen from the room, standing on the floor in front of the wall. */
function FrontView({ f, o, floorY, u, id }: { f: FrontFurniture; o: Shift; floorY: number; u: number; id: string }) {
  const { top, body, legs } = f;
  const legL = legs[0];
  const legR = legs[legs.length - 1];
  const under = f.kind === 'desk' ? { x: legL.x + legL.w, y: top.y + top.h, w: legR.x - legL.x - legL.w, h: floorY - o.y - top.y - top.h } : null;
  return (
    <g strokeWidth={u}>
      {/* The wall in the shade under the desk. */}
      {under && <R b={under} o={o} fill={`url(#${id}-under)`} />}
      {/* Contact shadows on the floor line. */}
      {legs.map((l, i) => (
        <ellipse key={`s${i}`} cx={o.x + l.x + l.w / 2} cy={floorY} rx={l.w * 1.1} ry={0.008} fill="#000" fillOpacity={0.45} />
      ))}
      {legs.map((l, i) => (
        <R key={i} b={l} o={o} r={0.004} fill={C.leg} stroke={C.line} />
      ))}
      {f.kind === 'desk' ? (
        <>
          {/* The rail under the top, with a shallow drawer in the middle. */}
          <R b={body} o={o} fill={C.body} stroke={C.line} />
          {body.w > 0.5 && (
            <>
              <R b={{ x: body.x + body.w / 2 - 0.22, y: body.y + 0.012, w: 0.44, h: body.h - 0.024 }} o={o} r={0.004} fill="none" stroke={C.line} />
              <R b={{ x: body.x + body.w / 2 - 0.05, y: body.y + body.h / 2 - 0.005, w: 0.1, h: 0.01 }} o={o} r={0.005} fill={C.handle} />
            </>
          )}
        </>
      ) : (
        <>
          {/* The cabinet: doors with small handles. */}
          <R b={body} o={o} fill={C.body} stroke={C.line} />
          {f.doors.map((x, i) => (
            <line key={i} x1={o.x + x} x2={o.x + x} y1={o.y + body.y + 0.015} y2={o.y + body.y + body.h - 0.015} stroke={C.groove} strokeWidth={2 * u} />
          ))}
          {[body.x, ...f.doors].map((x0, i, all) => {
            const x1 = all[i + 1] ?? body.x + body.w;
            const w = Math.min(0.12, (x1 - x0) * 0.3);
            return <R key={`h${i}`} b={{ x: (x0 + x1) / 2 - w / 2, y: body.y + 0.045, w, h: 0.01 }} o={o} r={0.005} fill={C.handle} />;
          })}
          {/* A plinth line where the cabinet ends above its legs. */}
          <line x1={o.x + body.x} x2={o.x + body.x + body.w} y1={o.y + body.y + body.h} y2={o.y + body.y + body.h} stroke={C.topEdge} strokeOpacity={0.5} />
        </>
      )}
      {/* The top, its upper face catching a bit of light. */}
      <R b={top} o={o} r={0.006} fill={`url(#${id}-top)`} stroke={C.line} />
      <line x1={o.x + top.x + 0.006} x2={o.x + top.x + top.w - 0.006} y1={o.y + top.y + 0.75 * u} y2={o.y + top.y + 0.75 * u} stroke={C.topEdge} strokeWidth={1.5 * u} />
      {f.mount && <R b={f.mount} o={o} r={0.004} fill={C.body} stroke={C.line} />}
      {f.stand && (
        <>
          <R b={f.stand.neck} o={o} r={0.008} fill={C.stand} stroke={C.standEdge} />
          <R b={f.stand.foot} o={o} r={0.005} fill={C.stand} stroke={C.standEdge} />
        </>
      )}
    </g>
  );
}

/** The furniture seen from above: the desk or console against the front wall, a chair or a sofa. */
function FloorView({ f, o, u, clip }: { f: FloorFurniture; o: Shift; u: number; clip: string }) {
  const seat = f.seat;
  return (
    <g strokeWidth={u} clipPath={clip}>
      {seat?.kind === 'chair' && <Chair b={seat.box} o={o} />}
      {seat?.kind === 'sofa' && <Sofa b={seat.box} o={o} u={u} />}
      <Shadow b={f.footprint} o={o} r={0.012} />
      <R b={f.footprint} o={o} r={0.012} fill={C.topSeen} stroke={C.line} />
      <line
        x1={o.x + f.footprint.x + 0.012}
        x2={o.x + f.footprint.x + f.footprint.w - 0.012}
        y1={o.y + f.footprint.y + f.footprint.h - 0.75 * u}
        y2={o.y + f.footprint.y + f.footprint.h - 0.75 * u}
        stroke={C.topEdge}
        strokeWidth={1.5 * u}
      />
      {f.foot && <R b={f.foot} o={o} r={0.02} fill={C.stand} stroke={C.standEdge} />}
      <R b={f.screen} o={o} r={0.008} fill="#0b0e15" stroke="#4a5470" strokeWidth={1.5 * u} />
    </g>
  );
}

/** A soft shadow under something seen from above, cast away from the ceiling light. */
function Shadow({ b, o, r }: { b: Box; o: Shift; r: number }) {
  return <R b={{ ...b, x: b.x + 0.02, y: b.y + 0.03 }} o={o} r={r} fill="#000" fillOpacity={0.28} />;
}

function Chair({ b, o }: { b: Box; o: Shift }) {
  const back = 0.09;
  const arm = 0.055;
  return (
    <g>
      <circle cx={o.x + b.x + b.w / 2 + 0.02} cy={o.y + b.y + b.h / 2 + 0.03} r={0.3} fill="#000" fillOpacity={0.22} />
      <R b={{ x: b.x + arm, y: b.y, w: b.w - 2 * arm, h: b.h - back + 0.02 }} o={o} r={0.07} fill={C.soft} stroke={C.line} />
      <R b={{ x: b.x, y: b.y + 0.08, w: arm, h: b.h - 0.2 }} o={o} r={0.025} fill={C.softRaised} stroke={C.line} />
      <R b={{ x: b.x + b.w - arm, y: b.y + 0.08, w: arm, h: b.h - 0.2 }} o={o} r={0.025} fill={C.softRaised} stroke={C.line} />
      <R b={{ x: b.x + 0.02, y: b.y + b.h - back, w: b.w - 0.04, h: back }} o={o} r={0.04} fill={C.softRaised} stroke={C.line} />
    </g>
  );
}

function Sofa({ b, o, u }: { b: Box; o: Shift; u: number }) {
  const arm = 0.18;
  const back = 0.22;
  const seats = b.w >= 1.9 ? 3 : 2;
  const seatW = (b.w - 2 * arm) / seats;
  return (
    <g>
      <Shadow b={b} o={o} r={0.07} />
      <R b={b} o={o} r={0.07} fill={C.softRaised} stroke={C.line} />
      {Array.from({ length: seats }, (_, i) => (
        <R key={i} b={{ x: b.x + arm + i * seatW + 0.008, y: b.y + 0.02, w: seatW - 0.016, h: b.h - back - 0.02 }} o={o} r={0.04} fill={C.soft} stroke={C.line} strokeWidth={u} />
      ))}
    </g>
  );
}
