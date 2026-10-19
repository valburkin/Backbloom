import { useEffect, useState } from 'react';

export const ROUTES = ['home', 'layout', 'devices', 'settings'] as const;
export type Route = (typeof ROUTES)[number];

function parse(hash: string): Route {
  const r = hash.replace(/^#\/?/, '').split(/[/?]/)[0];
  return (ROUTES as readonly string[]).includes(r) ? (r as Route) : 'home';
}

/** Tiny hash router: #/layout, #/devices ... (works when served from any path by the backend). */
export function useRoute(): [Route, (r: Route) => void] {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash));
  useEffect(() => {
    const on = () => setRoute(parse(window.location.hash));
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [route, (r: Route) => (window.location.hash = `#/${r}`)];
}
