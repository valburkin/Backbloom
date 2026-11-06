import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef } from 'react';
import type { LayoutView } from '../live/layoutView';

// The glow is drawn at this fraction of the view's size and scaled up by the browser, which softens it like light on
// a wall.
const GLOW_SCALE = 0.5;
// The dots are drawn at twice the view's size, so they stay sharp on large cards and high-density screens.
const DOT_SCALE = 2;
const UNLIT = 'rgb(30,34,46)';

// How light falls off around an LED (fractions of the radius → share of the center's strength), close to a lamp's
// light on a wall: bright near it, then a long soft tail.
const FALLOFF: ReadonlyArray<readonly [number, number]> = [
  [0, 1],
  [0.25, 0.45],
  [0.5, 0.15],
  [1, 0],
];
// Along a strip the glows of neighboring LEDs add up to about this many times one LED's center strength per radius
// covered: twice the area under FALLOFF.
const OVERLAP = 0.58;

/**
 * One layer of light around the LEDs, added up like light (overlapping LEDs mix, a red next to a blue gives purple):
 * each LED's own color and brightness, falling off over `radius`. `strength` is how bright the middle of a lit strip's
 * glow gets, whatever the LED density.
 */
function addLight(ctx: CanvasRenderingContext2D, x: number, y: number, rgb: readonly [number, number, number], radius: number, spacing: number, strength: number) {
  const a = Math.min(strength, (strength * spacing) / (OVERLAP * radius));
  const g = ctx.createRadialGradient(x, y, 0, x, y, radius);
  for (const [at, k] of FALLOFF) g.addColorStop(at, `rgba(${rgb[0]},${rgb[1]},${rgb[2]},${(a * k).toFixed(4)})`);
  ctx.fillStyle = g;
  ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2);
}

export interface LayoutLightsHandle {
  /** Shows LED colors: a flat [r, g, b, ...] list in the order of `leds`, starting at `offset`; missing LEDs are unlit. */
  paint(colors: ArrayLike<number> | null, offset?: number, count?: number): void;
}

/**
 * The user's LEDs in their room, drawn as on the Layout page (the walls, ceiling and floor folded out around the
 * front wall with the screen), each LED as a dot with a soft glow. Painted imperatively, so animations don't
 * re-render React.
 */
export const LayoutLights = forwardRef<LayoutLightsHandle, { view: LayoutView; label?: string }>(function LayoutLights({ view, label }, ref) {
  const glowRef = useRef<HTMLCanvasElement>(null);
  const dotsRef = useRef<HTMLCanvasElement>(null);

  useImperativeHandle(
    ref,
    () => ({
      paint(colors, offset = 0, count = Infinity) {
        const glow = glowRef.current?.getContext('2d');
        const dots = dotsRef.current?.getContext('2d');
        if (!glow || !dots) return;
        const r = Math.min(Math.max(view.spacing * 0.3, 1.6), 4.5);
        const spacing = Math.max(view.spacing, 1) * GLOW_SCALE;
        // the light right around each LED, and the wide, faint wash it throws on the wall
        const near = Math.max(view.spacing * 3, 10) * GLOW_SCALE;
        const wash = Math.max(view.spacing * 11, 36) * GLOW_SCALE;
        glow.globalCompositeOperation = 'source-over';
        glow.clearRect(0, 0, glow.canvas.width, glow.canvas.height);
        glow.globalCompositeOperation = 'lighter';
        // The wash comes from groups of a few neighboring LEDs, each drawn once with their average light from their
        // middle: one LED's share of so wide a glow is below the canvas's color steps and would band. A group ends at a
        // strip's end (the next LED is far away), so short strips and lone room lights get their own wash.
        const washEvery = Math.max(1, Math.floor(wash / (spacing * 2.5)));
        const group = { n: 0, x: 0, y: 0, r: 0, g: 0, b: 0, px: NaN, py: NaN };
        const flush = () => {
          if (group.n > 0 && group.r + group.g + group.b > 0) {
            const rgb = [Math.round(group.r / group.n), Math.round(group.g / group.n), Math.round(group.b / group.n)] as const;
            addLight(glow, (group.x / group.n) * GLOW_SCALE, (group.y / group.n) * GLOW_SCALE, rgb, wash, spacing * group.n, 0.16);
          }
          group.n = group.x = group.y = group.r = group.g = group.b = 0;
        };
        dots.setTransform(DOT_SCALE, 0, 0, DOT_SCALE, 0, 0);
        dots.clearRect(0, 0, view.width, view.height);
        for (const { x, y, led } of view.leds) {
          const i = offset + led * 3;
          const inFrame = !!colors && led < count && i + 2 < colors.length;
          const rgb = inFrame ? ([colors[i], colors[i + 1], colors[i + 2]] as const) : null;
          const peak = rgb ? Math.max(rgb[0], rgb[1], rgb[2]) : 0;
          if (rgb && peak > 0) addLight(glow, x * GLOW_SCALE, y * GLOW_SCALE, rgb, near, spacing, 0.7);
          if (group.n >= washEvery || Math.hypot(x - group.px, y - group.py) > view.spacing * 2.5) flush();
          group.n++;
          group.x += x;
          group.y += y;
          if (rgb) {
            group.r += rgb[0];
            group.g += rgb[1];
            group.b += rgb[2];
          }
          group.px = x;
          group.py = y;

          // the LED itself in its own color; an LED that is off (or not in the frame) shows as an unlit one
          dots.fillStyle = rgb && peak > 0 ? `rgb(${rgb[0]},${rgb[1]},${rgb[2]})` : UNLIT;
          dots.beginPath();
          dots.arc(x, y, r, 0, 2 * Math.PI);
          dots.fill();
        }
        flush();
      },
    }),
    [view],
  );

  const { width, height, screen } = view;
  return (
    <div
      className="relative w-full overflow-hidden rounded-lg bg-[#07080c]"
      style={{ aspectRatio: `${width} / ${height}` }}
      role={label ? 'img' : undefined}
      aria-label={label ? `${label}: ${view.leds.length} LEDs` : undefined}
      aria-hidden={label ? undefined : true}
    >
      <canvas
        ref={glowRef}
        width={Math.ceil(width * GLOW_SCALE)}
        height={Math.ceil(height * GLOW_SCALE)}
        className="absolute inset-0 h-full w-full blur-[2px]"
      />
      {view.surfaces.map((r, i) => (
        <div
          key={i}
          className="absolute border border-[#1c2130]"
          style={{
            left: `${(r.x / width) * 100}%`,
            top: `${(r.y / height) * 100}%`,
            width: `${(r.w / width) * 100}%`,
            height: `${(r.h / height) * 100}%`,
          }}
        />
      ))}
      <div
        className="absolute rounded-[3px] border border-[#343c4f]/70 bg-[#0d1017]"
        style={{
          left: `${(screen.x / width) * 100}%`,
          top: `${(screen.y / height) * 100}%`,
          width: `${(screen.w / width) * 100}%`,
          height: `${(screen.h / height) * 100}%`,
        }}
      />
      <canvas ref={dotsRef} width={width * DOT_SCALE} height={height * DOT_SCALE} className="absolute inset-0 h-full w-full" />
    </div>
  );
});

/** The live LED colors on the user's layout (the Preview cards); `compact` squeezes out the empty wall between strips. */
export function LiveLayoutLights({ view, frame, label = 'Live LED preview', compact }: { view: LayoutView; frame: number[] | null; label?: string; compact?: boolean }) {
  const lights = useRef<LayoutLightsHandle>(null);
  const shown = useMemo(() => (compact ? compactLayoutView(view) : view), [compact, view]);
  useEffect(() => {
    lights.current?.paint(frame);
  }, [frame, shown]);
  return <LayoutLights ref={lights} view={shown} label={label} />;
}

/**
 * Maps one axis so that only the bands with something in them keep their size: each interval (an LED or the screen,
 * with a margin) is kept, the empty stretches between them shrink to at most `gap`, the margins outside are cut.
 */
function squeezeAxis(bands: Array<[number, number]>, gap: number): { map: (v: number) => number; size: number } {
  const merged: Array<[number, number]> = [];
  for (const [a, b] of [...bands].sort((x, y) => x[0] - y[0])) {
    const last = merged[merged.length - 1];
    if (last && a <= last[1] + gap) last[1] = Math.max(last[1], b);
    else merged.push([a, b]);
  }
  // start of each kept band in the squeezed axis
  const starts: number[] = [];
  let pos = 0;
  merged.forEach(([a, b], i) => {
    if (i > 0) pos += gap;
    starts.push(pos);
    pos += b - a;
  });
  const map = (v: number) => {
    if (!merged.length) return v;
    if (v <= merged[0][0]) return 0;
    for (let i = 0; i < merged.length; i++) {
      const [a, b] = merged[i];
      if (v <= b) return starts[i] + Math.max(0, v - a);
      const next = merged[i + 1];
      if (next && v < next[0]) return starts[i] + (b - a) + ((v - b) / (next[0] - b)) * gap;
    }
    return pos;
  };
  return { map, size: Math.max(pos, 1) };
}

/**
 * The view without the empty wall between distant strips (a ceiling strip high above the screen, say): rows and
 * columns with no LED and no screen in them shrink to a small gap, so the strips fill the preview. The faint wall
 * outlines are left out, since they no longer line up.
 */
export function compactLayoutView(view: LayoutView): LayoutView {
  if (!view.leds.length) return view;
  const pad = Math.max(view.spacing * 1.5, 12);
  const gap = pad * 1.5;
  const s = view.screen;
  const xs = squeezeAxis([...view.leds.map((l): [number, number] => [l.x - pad, l.x + pad]), [s.x - pad, s.x + s.w + pad]], gap);
  const ys = squeezeAxis([...view.leds.map((l): [number, number] => [l.y - pad, l.y + pad]), [s.y - pad, s.y + s.h + pad]], gap);
  if (ys.size >= view.height * 0.9 && xs.size >= view.width * 0.9) return view;
  const x0 = xs.map(s.x);
  const y0 = ys.map(s.y);
  return {
    width: Math.ceil(xs.size),
    height: Math.ceil(ys.size),
    screen: { x: x0, y: y0, w: xs.map(s.x + s.w) - x0, h: ys.map(s.y + s.h) - y0 },
    surfaces: [],
    leds: view.leds.map((l) => ({ led: l.led, x: xs.map(l.x), y: ys.map(l.y) })),
    spacing: view.spacing,
  };
}
