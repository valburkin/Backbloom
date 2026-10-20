import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import type { LayoutView } from '../live/layoutView';

// The glow is drawn at this fraction of the view's size and scaled up by the browser, which softens it like light on
// a wall.
const GLOW_SCALE = 0.25;
// The dots are drawn at twice the view's size, so they stay sharp on large cards and high-density screens.
const DOT_SCALE = 2;
const UNLIT = 'rgb(30,34,46)';

export interface LayoutLightsHandle {
  /** Shows LED colors: a flat [r, g, b, ...] list in the order of `leds`, starting at `offset`; missing LEDs are unlit. */
  paint(colors: ArrayLike<number> | null, offset?: number, count?: number): void;
}

/**
 * The user's LEDs around a screen, drawn as on the Layout page (each strip outside the screen edge it samples, strips
 * on the same edge in their own lanes), each LED as a dot with a soft glow. Painted imperatively, so animations don't
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
        const halo = Math.max(view.spacing * 1.1, 8) * GLOW_SCALE;
        glow.clearRect(0, 0, glow.canvas.width, glow.canvas.height);
        dots.setTransform(DOT_SCALE, 0, 0, DOT_SCALE, 0, 0);
        dots.clearRect(0, 0, view.width, view.height);
        for (const { x, y, led } of view.leds) {
          const i = offset + led * 3;
          const lit = !!colors && led < count && i + 2 < colors.length;
          const color = lit ? `rgb(${colors[i]},${colors[i + 1]},${colors[i + 2]})` : UNLIT;
          if (lit) {
            glow.fillStyle = color;
            glow.beginPath();
            glow.arc(x * GLOW_SCALE, y * GLOW_SCALE, halo, 0, 2 * Math.PI);
            glow.fill();
          }

          dots.fillStyle = color;
          dots.beginPath();
          dots.arc(x, y, r, 0, 2 * Math.PI);
          dots.fill();
          if (lit) {
            // a lighter core, so each LED reads as a point of light in its glow
            dots.fillStyle = 'rgba(255,255,255,0.35)';
            dots.beginPath();
            dots.arc(x, y, r * 0.45, 0, 2 * Math.PI);
            dots.fill();
          }
        }
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
        className="absolute inset-0 h-full w-full opacity-90 blur-[3px] saturate-150"
      />
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

/** The live LED colors on the user's layout (the Preview cards). */
export function LiveLayoutLights({ view, frame, label = 'Live LED preview' }: { view: LayoutView; frame: number[] | null; label?: string }) {
  const lights = useRef<LayoutLightsHandle>(null);
  useEffect(() => {
    lights.current?.paint(frame);
  }, [frame, view]);
  return <LayoutLights ref={lights} view={view} label={label} />;
}
