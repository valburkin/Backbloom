import { useEffect, useMemo, useRef } from 'react';

/**
 * Wraps `fn` so it runs at most once per `ms`, always delivering the latest arguments (leading and trailing call).
 * Used for live controls (color picker, brightness) that would otherwise flood the service.
 */
export function useThrottled<A extends unknown[]>(fn: (...args: A) => void, ms: number): (...args: A) => void {
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const state = useRef<{ last: number; timer: ReturnType<typeof setTimeout> | null; args: A | null }>({ last: 0, timer: null, args: null });
  useEffect(() => () => {
    if (state.current.timer) clearTimeout(state.current.timer);
  }, []);
  return useMemo(
    () =>
      (...args: A) => {
        const s = state.current;
        s.args = args;
        const wait = s.last + ms - Date.now();
        if (wait <= 0 && !s.timer) {
          s.last = Date.now();
          fnRef.current(...args);
          s.args = null;
          return;
        }
        if (!s.timer)
          s.timer = setTimeout(() => {
            s.timer = null;
            s.last = Date.now();
            if (s.args) fnRef.current(...s.args);
            s.args = null;
          }, Math.max(0, wait));
      },
    [ms],
  );
}
