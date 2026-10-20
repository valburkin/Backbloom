import {
  compileLayout,
  controllerFromWledConfig,
  specFromLegacy,
  type Controller,
  type DriverSettings,
  type LayoutSpec,
  type LedRect,
} from '../layout';
import type { RpcApi } from './rpcApi';
import type { ServerInfo } from './types';

export type LayoutSource = 'saved' | 'legacy' | 'empty';

export interface LoadedLayout {
  spec: LayoutSpec;
  source: LayoutSource;
  /** Something the user should know about how the layout was obtained. */
  note?: string;
}

export function isLayoutSpec(v: unknown): v is LayoutSpec {
  if (!v || typeof v !== 'object') return false;
  const o = v as Partial<LayoutSpec>;
  return o.version === 1 && Array.isArray(o.controllers) && Array.isArray(o.strips);
}

function isRectArray(v: unknown): v is LedRect[] {
  return Array.isArray(v) && v.every((r) => r && typeof r === 'object' && typeof (r as LedRect).hmin === 'number');
}

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function isDriverSettings(v: unknown): v is DriverSettings {
  return !!v && typeof v === 'object' && !Array.isArray(v) && typeof (v as DriverSettings).type === 'string';
}

/** A controller with one output covering all LEDs, for when the hardware can't be queried. */
export function fallbackController(host: string, ledCount: number, name?: string, device?: DriverSettings): Controller {
  return {
    id: 'controller-1',
    name: name || host || 'LED controller',
    kind: device?.type ?? 'wled',
    host,
    outputs: [{ index: 0, start: 0, length: Math.max(0, ledCount) }],
    ...(device ? { device } : {}),
  };
}

/** A layout with the controller and one empty strip per output (used when legacy import isn't possible). */
export function emptySpecFor(controller: Controller): LayoutSpec {
  return {
    version: 1,
    controllers: [controller],
    strips: controller.outputs.map((o, i) => ({
      id: `strip-${i + 1}`,
      name: controller.outputs.length > 1 ? `Strip ${i + 1}` : 'Main strip',
      controllerId: controller.id,
      output: o.index,
      segments: [],
    })),
  };
}

async function withTimeout<T>(p: (signal: AbortSignal) => Promise<T>, ms: number): Promise<T> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), ms);
  try {
    return await p(ctrl.signal);
  } finally {
    clearTimeout(timer);
  }
}

/** The controller the legacy `device` describes; a WLED controller is asked for its outputs (one output on any failure). */
async function controllerForDevice(api: RpcApi, device: unknown, ledCount: number): Promise<{ controller: Controller; note?: string }> {
  const settings = isDriverSettings(device) ? device : undefined;
  const host = typeof settings?.host === 'string' ? settings.host.trim() : '';
  if (settings?.type !== 'wled' || !host) {
    return { controller: fallbackController(host, ledCount, undefined, settings) };
  }
  try {
    const cfg = await withTimeout((signal) => api.backend.fetchWledConfig(host, signal), 4000);
    return { controller: { ...controllerFromWledConfig(host, cfg), device: settings } };
  } catch (e) {
    return {
      controller: fallbackController(host, ledCount, undefined, settings),
      note: `Couldn't read the outputs of ${host} (${errorText(e)}), so all ${ledCount} LEDs are shown as one output.`,
    };
  }
}

function fromLegacy(rects: LedRect[], controller: Controller, note?: string): LoadedLayout {
  try {
    return { spec: specFromLegacy(rects, controller), source: 'legacy', note };
  } catch (e) {
    return {
      spec: emptySpecFor(controller),
      source: 'empty',
      note: `Your existing LED positions couldn't be converted (${errorText(e)}). Start from a template instead.`,
    };
  }
}

/**
 * Load the layout to edit. Admin sessions read the `layout` setting (every controller with its driver settings),
 * falling back to the legacy single controller (`leds` + `device`); without admin access only serverinfo's `leds`
 * is available.
 */
export async function loadLayout(api: RpcApi, opts: { admin: boolean; serverInfo?: ServerInfo | null }): Promise<LoadedLayout> {
  if (opts.admin) {
    const config = await api.getConfig();
    // Until a layout is saved the backend has none (`layout` is absent); the LED positions describe the LEDs then.
    if (isLayoutSpec(config.layout)) return { spec: config.layout, source: 'saved' };
    const rects = isRectArray(config.leds) ? config.leds : [];
    const { controller, note } = await controllerForDevice(api, config.device, rects.length);
    return fromLegacy(rects, controller, note);
  }
  const rects = isRectArray(opts.serverInfo?.leds) ? (opts.serverInfo!.leds as LedRect[]) : [];
  return fromLegacy(rects, fallbackController('', rects.length, 'LED controller'),
    'Sign in to see your controller details. LED positions were read from the running configuration.');
}

// ---- saving ----

export interface SaveBlocker {
  reason: 'noController' | 'errors';
  message: string;
}

/** Why the layout can't be saved right now (empty = saveable). Auth is checked separately. */
export function saveBlockers(spec: LayoutSpec, errorCount: number): SaveBlocker[] {
  const out: SaveBlocker[] = [];
  if (spec.controllers.length === 0)
    out.push({ reason: 'noController', message: 'Add a controller on the Devices page before saving.' });
  if (errorCount > 0)
    out.push({ reason: 'errors', message: `Fix ${errorCount === 1 ? 'the problem' : `the ${errorCount} problems`} listed below first.` });
  return out;
}

export interface SaveResult {
  /** LEDs of all controllers together. */
  ledCount: number;
  controllers: number;
  /** False when the backend accepted the save but dropped the `layout` key (older backend without that setting). */
  layoutPersisted: boolean;
}

/**
 * Save the layout: one `layout` setting with every controller (driver settings included) and strip; the backend
 * compiles it and feeds each controller its LEDs. `leds` (every controller's LEDs, one after the other) goes along
 * for backends that don't compile layouts; the Backbloom service derives it from the layout itself.
 * Only called from an explicit user action.
 */
export async function saveLayout(api: RpcApi, spec: LayoutSpec): Promise<SaveResult> {
  if (spec.controllers.length === 0) throw new Error('The layout has no controller.');
  const leds = compileLayout(spec).flatMap((c) => c.leds);
  await api.setConfig({ layout: spec, leds });
  let layoutPersisted = true;
  try {
    layoutPersisted = isLayoutSpec((await api.getConfig()).layout);
  } catch {
    /* verification is best-effort */
  }
  return { ledCount: leds.length, controllers: spec.controllers.length, layoutPersisted };
}
