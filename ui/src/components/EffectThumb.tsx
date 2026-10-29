import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import type { RpcApi } from '../api/rpcApi';
import type { EffectPreviews } from '../api/types';
import type { LayoutLightsHandle } from './LayoutLights';

const cache = new Map<string, Promise<EffectPreviews | null>>();
let cacheOwner: RpcApi | null = null;

/** One effect's preview, fetched once per connection and layout; null when there is none (an older backend). */
function fetchPreview(api: RpcApi, layoutKey: string, name: string): Promise<EffectPreviews | null> {
  if (cacheOwner !== api) {
    cache.clear();
    cacheOwner = api;
  }

  const key = `${layoutKey}\n${name}`;
  let promise = cache.get(key);
  if (!promise) {
    promise = api.effectPreviews(name).catch(() => null);
    cache.set(key, promise);
  }

  return promise;
}

// One animation loop for every thumbnail on the page.
const painters = new Set<(now: number) => void>();
let loop = 0;
function tick(now: number) {
  painters.forEach((paint) => paint(now));
  loop = painters.size ? requestAnimationFrame(tick) : 0;
}
function animate(paint: (now: number) => void): () => void {
  painters.add(paint);
  if (!loop) loop = requestAnimationFrame(tick);
  return () => {
    painters.delete(paint);
  };
}

function reducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * The LEDs in their order as one strip of light: each LED one pixel of a small canvas that the browser stretches into
 * a smooth band, over a blurred copy for the glow. Compact enough for a dense grid, and the same for every layout.
 */
const StripLights = forwardRef<LayoutLightsHandle>(function StripLights(_, ref) {
  const strip = useRef<HTMLCanvasElement>(null);
  const glow = useRef<HTMLCanvasElement>(null);
  const image = useRef<ImageData | null>(null);
  useImperativeHandle(
    ref,
    () => ({
      paint(colors, offset = 0, count = 0) {
        const canvas = strip.current;
        const ctx = canvas?.getContext('2d');
        if (!canvas || !ctx || !colors || count <= 0) return;
        if (canvas.width !== count) canvas.width = count;
        if (glow.current && glow.current.width !== count) glow.current.width = count;
        if (!image.current || image.current.width !== count) image.current = ctx.createImageData(count, 1);
        const px = image.current.data;
        for (let i = 0; i < count; i++) {
          const s = offset + i * 3;
          px[i * 4] = colors[s] ?? 0;
          px[i * 4 + 1] = colors[s + 1] ?? 0;
          px[i * 4 + 2] = colors[s + 2] ?? 0;
          px[i * 4 + 3] = 255;
        }
        ctx.putImageData(image.current, 0, 0);
        const g = glow.current?.getContext('2d');
        if (g) {
          g.clearRect(0, 0, count, 1);
          g.drawImage(canvas, 0, 0);
        }
      },
    }),
    [],
  );
  return (
    <div className="relative h-12 w-full overflow-hidden rounded-lg bg-[#07080c]" aria-hidden>
      <canvas ref={glow} width={1} height={1} className="absolute inset-x-1 inset-y-2 h-[calc(100%-1rem)] w-[calc(100%-0.5rem)] opacity-80 blur-md saturate-150" />
      <canvas ref={strip} width={1} height={1} className="absolute inset-x-3 top-1/2 h-1.5 w-[calc(100%-1.5rem)] -translate-y-1/2 rounded-full" />
    </div>
  );
});

/**
 * The effect's preview looping as a strip of light (see StripLights). The preview is fetched when the thumbnail comes
 * into view; it animates only while visible, and with reduced motion it shows one still frame.
 */
export function EffectThumb({ api, name, layoutKey }: { api: RpcApi; name: string; layoutKey: string }) {
  const box = useRef<HTMLDivElement>(null);
  const lights = useRef<LayoutLightsHandle>(null);
  const [visible, setVisible] = useState(false);
  const [preview, setPreview] = useState<EffectPreviews | null | undefined>(undefined);
  const frames = preview?.frames.get(name);

  useEffect(() => {
    if (!box.current || typeof IntersectionObserver !== 'function') {
      setVisible(true);
      return;
    }

    const observer = new IntersectionObserver(([entry]) => setVisible(!!entry?.isIntersecting), { rootMargin: '200px' });
    observer.observe(box.current);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (!visible) return;
    let live = true;
    void fetchPreview(api, layoutKey, name).then((p) => {
      if (live) setPreview(p);
    });
    return () => {
      live = false;
    };
  }, [api, layoutKey, name, visible]);

  useEffect(() => {
    if (!preview || !frames) return;
    const size = preview.ledCount * 3;
    const count = size ? Math.floor(frames.length / size) : 0;
    if (!count) return;
    const paint = (frame: number) => lights.current?.paint(frames, frame * size, preview.ledCount);
    if (reducedMotion()) {
      paint(Math.floor(count * 0.4));
      return;
    }

    let current = -1;
    const step = (now: number) => {
      const frame = Math.floor((now / 1000) * preview.fps) % count;
      if (frame !== current) {
        current = frame;
        paint(frame);
      }
    };
    step(performance.now());
    return visible ? animate(step) : undefined;
  }, [preview, frames, visible]);

  return (
    <div ref={box}>
      {frames ? (
        <StripLights ref={lights} />
      ) : (
        <div className="relative h-12 w-full overflow-hidden rounded-lg bg-[#07080c]" aria-hidden>
          <div className={`absolute inset-x-3 top-1/2 h-1.5 -translate-y-1/2 rounded-full bg-[#1c2130] ${preview === undefined ? 'motion-safe:animate-pulse' : ''}`} />
        </div>
      )}
    </div>
  );
}
