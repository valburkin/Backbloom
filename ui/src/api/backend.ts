import { WsClient } from './client';
import { MockBackend } from './mock';
import type { Backend } from './types';

export const DEFAULT_BACKEND_PORT = 8090;

interface LocationLike {
  protocol: string;
  host: string;
  hostname: string;
  search: string;
}

/** Normalize "host", "host:port", "http(s)://host:port" or "ws(s)://host:port" into a WebSocket URL. */
export function normalizeBackendUrl(value: string, secure = false): string {
  const v = value.trim().replace(/\/+$/, '');
  if (/^wss?:\/\//i.test(v)) return v;
  if (/^https:\/\//i.test(v)) return 'wss://' + v.slice(8);
  if (/^http:\/\//i.test(v)) return 'ws://' + v.slice(7);
  return `${secure ? 'wss' : 'ws'}://${v}`;
}

/**
 * Backend URL priority: `?backend=host:port` → VITE_BACKEND → the page's own host (the backend serves this UI
 * and its WebSocket on the same port). In the Vite dev server the page host is Vite itself, so dev falls back to
 * port 8090 on the same hostname.
 */
export function resolveBackendUrl(loc: LocationLike, envBackend?: string, isDev = false): string {
  const secure = loc.protocol === 'https:';
  const fromQuery = new URLSearchParams(loc.search).get('backend');
  if (fromQuery) return normalizeBackendUrl(fromQuery, secure);
  if (envBackend) return normalizeBackendUrl(envBackend, secure);
  if (isDev) return `${secure ? 'wss' : 'ws'}://${loc.hostname}:${DEFAULT_BACKEND_PORT}`;
  return `${secure ? 'wss' : 'ws'}://${loc.host}`;
}

export function isMockEnabled(loc: LocationLike, envMock?: string): boolean {
  return envMock === '1' || new URLSearchParams(loc.search).has('mock');
}

export function createBackend(): Backend {
  const loc = window.location;
  const env = import.meta.env;
  if (isMockEnabled(loc, env.VITE_MOCK as string | undefined)) {
    const mode = new URLSearchParams(loc.search).get('mock');
    return new MockBackend({ requireAuth: mode === 'auth' });
  }
  return new WsClient({ url: resolveBackendUrl(loc, env.VITE_BACKEND as string | undefined, env.DEV) });
}
