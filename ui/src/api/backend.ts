import { WsClient } from './client';
import { MockBackend } from './mock';
import type { Backend } from './types';

export const DEFAULT_BACKEND_PORT = 18290;

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
 * port 18290 on the same hostname.
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
    // ?mock=auth: another device, which signs in; ?mock=welcome: a new install's setup (only the test device);
    // ?mock=welcome-imported: the setup after importing HyperHDR settings; ?mock=hyperhdr: the same while HyperHDR runs;
    // ?mock=imported: no layout saved yet, only the LED positions (as after importing HyperHDR settings);
    // ?mock=offline: a device can't be reached; ?mock=presets: two presets, Gaming turned on by its running game;
    // ?mock=nousb: no new WLED board on USB;
    // ?mock=welcome-hotspot: a new install with only a new board's hotspot nearby; ?mock=location: Windows hides nearby
    // Wi-Fi networks; ?mock=noboards: no new board anywhere;
    // ?mock=existing: the reference board on the network, not in the layout yet (Add it as it is)
    const mode = new URLSearchParams(loc.search).get('mock');
    const firstRun = mode === 'welcome' || mode === 'welcome-hotspot' || mode === 'welcome-imported' || mode === 'hyperhdr';
    return new MockBackend({
      requireAuth: mode === 'auth',
      firstRun,
      fresh: mode === 'welcome' || mode === 'welcome-hotspot',
      otherApp: mode === 'hyperhdr',
      offline: mode === 'offline',
      imported: mode === 'imported',
      demoLayout: !firstRun && mode !== 'imported' && mode !== 'existing',
      existingBoard: mode === 'existing' || undefined,
      usb: mode !== 'nousb' && mode !== 'welcome-hotspot' && mode !== 'noboards',
      hotspot: mode !== 'noboards',
      locationBlocked: mode === 'location',
      presets: mode === 'presets',
      running: mode === 'presets' ? ['witcher3.exe'] : undefined,
    });
  }
  return new WsClient({ url: resolveBackendUrl(loc, env.VITE_BACKEND as string | undefined, env.DEV) });
}
