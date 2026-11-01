import { useEffect, useReducer, useRef, useState } from 'react';
import { useConnection } from '../app/connection';
import type { RpcApi } from '../api/rpcApi';
import { appendLogs, type LogState } from './logs';
import { performanceReducer, summarize, type PerformanceSummary } from './performance';

// The LED stream is per connection; several components may want it at once.
const ledStreamUsers = new WeakMap<RpcApi, number>();

/** Latest LED colors as a flat [r,g,b,...] array, refreshed at most ~10 times a second. Null until the first frame. */
export function useLedFrame(enabled = true): number[] | null {
  const { api, backend, session, status } = useConnection();
  const [frame, setFrame] = useState<number[] | null>(null);
  const pending = useRef<number[] | null>(null);
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
      const leds = (e.data as { leds?: unknown })?.leds;
      if (!Array.isArray(leds)) return;
      pending.current = leds as number[];
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
