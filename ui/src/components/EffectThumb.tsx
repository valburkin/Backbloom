import { useEffect, useRef, useState } from 'react';
import type { RpcApi } from '../api/rpcApi';
import type { EffectPreviews } from '../api/types';
import { buildLayoutView, standardFrameSpec, type LayoutView } from '../live/layoutView';
import { LayoutLights, type LayoutLightsHandle } from './LayoutLights';

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

let standardView: LayoutView | null = null;
function standardFrame(): LayoutView {
  return (standardView ??= buildLayoutView(standardFrameSpec()));
}

/**
 * The effect's preview looping on the user's LEDs (see LayoutLights). The preview is fetched when the thumbnail comes
 * into view; it animates only while visible, and with reduced motion it shows one still frame.
 */
export function EffectThumb({ api, name, view, layoutKey }: { api: RpcApi; name: string; view: LayoutView | null; layoutKey: string }) {
  const box = useRef<HTMLDivElement>(null);
  const lights = useRef<LayoutLightsHandle>(null);
  const [visible, setVisible] = useState(false);
  const [preview, setPreview] = useState<EffectPreviews | null | undefined>(undefined);
  const shown = preview?.standard ? standardFrame() : view;
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
    if (!preview || !frames || !shown) return;
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
  }, [preview, frames, shown, visible]);

  return (
    <div ref={box}>
      {shown && frames ? (
        <LayoutLights ref={lights} view={shown} />
      ) : (
        <div className="relative aspect-video w-full overflow-hidden rounded-lg bg-[#07080c]" aria-hidden>
          <div className={`absolute inset-[18%] rounded-[3px] bg-[#0d1017] ${preview === undefined ? 'animate-pulse' : ''}`} />
        </div>
      )}
    </div>
  );
}
