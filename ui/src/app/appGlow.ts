import { useEffect, useRef } from 'react';

const VARS = ['--glow-l', '--glow-m', '--glow-r'] as const;
/** How often the glow takes new colors: each change fades over a second (.app-glow), and repaints while it does. */
const UPDATE_MS = 1000;

function glowElement(): HTMLElement | null {
  return document.getElementById('main');
}

function apply(el: HTMLElement, colors: readonly string[] | null) {
  if (!colors) {
    el.removeAttribute('data-live');
    VARS.forEach((v) => el.style.removeProperty(v));
    return;
  }
  el.setAttribute('data-live', '');
  colors.forEach((c, i) => el.style.setProperty(VARS[i], c));
}

/**
 * Tints the page glow (.app-glow on the main area) with the given left, middle and right colors while the calling
 * page is shown: Home passes what the lights show right now. Takes the latest colors once a second; null, reduced
 * motion or unmounting keeps the brand colors.
 */
export function useAppGlow(colors: readonly [string, string, string] | null) {
  const latest = useRef(colors);
  useEffect(() => {
    latest.current = colors;
  });
  useEffect(() => {
    const el = glowElement();
    if (!el || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;
    let shown = '';
    const update = () => {
      const key = latest.current?.join('|') ?? '';
      if (key === shown) return;
      shown = key;
      apply(el, latest.current);
    };
    update();
    const timer = setInterval(update, UPDATE_MS);
    return () => {
      clearInterval(timer);
      apply(el, null);
    };
  }, []);
}
