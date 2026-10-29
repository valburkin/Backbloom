import { useEffect, useState } from 'react';

export const ROUTES = ['home', 'layout', 'devices', 'capture', 'colors', 'effects', 'settings'] as const;
export type Route = (typeof ROUTES)[number];

export interface Location {
  route: Route;
  /** Second path segment, e.g. "support" in #/settings/support. */
  sub: string | null;
  /** A control to show, e.g. "crop" in #/capture?focus=crop (see {@link revealFocus}). */
  focus: string | null;
}

export function parseHash(hash: string): Location {
  const [path, query = ''] = hash.replace(/^#\/?/, '').split('?');
  const [r, sub] = path.split('/');
  const route = (ROUTES as readonly string[]).includes(r) ? (r as Route) : 'home';
  const focus = new URLSearchParams(query).get('focus');
  return { route, sub: route === r && sub ? sub : null, focus: focus || null };
}

export function hrefFor(route: Route, sub?: string | null, focus?: string | null): string {
  return `#/${route}${sub ? `/${sub}` : ''}${focus ? `?focus=${encodeURIComponent(focus)}` : ''}`;
}

/**
 * Shows the control `id` names: the element with that id, else the first with `data-focus="<id>"`. Opens the
 * disclosures around it (More options) and, with `data-focus-open`, its own first one; scrolls it into view and
 * highlights it briefly.
 * False while it isn't rendered (yet).
 */
export function revealFocus(id: string, root: Document = document): boolean {
  const el = root.getElementById(id) ?? root.querySelector<HTMLElement>(`[data-focus="${CSS.escape(id)}"]`);
  if (!el) return false;
  for (let d = el.closest('details'); d; d = d.parentElement?.closest('details') ?? null) d.open = true;
  const own = el instanceof HTMLDetailsElement ? el : el.hasAttribute('data-focus-open') ? el.querySelector('details') : null;
  if (own) own.open = true;
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  el.scrollIntoView({ block: 'start', behavior: reduced ? 'auto' : 'smooth' });
  el.animate?.(
    [{ boxShadow: '0 0 0 2px var(--color-accent)', borderRadius: '12px' }, { boxShadow: '0 0 0 2px transparent', borderRadius: '12px' }],
    { duration: 2000, easing: 'ease-in' },
  );
  return true;
}

/** Follows a link to a control (`?focus=`): waits up to 4 s for the page to render it, then shows it. */
export function useFocusTarget(loc: Location): void {
  useEffect(() => {
    const id = loc.focus;
    if (!id) return;
    let tries = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const attempt = () => {
      if (revealFocus(id) || ++tries >= 40) return;
      timer = setTimeout(attempt, 100);
    };
    timer = setTimeout(attempt, 0);
    return () => clearTimeout(timer);
  }, [loc.route, loc.sub, loc.focus]);
}

/** Opens `href` (a `#/…` link); shows its control again when the page is already there. */
export function openHref(href: string): void {
  if (window.location.hash === href) {
    const { focus } = parseHash(href);
    if (focus) revealFocus(focus);
  } else window.location.hash = href;
}

/** Tiny hash router: #/layout, #/settings/support, #/capture?focus=crop ... (works when served from any path by the backend). */
export function useRoute(): [Location, (route: Route, sub?: string | null) => void] {
  const [loc, setLoc] = useState<Location>(() => parseHash(window.location.hash));
  useEffect(() => {
    const on = () => setLoc(parseHash(window.location.hash));
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [loc, (route, sub) => (window.location.hash = hrefFor(route, sub))];
}
