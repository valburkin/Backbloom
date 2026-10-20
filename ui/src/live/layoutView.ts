import { useEffect, useState } from 'react';
import { isLayoutSpec, fallbackController } from '../api/layoutService';
import type { RpcApi } from '../api/rpcApi';
import type { ServerInfo } from '../api/types';
import { assignLanes, LANE_BASE, LANE_STEP, stripDots, typicalSpacing } from '../editor/geometry';
import { specFromLegacy, type LayoutSpec, type LedRect } from '../layout';

/** One LED to draw: where (in the view's units) and its index in the engine's LED list (and the preview frames). */
export interface ViewLed {
  x: number;
  y: number;
  led: number;
}

/** The user's LEDs drawn as on the Layout page: each strip just outside the screen edge it samples, in lanes. */
export interface LayoutView {
  width: number;
  height: number;
  screen: { x: number; y: number; w: number; h: number };
  leds: ViewLed[];
  /** Typical distance between neighboring LEDs (sizes the dots and their glow). */
  spacing: number;
}

// Small enough next to the lanes (16 units apart) that strips sharing an edge read as separate rows.
const SCREEN_W = 320;
const SCREEN_H = 180;

/**
 * Lays out the strips of `spec` around a 16:9 screen. Each LED's index is where the backend puts it in `leds`: the
 * controllers one after the other (each as long as its outputs reach), a strip at its output's start, cut to the
 * output's length. Dark ("off") LEDs aren't drawn.
 */
export function buildLayoutView(spec: LayoutSpec): LayoutView {
  const { lanes, count } = assignLanes(spec.strips);
  const margin = LANE_BASE + count * LANE_STEP + 6;
  const screen = { x: margin, y: margin, w: SCREEN_W, h: SCREEN_H };
  const offsets = new Map<string, number>();
  let next = 0;
  for (const c of spec.controllers) {
    offsets.set(c.id, next);
    next += c.outputs.reduce((m, o) => Math.max(m, Math.max(0, Math.floor(o.start)) + Math.max(0, Math.floor(o.length))), 0);
  }

  const leds: ViewLed[] = [];
  const spacings: number[] = [];
  for (const strip of spec.strips) {
    const controller = spec.controllers.find((c) => c.id === strip.controllerId);
    const output = controller?.outputs.find((o) => o.index === strip.output);
    if (!controller || !output) continue;
    const dots = stripDots(strip, screen, lanes.get(strip.id) ?? 0);
    spacings.push(typicalSpacing(dots.filter((d) => d.kind !== 'off')));
    const first = offsets.get(controller.id)! + Math.max(0, Math.floor(output.start));
    for (const d of dots) {
      if (d.kind !== 'off' && d.index < output.length) leds.push({ x: d.x, y: d.y, led: first + d.index });
    }
  }

  spacings.sort((a, b) => a - b);
  return {
    width: SCREEN_W + 2 * margin,
    height: SCREEN_H + 2 * margin,
    screen,
    leds,
    spacing: spacings.length ? spacings[Math.floor(spacings.length / 2)] : 12,
  };
}

/** The backend's standard preview frame (EffectPreview.StandardFrame): 16 LEDs along the top and bottom, 9 per side. */
export function standardFrameSpec(top = 16, side = 9): LayoutSpec {
  const depth = 0.1;
  const controller = fallbackController('', 2 * (top + side), 'Preview');
  return {
    version: 1,
    controllers: [controller],
    strips: [
      {
        id: 'frame',
        name: 'Frame',
        controllerId: controller.id,
        output: controller.outputs[0]?.index ?? 0,
        segments: [
          { kind: 'edge', edge: 'top', count: top, from: 0, to: 1, depth },
          { kind: 'edge', edge: 'right', count: side, from: 0, to: 1, depth },
          { kind: 'edge', edge: 'bottom', count: top, from: 1, to: 0, depth },
          { kind: 'edge', edge: 'left', count: side, from: 1, to: 0, depth },
        ],
      },
    ],
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
