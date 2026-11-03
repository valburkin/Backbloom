import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Undo2 } from 'lucide-react';
import { Button } from '../components/ui';
import { useConnection } from '../app/connection';
import { surfaceOrigin, type PathPoint, type Room } from '../layout';
import { Scenery } from '../pages/layout/Scenery';
import { ErrorText, errorMessage } from './parts';
import {
  MAX_ROUTE_POINTS,
  boardStart,
  checkLeds,
  fromBoard,
  ledSpots,
  pinRange,
  pointLeds,
  routePath,
  snapFront,
  traceCrop,
  type FrontSpot,
  type TracePoint,
} from './trace';
import { createSerial } from './wled';

/** Renders the trace in the setup's step: its heading, body and buttons (one primary). */
export type TraceFrame = (p: { title: string; intro: ReactNode; children: ReactNode; actions: ReactNode }) => ReactNode;

/** Meters per pixel of the drawing at its usual width (strokes, text and the snapping distance). */
const UNIT_PX = 600;
const SNAP_PX = 14;

/**
 * The front view the user draws on: the monitor (or TV) and the desk (or stand) as Layout draws them, the board (to
 * drag where it is), the route's points joined in order and, once checked, the strip's LEDs along it with the LED
 * numbers on the corners and ends.
 */
export function TraceDrawing({
  room,
  board,
  points,
  leds,
  labels,
  onBoard,
  onPick,
}: {
  room: Room;
  board: FrontSpot;
  points: readonly TracePoint[];
  /** The strip's LEDs along the route (while checking). */
  leds?: readonly FrontSpot[];
  /** A label per point (LED numbers while checking). */
  labels?: readonly string[];
  onBoard?(p: FrontSpot): void;
  onPick?(p: TracePoint): void;
}) {
  const svg = useRef<SVGSVGElement>(null);
  const dragging = useRef(false);
  const dragged = useRef(false);
  /** The press was on the board: a click there starts the route at the board, never adds a point later on. */
  const onMarker = useRef(false);
  const [hover, setHover] = useState<FrontSpot | null>(null);
  const crop = traceCrop(room);
  const o = surfaceOrigin(room, 'front');
  const u = crop.w / UNIT_PX;
  const s = room.screen;
  const toFront = (clientX: number, clientY: number): FrontSpot | null => {
    const m = svg.current?.getScreenCTM();
    if (!m) return null;
    const p = new DOMPoint(clientX, clientY).matrixTransform(m.inverse());
    return { x: Math.min(crop.x + crop.w, Math.max(crop.x, p.x - o.x)), y: Math.min(crop.y + crop.h, Math.max(crop.y, p.y - o.y)) };
  };
  const snap = (p: FrontSpot) => snapFront(room, p.x, p.y, SNAP_PX * u, points.length === 0 ? board : undefined);
  const screenName = room.furniture === 'tv' ? 'TV' : 'monitor';
  const deskName = room.furniture === 'tv' ? 'TV stand' : 'desk';
  return (
    <svg
      ref={svg}
      viewBox={`${o.x + crop.x} ${o.y + crop.y} ${crop.w} ${crop.h}`}
      className={onPick ? 'block w-full cursor-crosshair touch-none select-none rounded-lg' : 'block w-full touch-none select-none rounded-lg'}
      style={{ aspectRatio: `${crop.w} / ${crop.h}` }}
      role="img"
      aria-label={`Your ${screenName} and ${deskName} from the front${points.length ? `, the route with ${points.length} point${points.length === 1 ? '' : 's'}` : ''}`}
      onPointerMove={(e) => {
        const p = toFront(e.clientX, e.clientY);
        if (!p) return;
        if (dragging.current && onBoard) {
          dragged.current = true;
          onBoard(p);
        } else if (onPick) setHover(snap(p));
      }}
      onPointerUp={() => {
        dragging.current = false;
      }}
      onPointerLeave={() => {
        dragging.current = false;
        setHover(null);
      }}
      onClick={(e) => {
        // the end of a drag isn't a click on the route
        const marker = onMarker.current;
        onMarker.current = false;
        if (dragged.current || (marker && points.length > 0)) {
          dragged.current = false;
          return;
        }
        const p = onPick && toFront(e.clientX, e.clientY);
        if (p) onPick(snap(p));
      }}
    >
      <rect x={o.x + crop.x} y={o.y + crop.y} width={crop.w} height={crop.h} fill="#0a0c11" />
      <Scenery room={room} u={u} />
      <rect x={o.x + s.x} y={o.y + s.y} width={s.width} height={s.height} rx={0.008} fill="#0b0e15" stroke="#4a5470" strokeWidth={1.5 * u} />
      {/* the board: drag it to where it is */}
      <g
        className={onBoard ? 'cursor-grab' : undefined}
        onPointerDown={(e) => {
          if (!onBoard) return;
          e.stopPropagation();
          dragging.current = true;
          dragged.current = false;
          onMarker.current = true;
          try {
            svg.current?.setPointerCapture(e.pointerId);
          } catch {
            // no capture (the pointer is gone already): the drag still follows while over the drawing
          }
        }}
      >
        <rect x={o.x + board.x - 14 * u} y={o.y + board.y - 9 * u} width={28 * u} height={18 * u} rx={3 * u} fill="#1f2a3d" stroke="#8b9bbf" strokeWidth={1.2 * u} />
        <text x={o.x + board.x} y={o.y + board.y + 19 * u} textAnchor="middle" fontSize={9 * u} fontWeight={600} fill="#e6e8ef" pointerEvents="none">
          Board
        </text>
        <title>Your board: drag it to where it is</title>
      </g>
      {points.length > 1 && (
        <polyline
          points={points.map((p) => `${o.x + p.x},${o.y + p.y}`).join(' ')}
          fill="none"
          stroke="var(--color-accent)"
          strokeOpacity={leds ? 0.45 : 1}
          strokeWidth={3 * u}
          strokeLinejoin="round"
          strokeLinecap="round"
        />
      )}
      {leds?.map((l, i) => <circle key={i} cx={o.x + l.x} cy={o.y + l.y} r={2 * u} fill="#e6e8ef" fillOpacity={0.7} pointerEvents="none" />)}
      {points.map((p, i) => (
        <g key={i} pointerEvents="none">
          <circle cx={o.x + p.x} cy={o.y + p.y} r={6 * u} fill="var(--color-accent)" stroke="#0a0c11" strokeWidth={1.5 * u} />
          {(labels?.[i] ?? (i === 0 ? 'Start' : '')) && (
            <text x={o.x + p.x} y={o.y + p.y - 10 * u} textAnchor="middle" fontSize={11 * u} fontWeight={600} fill="#e6e8ef">
              {labels?.[i] ?? 'Start'}
            </text>
          )}
        </g>
      ))}
      {hover && <circle cx={o.x + hover.x} cy={o.y + hover.y} r={6 * u} fill="none" stroke="#fff" strokeOpacity={0.8} strokeWidth={1.5 * u} pointerEvents="none" />}
    </svg>
  );
}

type Phase = 'draw' | 'check' | 'adjust';

/**
 * Trace your strip: the user drags the board to where it is and draws the strip's route (its start, each corner or
 * turn, its end); the strip's LEDs are spread evenly along it, as on Layout. The board then lights the LEDs that land
 * on the corners and ends (`wled-test` `points`) and the user says whether they sit on the corners; if not, Fewer and
 * More move each corner's LED onto it, which matches that corner to its LED. `onDone` gets the strip's path (LED 1 at
 * the end nearest the board); Cancel changes nothing. The board's own state comes back when the trace ends (`off`).
 */
export function TraceStrip({
  frame,
  room,
  name,
  host,
  output,
  leds,
  onDone,
  onCancel,
}: {
  frame: TraceFrame;
  room: Room;
  name: string;
  host: string;
  output: number;
  leds: number;
  onDone(path: PathPoint[]): void;
  onCancel(): void;
}) {
  const { api } = useConnection();
  const [phase, setPhase] = useState<Phase>('draw');
  const [board, setBoard] = useState<FrontSpot>(() => boardStart(room));
  const [points, setPoints] = useState<TracePoint[]>([]);
  /** Corners matched to an LED (point index → LED, from 1). */
  const [pinned, setPinned] = useState<Record<number, number>>({});
  const [error, setError] = useState<string | null>(null);
  const [serial] = useState(createSerial);
  const live = useRef(true);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const path = useMemo(() => routePath(points, pinned), [points, pinned]);
  const ledsAt = useMemo(() => (points.length > 1 ? pointLeds(room, path, leds) : []), [room, path, leds, points.length]);
  // drawing: the strip dim and its first LED bright (where it starts); checking: the LEDs on the corners and ends
  const lit = phase === 'draw' || ledsAt.length === 0 ? [1] : checkLeds(ledsAt);
  const litKey = lit.join(',');

  useEffect(() => {
    live.current = true;
    return () => {
      live.current = false;
      if (timer.current) clearTimeout(timer.current);
      // the board's own state back, after anything still on its way
      void serial(() => api.wledTest(host, { output, pattern: 'off' })).catch(() => undefined);
    };
  }, []);

  // what the board shows, sent a moment after the last change
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      void serial(async () => {
        if (!live.current) return;
        try {
          await api.wledTest(host, { output, pattern: 'points', leds: lit });
          if (live.current) setError(null);
        } catch (e) {
          if (live.current) setError(errorMessage(e));
        }
      });
    }, 200);
  }, [litKey]);

  const pick = (p: TracePoint) => {
    const last = points[points.length - 1];
    // a second click on the same spot adds nothing (a corner is one point)
    if (last && Math.hypot(last.x - p.x, last.y - p.y) < 0.005) return;
    if (points.length < MAX_ROUTE_POINTS) setPoints([...points, p]);
  };
  const check = () => {
    // LED 1 is the end nearest the board
    setPoints(fromBoard(points, board));
    setPinned({});
    setPhase('check');
  };
  const redraw = () => {
    setPinned({});
    setPhase('draw');
  };
  const move = (i: number, d: number) => {
    const { min, max } = pinRange(ledsAt, i, leds);
    setPinned({ ...pinned, [i]: Math.max(min, Math.min(max, ledsAt[i] + d)) });
  };
  const done = () => onDone(routePath(points, pinned));

  if (phase === 'draw')
    return frame({
      title: 'Trace your strip',
      intro: `Drag the board to where it is. Then click where the ${name} strip starts (the end its wire from the board goes into), at each corner or turn, and where it ends. The board lights the strip dimly and its first LED brightly.`,
      actions: (
        <>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="primary" disabled={points.length < 2} onClick={check}>
            Next: check the corners
          </Button>
        </>
      ),
      children: (
        <div className="space-y-3">
          <TraceDrawing room={room} board={board} points={points} onBoard={setBoard} onPick={pick} />
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" icon={<Undo2 size={14} />} disabled={!points.length} onClick={() => setPoints(points.slice(0, -1))}>
              Undo last point
            </Button>
            <Button size="sm" variant="ghost" disabled={!points.length} onClick={() => setPoints([])}>
              Clear
            </Button>
            {points.length > 0 && (
              <span className="text-xs text-ink-faint">
                {points.length === 1 ? `Starts at ${points[0].where}.` : `${points.length} points, from ${points[0].where} to ${points[points.length - 1].where}.`}
              </span>
            )}
          </div>
          <p className="text-xs text-ink-faint">
            Clicks snap to the screen’s edges and corners and to the {room.furniture === 'tv' ? 'TV stand' : 'desk'}’s back edge. Backbloom spreads the strip’s{' '}
            {leds} LEDs evenly along the route.
          </p>
          {error && <ErrorText>{error}</ErrorText>}
        </div>
      ),
    });

  const spots = ledSpots(room, path, leds);
  const labels = ledsAt.map((n) => `LED ${n}`);
  const drawing = <TraceDrawing room={room} board={board} points={points} leds={spots} labels={labels} />;

  if (phase === 'check')
    return frame({
      title: 'Are the bright LEDs on the corners?',
      intro: 'The board lights the LEDs that land on your corners and ends brightly, the rest dimly, numbered as on the drawing.',
      actions: (
        <>
          <Button variant="ghost" onClick={redraw}>
            Change the route
          </Button>
          <Button variant="secondary" onClick={() => setPhase('adjust')}>
            No, move them
          </Button>
          <Button variant="primary" onClick={done}>
            Yes, use this route
          </Button>
        </>
      ),
      children: (
        <div className="space-y-3">
          {drawing}
          {error && <ErrorText>{error}</ErrorText>}
        </div>
      ),
    });

  return frame({
    title: 'Move the LEDs onto the corners',
    intro: 'For each corner, press Fewer or More until the bright LED sits on it. The LEDs between the corners are spread evenly.',
    actions: (
      <>
        <Button variant="ghost" onClick={redraw}>
          Change the route
        </Button>
        <Button variant="primary" onClick={done}>
          Use this route
        </Button>
      </>
    ),
    children: (
      <div className="space-y-3">
        {drawing}
        <ul className="divide-y divide-line border-y border-line">
          {points.map((p, i) => {
            const end = i === 0 || i === points.length - 1;
            const { min, max } = pinRange(ledsAt, i, leds);
            return (
              <li key={i} className="flex flex-wrap items-center gap-2 py-2 text-sm">
                <span className="min-w-[12rem] flex-1">
                  {i === 0 ? 'Start' : i === points.length - 1 ? 'End' : `Corner ${i}`} <span className="text-xs text-ink-faint">· {p.where}</span>
                </span>
                {end ? (
                  <span className="text-xs text-ink-faint">LED {ledsAt[i]}</span>
                ) : (
                  <span className="flex items-center gap-1.5" role="group" aria-label={`The LED on corner ${i}`}>
                    <Button size="sm" disabled={ledsAt[i] <= min} onClick={() => move(i, -1)}>
                      Fewer
                    </Button>
                    <span className="min-w-[4.5rem] text-center text-sm font-semibold tabular-nums" aria-live="polite">
                      LED {ledsAt[i]}
                    </span>
                    <Button size="sm" disabled={ledsAt[i] >= max} onClick={() => move(i, 1)}>
                      More
                    </Button>
                  </span>
                )}
              </li>
            );
          })}
        </ul>
        {error && <ErrorText>{error}</ErrorText>}
      </div>
    ),
  });
}
