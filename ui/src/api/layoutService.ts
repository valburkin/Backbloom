import {
  compileLayout,
  controllerFromWledConfig,
  specFromLegacy,
  type Controller,
  type LayoutSpec,
  type LedRect,
} from '../layout';
import type { RpcApi } from './rpcApi';
import type { ServerInfo } from './types';

export type LayoutSource = 'saved' | 'legacy' | 'empty';

export interface LoadedLayout {
  spec: LayoutSpec;
  source: LayoutSource;
  /** Instance whose `leds` / `device` the spec was derived from. */
  instance: number;
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

/** A controller with one output covering all LEDs, for when the hardware can't be queried. */
export function fallbackController(host: string, ledCount: number, name?: string): Controller {
  return {
    id: 'controller-1',
    name: name || host || 'LED controller',
    kind: 'wled',
    host,
    outputs: [{ index: 0, start: 0, length: Math.max(0, ledCount) }],
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

/** Ask the WLED controller for its outputs; fall back to a single output on any failure. */
async function controllerForDevice(api: RpcApi, device: unknown, ledCount: number): Promise<{ controller: Controller; note?: string }> {
  const d = (device ?? {}) as { type?: string; host?: string };
  const host = typeof d.host === 'string' ? d.host.trim() : '';
  if (d.type !== 'wled' || !host) {
    return { controller: fallbackController(host, ledCount), note: d.type && d.type !== 'wled' ? `The current LED device type ("${d.type}") isn't a WLED controller; it is shown as a single output.` : undefined };
  }
  try {
    const cfg = await withTimeout((signal) => api.backend.fetchWledConfig(host, signal), 4000);
    return { controller: controllerFromWledConfig(host, cfg) };
  } catch (e) {
    return {
      controller: fallbackController(host, ledCount),
      note: `Couldn't read the outputs of ${host} (${errorText(e)}), so all ${ledCount} LEDs are shown as one output.`,
    };
  }
}

function fromLegacy(rects: LedRect[], controller: Controller, instance: number, note?: string): LoadedLayout {
  try {
    return { spec: specFromLegacy(rects, controller), source: 'legacy', instance, note };
  } catch (e) {
    return {
      spec: emptySpecFor(controller),
      source: 'empty',
      instance,
      note: `Your existing LED positions couldn't be converted (${errorText(e)}). Start from a template instead.`,
    };
  }
}

/**
 * Load the layout to edit. Admin sessions read the global `layout` setting (falling back to the instance's
 * legacy `leds` + `device`); without admin access only serverinfo's `leds` is available.
 */
export async function loadLayout(api: RpcApi, opts: { admin: boolean; serverInfo?: ServerInfo | null }): Promise<LoadedLayout> {
  const instance = opts.serverInfo?.currentInstance ?? 0;
  if (opts.admin) {
    const config = await api.getConfig();
    // The backend creates an empty layout by default; that means "never saved", so use the LED positions instead.
    const saved = isLayoutSpec(config.layout) && (config.layout.controllers.length > 0 || config.layout.strips.length > 0);
    if (saved) return { spec: config.layout as LayoutSpec, source: 'saved', instance };
    const rects = isRectArray(config.leds) ? config.leds : [];
    const { controller, note } = await controllerForDevice(api, config.device, rects.length);
    return fromLegacy(rects, controller, instance, note);
  }
  const rects = isRectArray(opts.serverInfo?.leds) ? (opts.serverInfo!.leds as LedRect[]) : [];
  return fromLegacy(rects, fallbackController('', rects.length, 'LED controller'), instance,
    'Sign in to see your controller details. LED positions were read from the running configuration.');
}

// ---- saving ----

export interface SaveBlocker {
  reason: 'multiController' | 'noController' | 'errors' | 'compile';
  message: string;
}

/** Why the layout can't be saved right now (empty = saveable). Auth is checked separately. */
export function saveBlockers(spec: LayoutSpec, errorCount: number): SaveBlocker[] {
  const out: SaveBlocker[] = [];
  if (spec.controllers.length === 0)
    out.push({ reason: 'noController', message: 'Add a controller on the Devices page before saving.' });
  if (spec.controllers.length > 1)
    out.push({
      reason: 'multiController',
      message:
        'Saving layouts that span more than one controller isn’t supported yet. Remove the extra controllers on the Devices page to save.',
    });
  if (errorCount > 0)
    out.push({ reason: 'errors', message: `Fix ${errorCount === 1 ? 'the problem' : `the ${errorCount} problems`} listed below first.` });
  return out;
}

function sameHost(a: string, b: string): boolean {
  const norm = (h: string) => h.trim().toLowerCase().replace(/^https?:\/\//, '').replace(/\/.*$/, '');
  return norm(a) === norm(b);
}

/** Find the running instance whose LED device is this controller. Single-instance setups skip the search. */
async function findInstanceFor(api: RpcApi, controller: Controller, serverInfo: ServerInfo | null): Promise<number> {
  const current = serverInfo?.currentInstance ?? 0;
  const running = (serverInfo?.instance ?? []).filter((i) => i.running);
  if (running.length <= 1) return running[0]?.instance ?? current;
  let found: number | null = null;
  try {
    for (const inst of running) {
      await api.switchInstance(inst.instance);
      const cfg = await api.getConfig();
      const host = (cfg.device as { host?: string } | undefined)?.host;
      if (typeof host === 'string' && sameHost(host, controller.host)) {
        found = inst.instance;
        break;
      }
    }
  } finally {
    await api.switchInstance(current).catch(() => undefined);
  }
  return found ?? current;
}

export interface SaveResult {
  instance: number;
  ledCount: number;
  /** False when the backend accepted the save but dropped the `layout` key (older backend without that setting). */
  layoutPersisted: boolean;
}

/**
 * Compile and save: global `layout` (the spec) + the target instance's `leds`. Only these two sections are
 * sent; the backend keeps the others.
 * Only called from an explicit user action.
 */
export async function saveLayout(api: RpcApi, spec: LayoutSpec, serverInfo: ServerInfo | null): Promise<SaveResult> {
  if (spec.controllers.length !== 1) throw new Error('Only single-controller layouts can be saved for now.');
  const controller = spec.controllers[0];
  const compiled = compileLayout(spec).find((c) => c.controllerId === controller.id);
  if (!compiled) throw new Error('The layout compiler returned nothing for this controller.');

  const current = serverInfo?.currentInstance ?? 0;
  const instance = await findInstanceFor(api, controller, serverInfo);
  const switched = instance !== current;
  try {
    if (switched) await api.switchInstance(instance);
    await api.setConfig({ leds: compiled.leds, layout: spec });
    let layoutPersisted = true;
    try {
      layoutPersisted = isLayoutSpec((await api.getConfig()).layout);
    } catch {
      /* verification is best-effort */
    }
    return { instance, ledCount: compiled.leds.length, layoutPersisted };
  } finally {
    if (switched) await api.switchInstance(current).catch(() => undefined);
  }
}
