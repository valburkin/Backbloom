import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { useConnection } from '../app/connection';
import type { RpcApi } from '../api/rpcApi';
import { appendLogs, type LogState } from './logs';
import { performanceReducer, summarize, type PerformanceSummary } from './performance';
import { easeShare, previewColors } from './previewLight';

// The LED stream is per connection; several components may want it at once.
const ledStreamUsers = new WeakMap<RpcApi, number>();

/** One frame of the LED stream: the colors sent, and the share of the full output the brightness left in it. */
export interface LedFrame {
  leds: number[];
  share: number;
}

/** Latest LED frame, refreshed at most ~10 times a second. Null until the first frame. */
export function useLedFrame(enabled = true): LedFrame | null {
  const { api, backend, session, status } = useConnection();
  const [frame, setFrame] = useState<LedFrame | null>(null);
  const pending = useRef<LedFrame | null>(null);
  const live = enabled && status === 'open' && session > 0;

  useEffect(() => {
    if (!live) {
      setFrame(null);
      return;
    }
    const users = (ledStreamUsers.get(api) ?? 0) + 1;
    ledStreamUsers.set(api, users);
    if (users === 1) api.ledStream(true).catch(() => undefined);
    let timer: ReturnType<typeof setTimeout> | null = null;
    const off = backend.onEvent('ledcolors-ledstream-update', (e) => {
      const data = e.data as { leds?: unknown; share?: unknown } | undefined;
      const leds = data?.leds;
      if (!Array.isArray(leds)) return;
      pending.current = { leds: leds as number[], share: typeof data?.share === 'number' ? data.share : 1 };
      if (!timer)
        timer = setTimeout(() => {
          timer = null;
          setFrame(pending.current);
        }, 80);
    });
    return () => {
      off();
      if (timer) clearTimeout(timer);
      const left = (ledStreamUsers.get(api) ?? 1) - 1;
      ledStreamUsers.set(api, left);
      if (left === 0) api.ledStream(false).catch(() => undefined);
    };
  }, [api, backend, live, session]);

  return live ? frame : null;
}

/** The LED colors for a preview: as the LEDs light the room right now, drawn for the screen (see previewLight.ts). */
export function usePreviewFrame(enabled = true): { frame: number[] | null } {
  const raw = useLedFrame(enabled);
  const share = useEasedShare(raw?.share ?? null);
  const frame = useMemo(() => (raw ? previewColors(raw.leds, share ?? raw.share) : null), [raw, share]);
  return { frame };
}

/** The frames' share, eased toward each new value (easeShare); null without frames. */
function useEasedShare(target: number | null): number | null {
  const [shown, setShown] = useState<number | null>(target);
  const at = useRef({ share: target, time: 0 });
  useEffect(() => {
    if (target === null) {
      at.current = { share: null, time: 0 };
      setShown(null);
      return;
    }
    if (at.current.share === null || reducedMotion()) {
      at.current = { share: target, time: performance.now() };
      setShown(target);
      return;
    }
    // keep stepping until it arrives: the frames may stop coming once the colors settle
    let timer: ReturnType<typeof setTimeout> | null = null;
    const step = () => {
      const now = performance.now();
      const next = easeShare(at.current.share ?? target, target, now - at.current.time);
      at.current = { share: next, time: now };
      setShown(next);
      timer = next === target ? null : setTimeout(step, 50);
    };
    at.current = { share: at.current.share, time: performance.now() };
    timer = setTimeout(step, 50);
    return () => {
      if (timer) clearTimeout(timer);
    };
  }, [target]);
  return shown;
}

function reducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/** Live performance numbers (subscribed with the session's other events; one round is requested on mount). */
export function usePerformance(): PerformanceSummary {
  const { api, backend, session, status } = useConnection();
  const [state, dispatch] = useReducer(performanceReducer, {});
  useEffect(() => backend.onEvent('performance-update', (e) => dispatch(e.data)), [backend]);
  useEffect(() => {
    if (status === 'open' && session > 0) api.requestPerformance().catch(() => undefined);
  }, [api, session, status]);
  return summarize(state);
}

/** Live log messages (admin only). Streaming stops when `enabled` turns false or the component unmounts. */
export function useLogStream(enabled: boolean): { logs: LogState; clear(): void; error: string | null } {
  const { api, backend, session, status } = useConnection();
  const [logs, setLogs] = useState<LogState>({ lines: [], nextId: 1 });
  const [error, setError] = useState<string | null>(null);
  const live = enabled && status === 'open' && session > 0;
  useEffect(() => {
    if (!live) return;
    const off = backend.onEvent('logging-update', (e) => setLogs((s) => appendLogs(s, e.data)));
    setError(null);
    api.logStream(true).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    return () => {
      off();
      api.logStream(false).catch(() => undefined);
    };
  }, [api, backend, live, session]);
  return { logs, clear: () => setLogs((s) => ({ lines: [], nextId: s.nextId })), error };
}
