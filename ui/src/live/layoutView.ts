import { useEffect, useState } from 'react';
import { isLayoutSpec, fallbackController } from '../api/layoutService';
import type { RpcApi } from '../api/rpcApi';
import type { ServerInfo } from '../api/types';
import {
  aroundScreen,
  darkMask,
  defaultRoom,
  ledInUnfolded,
  placeLeds,
  specFromLegacy,
  SURFACES,
  surfaceOrigin,
  surfaceSize,
  toUnfolded,
  type LayoutSpec,
  type LedRect,
} from '../layout';

/** One LED to draw: where (in the view's units) and its index in the engine's LED list (and the preview frames). */
export interface ViewLed {
  x: number;
  y: number;
  led: number;
}

/** The user's LEDs drawn in the unfolded room, around the screen: the part of the room the strips are in. */
export interface LayoutView {
  width: number;
  height: number;
  screen: { x: number; y: number; w: number; h: number };
  /** Walls, ceiling and floor in view, drawn faintly for orientation. */
  surfaces: Array<{ x: number; y: number; w: number; h: number }>;
  leds: ViewLed[];
  /** Typical distance between neighboring LEDs (sizes the dots and their glow). */
  spacing: number;
}

/** Width of the view in its units; the height follows the part of the room shown. */
const VIEW_W = 480;

/**
 * Lays out the strips of `spec` in the unfolded room, cropped to the screen and every LED (with a margin). Each LED's
 * index is where the backend puts it in `leds`: the controllers one after the other (each as long as its outputs
 * reach), a strip at its output's start, cut to the output's length. Dark LEDs aren't drawn.
 */
export function buildLayoutView(spec: LayoutSpec): LayoutView {
  const { room } = spec;
  const offsets = new Map<string, number>();
  let next = 0;
  for (const c of spec.controllers) {
    offsets.set(c.id, next);
    next += c.outputs.reduce((m, o) => Math.max(m, Math.max(0, Math.floor(o.start)) + Math.max(0, Math.floor(o.length))), 0);
  }

  const scr = toUnfolded(room, { surface: 'front', x: room.screen.x, y: room.screen.y });
  let x0 = scr.x;
  let y0 = scr.y;
  let x1 = scr.x + room.screen.width;
  let y1 = scr.y + room.screen.height;
  const placed: Array<{ x: number; y: number; led: number }> = [];
  const gaps: number[] = [];
  for (const strip of spec.strips) {
    const controller = spec.controllers.find((c) => c.id === strip.controllerId);
    const output = controller?.outputs.find((o) => o.index === strip.output);
    if (!controller || !output || strip.path.length < 2) continue;
    const dark = darkMask(strip);
    const first = offsets.get(controller.id)! + Math.max(0, Math.floor(output.start));
    let prev: { x: number; y: number } | null = null;
    for (const led of placeLeds(room, strip)) {
      const p = ledInUnfolded(room, strip.path, led);
      if (prev) gaps.push(Math.hypot(p.x - prev.x, p.y - prev.y));
      prev = p;
      if (dark[led.index] || led.index >= output.length) continue;
      placed.push({ ...p, led: first + led.index });
      x0 = Math.min(x0, p.x);
      y0 = Math.min(y0, p.y);
      x1 = Math.max(x1, p.x);
      y1 = Math.max(y1, p.y);
    }
  }

  const margin = Math.max(0.12, 0.08 * Math.max(x1 - x0, y1 - y0));
  x0 -= margin;
  y0 -= margin;
  x1 += margin;
  y1 += margin;
  const k = VIEW_W / (x1 - x0);
  const at = (x: number, y: number) => ({ x: (x - x0) * k, y: (y - y0) * k });
  const surfaces = SURFACES.map((s) => {
    const o = surfaceOrigin(room, s);
    const { w, h } = surfaceSize(room, s);
    const a = at(o.x, o.y);
    return { x: a.x, y: a.y, w: w * k, h: h * k };
  }).filter((r) => r.x < VIEW_W && r.y < (y1 - y0) * k && r.x + r.w > 0 && r.y + r.h > 0);
  gaps.sort((a, b) => a - b);
  const typical = gaps.filter((g) => g > 1e-6);
  return {
    width: VIEW_W,
    height: Math.max(1, Math.round((y1 - y0) * k)),
    screen: { ...at(scr.x, scr.y), w: room.screen.width * k, h: room.screen.height * k },
    surfaces,
    leds: placed.map((p) => ({ ...at(p.x, p.y), led: p.led })),
    spacing: typical.length ? typical[Math.floor(typical.length / 2)] * k : 12,
  };
}

/** The backend's standard preview frame (EffectPreview.StandardFrame): 16 LEDs along the top and bottom, 9 per side, clockwise from the top left. */
export function standardFrameSpec(top = 16, side = 9): LayoutSpec {
  const room = { ...defaultRoom('none'), screen: { x: 1.7, y: 1.2, width: 0.64, height: 0.36 } };
  const controller = fallbackController('', 2 * (top + side), 'Preview');
  const path = aroundScreen(room, { start: 'top-left', clockwise: true }).map((p, i) => ({ ...p, at: [0, top, top + side, 2 * top + side, 2 * (top + side)][i] }));
  return {
    version: 1,
    room,
    controllers: [controller],
    strips: [{ id: 'frame', name: 'Frame', controllerId: controller.id, output: controller.outputs[0]?.index ?? 0, ledCount: 2 * (top + side), path }],
  };
}

function isRectArray(v: unknown): v is LedRect[] {
  return Array.isArray(v) && v.every((r) => r && typeof r === 'object' && typeof (r as LedRect).hmin === 'number');
}

/**
 * The user's layout for drawing previews: the saved `layout` setting (admin sessions), else one inferred from the LED
 * positions in serverinfo, without asking any controller. Null until loaded or when it can't be worked out.
 */
export function useLayoutView(api: RpcApi, admin: boolean, leds: ServerInfo['leds']): LayoutView | null {
  const [view, setView] = useState<LayoutView | null>(null);
  useEffect(() => {
    let live = true;
    const fromLeds = (): LayoutView | null => {
      const rects = isRectArray(leds) ? leds : [];
      if (!rects.length) return null;
      try {
        return buildLayoutView(specFromLegacy(rects, fallbackController('', rects.length, 'LED controller')));
      } catch {
        return null;
      }
    };
    void (async () => {
      let next: LayoutView | null = null;
      if (admin) {
        try {
          const layout = (await api.getConfig()).layout;
          if (isLayoutSpec(layout)) next = buildLayoutView(layout);
        } catch {
          /* fall back to the LED positions */
        }
      }

      next ??= fromLeds();
      if (live) setView(next);
    })();
    return () => {
      live = false;
    };
  }, [api, admin, leds]);
  return view;
}
