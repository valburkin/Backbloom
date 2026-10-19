import { useEffect, useState } from 'react';

export const ROUTES = ['home', 'layout', 'devices', 'capture', 'colors', 'effects', 'settings'] as const;
export type Route = (typeof ROUTES)[number];

export interface Location {
  route: Route;
  /** Second path segment, e.g. "logs" in #/settings/logs. */
  sub: string | null;
}

export function parseHash(hash: string): Location {
  const [r, sub] = hash.replace(/^#\/?/, '').split('?')[0].split('/');
  const route = (ROUTES as readonly string[]).includes(r) ? (r as Route) : 'home';
  return { route, sub: route === r && sub ? sub : null };
}

export function hrefFor(route: Route, sub?: string | null): string {
  return `#/${route}${sub ? `/${sub}` : ''}`;
}

/** Tiny hash router: #/layout, #/settings/logs ... (works when served from any path by the backend). */
export function useRoute(): [Location, (route: Route, sub?: string | null) => void] {
  const [loc, setLoc] = useState<Location>(() => parseHash(window.location.hash));
  useEffect(() => {
    const on = () => setLoc(parseHash(window.location.hash));
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [loc, (route, sub) => (window.location.hash = hrefFor(route, sub))];
}
