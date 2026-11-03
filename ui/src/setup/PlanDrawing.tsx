import type { Arrow, Point, Side, StripPlan } from './plan';

const SCREEN_W = 300;
const SCREEN_H_MAX = 180;
const MARGIN = 64;

const SCREEN_COLORS = {
  screen: 'var(--color-surface-2)',
  screenLine: 'var(--color-line-strong)',
  stand: 'var(--color-surface-3)',
  strip: 'var(--color-accent)',
  arrow: 'var(--color-accent-ink)',
  joint: 'var(--color-ink-muted)',
  text: 'var(--color-ink-muted)',
  strong: 'var(--color-ink)',
  feed: 'var(--color-accent-strong)',
  bg: 'var(--color-bg)',
};
const PRINT_COLORS = { screen: '#f2f2f2', screenLine: '#888', stand: '#ddd', strip: '#333', arrow: '#fff', joint: '#555', text: '#333', strong: '#000', feed: '#000', bg: '#fff' };

const ANGLE: Record<Arrow, number> = { right: 0, down: 90, left: 180, up: 270 };
const NORMAL: Record<Side, Point> = { top: { x: 0, y: -1 }, bottom: { x: 0, y: 1 }, left: { x: -1, y: 0 }, right: { x: 1, y: 0 } };

/**
 * The plan drawn: the screen, the strip's pieces with their LED counts and data arrows, where the board connects,
 * the joints, the wire across the stand and the power feeds. As seen from the seat, or from behind (`fromBehind`,
 * where the strip is stuck on: left and right swap).
 */
export function PlanDrawing({ plan, fromBehind = false, print = false }: { plan: StripPlan; fromBehind?: boolean; print?: boolean }) {
  const c = print ? PRINT_COLORS : SCREEN_COLORS;
  const { widthMm: W, heightMm: H } = plan.input.screen;
  const s = Math.min(SCREEN_W / W, SCREEN_H_MAX / H);
  const w = W * s;
  const h = H * s;
  const flip = (x: number) => (fromBehind ? W - x : x);
  const P = (p: Point) => ({ x: flip(p.x) * s, y: p.y * s });
  const sideOf = (side: Side): Side => (fromBehind && side === 'left' ? 'right' : fromBehind && side === 'right' ? 'left' : side);
  const arrowOf = (a: Arrow): Arrow => (fromBehind && a === 'left' ? 'right' : fromBehind && a === 'right' ? 'left' : a);
  const gap = plan.input.sides === 4 ? plan.input.standGapMm : 0;
  const standW = Math.max(40, (gap > 0 ? gap * 0.6 : 140) * s);
  const first = plan.pieces[0];
  const start = first ? P(first.from) : { x: 0, y: 0 };
  const startTop = first ? first.from.y < H / 2 : false;
  const board = { x: Math.min(w - 30, Math.max(-MARGIN + 34, start.x)), y: startTop ? -44 : h + 42 };

  return (
    <svg
      viewBox={`${-MARGIN} ${-MARGIN} ${w + 2 * MARGIN} ${h + 2 * MARGIN}`}
      className="h-auto w-full"
      role="img"
      aria-label={`Strip plan: ${plan.totalLeds} LEDs on ${plan.pieces.length} pieces, seen from ${fromBehind ? 'behind the screen' : 'your seat'}`}
      style={{ fontFamily: 'var(--font-sans), sans-serif' }}
    >
      {/* stand */}
      <rect x={w / 2 - standW / 2} y={h - 4} width={standW} height={MARGIN * 0.55} rx={4} fill={c.stand} />
      {/* screen */}
      <rect x={0} y={0} width={w} height={h} rx={6} fill={c.screen} stroke={c.screenLine} strokeWidth={1.5} />
      <text x={w / 2} y={h / 2 + 5} textAnchor="middle" fontSize={14} fill={c.text}>
        {fromBehind ? 'Back of the screen' : 'Screen'} · {Math.round(W / 10)} × {Math.round(H / 10)} cm
      </text>

      {/* data wire from the board */}
      {first && (
        <>
          <line x1={board.x} y1={board.y + (startTop ? 14 : -14)} x2={start.x} y2={start.y} stroke={c.joint} strokeWidth={2} strokeDasharray="4 3" />
          <rect x={board.x - 32} y={board.y - 14} width={64} height={28} rx={5} fill={c.bg} stroke={c.strong} strokeWidth={1.5} />
          <text x={board.x} y={board.y + 5} textAnchor="middle" fontSize={15} fontWeight={600} fill={c.strong}>
            Board
          </text>
        </>
      )}

      {/* the wire across the stand */}
      {plan.joints
        .filter((j) => j.kind === 'jumper')
        .map((j) => {
          const before = plan.pieces.find((p) => p.lastLed === j.afterLed);
          const after = plan.pieces.find((p) => p.firstLed === j.afterLed + 1);
          if (!before || !after) return null;
          const a = P(before.to);
          const b = P(after.from);
          return (
            <path
              key={`jumper-${j.afterLed}`}
              d={`M ${a.x} ${a.y} C ${a.x} ${a.y + 26}, ${b.x} ${b.y + 26}, ${b.x} ${b.y}`}
              fill="none"
              stroke={c.joint}
              strokeWidth={2}
              strokeDasharray="5 3"
            />
          );
        })}

      {/* pieces */}
      {plan.pieces.map((p) => {
        const a = P(p.from);
        const len = Math.hypot(p.to.x - p.from.x, p.to.y - p.from.y);
        const t = len > 0 ? Math.min(1, p.cutMm / len) : 0;
        const b = P({ x: p.from.x + (p.to.x - p.from.x) * t, y: p.from.y + (p.to.y - p.from.y) * t });
        const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
        const side = sideOf(p.side);
        const n = NORMAL[side];
        const label = { x: mid.x + n.x * 14, y: mid.y + n.y * 16 + (n.y === 0 ? 4 : n.y > 0 ? 8 : 0) };
        const anchor = n.x < 0 ? 'end' : n.x > 0 ? 'start' : 'middle';
        // the number of the piece's first LED: just before its start, outside the screen
        const dx = Math.sign(b.x - a.x);
        const dy = Math.sign(b.y - a.y);
        const firstAt = { x: a.x - dx * 6 + n.x * 12, y: a.y - dy * 8 + n.y * 18 + (n.y > 0 ? 6 : n.y < 0 ? 0 : 5) };
        return (
          <g key={`${p.label}-${p.firstLed}`}>
            <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} stroke={c.strip} strokeWidth={9} strokeLinecap="round" />
            {p.leds > 0 &&
              [0.3, 0.7].map((f) => (
                <path
                  key={f}
                  d="M -4 -5 L 3 0 L -4 5"
                  fill="none"
                  stroke={c.arrow}
                  strokeWidth={2}
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  transform={`translate(${a.x + (b.x - a.x) * f} ${a.y + (b.y - a.y) * f}) rotate(${ANGLE[arrowOf(p.arrow)]})`}
                />
              ))}
            <text x={label.x} y={label.y} textAnchor={anchor} fontSize={16} fill={c.strong} fontWeight={600}>
              {p.leds} LEDs
            </text>
            {p.leds > 0 && p.firstLed > 1 && (
              <text
                x={firstAt.x}
                y={firstAt.y}
                textAnchor={n.y !== 0 ? (dx > 0 ? 'end' : 'start') : anchor}
                fontSize={14}
                fill={c.text}
              >
                {p.firstLed}
              </text>
            )}
          </g>
        );
      })}

      {/* joints */}
      {plan.joints
        .filter((j) => j.kind === 'corner')
        .map((j) => {
          const at = P(j.at);
          return <circle key={`joint-${j.afterLed}`} cx={at.x} cy={at.y} r={5} fill={c.bg} stroke={c.joint} strokeWidth={2} />;
        })}

      {/* power feeds */}
      {plan.feeds.map((f) => {
        const at = P(f.at);
        return (
          <g key={`feed-${f.led}`} transform={`translate(${at.x} ${at.y})`}>
            <circle r={8} fill={c.bg} stroke={c.feed} strokeWidth={2} />
            <path d="M -4 0 H 4 M 0 -4 V 4" stroke={c.feed} strokeWidth={2} strokeLinecap="round" />
          </g>
        );
      })}
    </svg>
  );
}

/** What the drawing's marks mean. */
export function PlanLegend({ fromBehind }: { fromBehind: boolean }) {
  return (
    <p className="text-xs text-ink-faint">
      Arrows: the direction the data runs · circles: joints · plus: power from the supply ·{' '}
      {fromBehind ? 'seen from behind the screen, where you stick the strip on' : 'seen from your seat (left and right swap behind the screen)'}
    </p>
  );
}
