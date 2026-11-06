import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { cx, FLOATING } from './ui';

/** Space between the button and its menu, and between the menu and the window's edge (px). */
const GAP = 4;
const MARGIN = 8;

type Box = { left: number; top: number; right: number; bottom: number };

/**
 * Where a menu of `width` x `height` goes for a button at `anchor` in a `viewport` (px): below the button, or above it
 * when it doesn't fit below and there's more room above; aligned with the button's right or left edge, kept inside the
 * window, and capped (it scrolls) when taller than the room it has.
 */
export function placeMenu(
  anchor: Box,
  size: { width: number; height: number },
  viewport: { width: number; height: number },
  align: 'left' | 'right',
): { left: number; top?: number; bottom?: number; maxHeight: number } {
  const left = Math.max(MARGIN, Math.min(align === 'right' ? anchor.right - size.width : anchor.left, viewport.width - size.width - MARGIN));
  const below = Math.max(0, viewport.height - anchor.bottom - GAP - MARGIN);
  const above = Math.max(0, anchor.top - GAP - MARGIN);
  return size.height <= below || below >= above
    ? { left, top: anchor.bottom + GAP, maxHeight: below }
    : { left, bottom: viewport.height - anchor.top + GAP, maxHeight: above };
}

const items = (menu: HTMLElement) => Array.from(menu.querySelectorAll<HTMLElement>('[role="menuitem"]:not(:disabled)'));

/**
 * A menu for the button inside `anchor`, shown while it is mounted. It's drawn over the page, so panels that scroll
 * don't clip it. It closes on a click outside, Escape, Tab, a scroll or a resize; arrow keys move between its items,
 * and focus goes back to the button when it closes.
 */
export function Menu({
  anchor,
  align = 'right',
  maxHeight = Infinity,
  className,
  onClose,
  children,
}: {
  /** The button's wrapper: clicks inside it don't count as outside, and the menu lines up with it. */
  anchor: RefObject<HTMLElement | null>;
  align?: 'left' | 'right';
  /** At most this tall (px) even when there's more room; it scrolls beyond. */
  maxHeight?: number;
  className?: string;
  onClose(): void;
  children: ReactNode;
}) {
  const menu = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<CSSProperties>();
  const close = useRef(onClose);
  close.current = onClose;

  // Placed before the first paint, and again when what it shows changes size (a list that loads).
  useLayoutEffect(() => {
    const el = menu.current;
    const inner = content.current;
    const place = () => {
      if (!el || !inner || !anchor.current) return;
      const border = el.offsetHeight - el.clientHeight;
      const size = { width: el.offsetWidth, height: Math.min(inner.offsetHeight + border, maxHeight) };
      const p = placeMenu(anchor.current.getBoundingClientRect(), size, { width: window.innerWidth, height: window.innerHeight }, align);
      setPos({ ...p, maxHeight: Math.min(p.maxHeight, maxHeight) });
    };
    place();
    if (!inner) return;
    const observer = new ResizeObserver(place);
    observer.observe(inner);
    return () => observer.disconnect();
  }, [anchor, align, maxHeight]);

  // Focus moves into the menu once it's placed (it's hidden until then) and back to the button when it closes from
  // inside (a layout effect, so the menu is still in the page when it closes).
  const placed = pos !== undefined;
  useLayoutEffect(() => {
    if (placed && menu.current) items(menu.current)[0]?.focus();
  }, [placed]);
  useLayoutEffect(() => {
    const el = menu.current;
    const button = anchor.current;
    return () => {
      if (el?.contains(document.activeElement)) button?.querySelector<HTMLElement>('button')?.focus();
    };
  }, [anchor]);

  useEffect(() => {
    const el = menu.current;
    const button = anchor.current;
    const inside = (t: EventTarget | null) => t instanceof Node && (!!button?.contains(t) || !!el?.contains(t));
    const onDown = (e: MouseEvent) => !inside(e.target) && close.current();
    const onKey = (e: globalThis.KeyboardEvent) => e.key === 'Escape' && close.current();
    // It's fixed to the window, so it would drift from its button when the page scrolls or resizes.
    const onScroll = (e: Event) => !inside(e.target) && close.current();
    const onResize = () => close.current();
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
    };
  }, [anchor]);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Tab') {
      e.preventDefault();
      close.current();
      return;
    }
    const list = menu.current ? items(menu.current) : [];
    if (list.length === 0) return;
    const at = list.indexOf(document.activeElement as HTMLElement);
    const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: list.length - 1 }[e.key];
    if (next === undefined) return;
    e.preventDefault();
    list[(next + list.length) % list.length]?.focus();
  };

  return createPortal(
    <div
      ref={menu}
      role="menu"
      onKeyDown={onKeyDown}
      style={pos ?? { left: 0, top: 0, visibility: 'hidden' }}
      className={cx('scroll-thin fixed z-50 w-72 overflow-y-auto rounded-2xl', FLOATING, className)}
    >
      <div ref={content} className="p-1.5">
        {children}
      </div>
    </div>,
    document.body,
  );
}
