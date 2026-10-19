import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Pause, Play, X } from 'lucide-react';
import { Segmented } from '../../components/ui';

type Mode = 'rainbow' | 'edges';
const EDGE_COLORS = { top: '#ff2a2a', right: '#22ff44', bottom: '#2a5bff', left: '#ffd60a' } as const;
const SECONDS_PER_TURN = 24;

/**
 * Fullscreen test pattern. This UI runs on the captured PC, so the lights mirror what's drawn here:
 * a slowly rotating rainbow around the border (checks direction) or one solid color per edge (checks placement).
 */
export function TestPattern({ onClose }: { onClose: () => void }) {
  const root = useRef<HTMLDivElement>(null);
  const band = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<Mode>('rainbow');
  const [paused, setPaused] = useState(false);
  const angle = useRef(0);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  // Fullscreen + Esc handling.
  useEffect(() => {
    const el = root.current;
    el?.focus();
    el?.requestFullscreen?.().catch(() => undefined);
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onCloseRef.current();
    };
    const onFs = () => {
      if (!document.fullscreenElement) onCloseRef.current();
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('fullscreenchange', onFs);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('fullscreenchange', onFs);
      if (document.fullscreenElement) document.exitFullscreen().catch(() => undefined);
    };
  }, []);

  // Rotate the rainbow.
  useEffect(() => {
    if (mode !== 'rainbow' || paused) return;
    let raf = 0;
    let last = performance.now();
    const tick = (t: number) => {
      angle.current = (angle.current + ((t - last) / 1000) * (360 / SECONDS_PER_TURN)) % 360;
      last = t;
      band.current?.style.setProperty('--a', `${angle.current}deg`);
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [mode, paused]);

  const label = 'absolute text-sm font-bold tracking-[0.3em] text-white [text-shadow:0_1px_6px_rgba(0,0,0,0.9)]';

  return createPortal(
    <div ref={root} tabIndex={-1} role="dialog" aria-modal="true" aria-label="Test pattern" className="fixed inset-0 z-[60] bg-black outline-none">
      {mode === 'rainbow' ? (
        <div
          ref={band}
          className="absolute inset-0"
          style={{
            background:
              'conic-gradient(from var(--a, 0deg) at 50% 50%, #ff0000, #ffff00, #00ff00, #00ffff, #0000ff, #ff00ff, #ff0000)',
          }}
        />
      ) : (
        <div className="absolute inset-0">
          {/* Four trapezoids meeting at the corners */}
          <div className="absolute inset-0" style={{ background: EDGE_COLORS.top, clipPath: 'polygon(0 0, 100% 0, 50% 50%)' }} />
          <div className="absolute inset-0" style={{ background: EDGE_COLORS.right, clipPath: 'polygon(100% 0, 100% 100%, 50% 50%)' }} />
          <div className="absolute inset-0" style={{ background: EDGE_COLORS.bottom, clipPath: 'polygon(0 100%, 100% 100%, 50% 50%)' }} />
          <div className="absolute inset-0" style={{ background: EDGE_COLORS.left, clipPath: 'polygon(0 0, 0 100%, 50% 50%)' }} />
        </div>
      )}
      {/* Black center leaves a colored border band */}
      <div className="absolute inset-[22%_18%] rounded-3xl bg-black" />

      <span className={`${label} left-1/2 top-[6%] -translate-x-1/2`}>TOP</span>
      <span className={`${label} bottom-[6%] left-1/2 -translate-x-1/2`}>BOTTOM</span>
      <span className={`${label} left-[4%] top-1/2 -translate-y-1/2 -rotate-90`}>LEFT</span>
      <span className={`${label} right-[4%] top-1/2 -translate-y-1/2 rotate-90`}>RIGHT</span>

      <div className="absolute left-1/2 top-1/2 w-[min(32rem,56vw)] -translate-x-1/2 -translate-y-1/2 text-center text-white">
        <h2 className="text-2xl font-semibold">Test pattern</h2>
        <p className="mt-3 text-[15px] leading-relaxed text-white/70">
          {mode === 'rainbow'
            ? 'Colors travel clockwise around the screen. Your lights should follow the same way — if a strip moves the other way, reverse its direction in the layout.'
            : 'Each edge has its own color: top red, right green, bottom blue, left yellow. Check that every LED shows the color of the edge it sits behind.'}
        </p>
        <div className="mt-6 flex items-center justify-center gap-3">
          <Segmented
            label="Pattern"
            value={mode}
            onChange={setMode}
            options={[
              { value: 'rainbow', label: 'Moving rainbow' },
              { value: 'edges', label: 'Color per edge' },
            ]}
          />
          {mode === 'rainbow' && (
            <button
              type="button"
              onClick={() => setPaused((p) => !p)}
              className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-white/20 px-3 text-sm text-white/90 hover:bg-white/10"
            >
              {paused ? <Play size={15} /> : <Pause size={15} />}
              {paused ? 'Resume' : 'Pause'}
            </button>
          )}
          <button
            type="button"
            onClick={onClose}
            className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-white px-3 text-sm font-medium text-black hover:bg-white/90"
          >
            <X size={15} /> Exit
          </button>
        </div>
        <p className="mt-4 text-xs text-white/40">Press Esc to exit</p>
      </div>
    </div>,
    document.body,
  );
}
