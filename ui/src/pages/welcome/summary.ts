import type { BackendConfig, ServerInfo } from '../../api/types';
import type { Controller, LayoutSpec } from '../../layout';
import { addressOf, driverName, isTestDevice } from '../devices/drivers';

/** One line of the welcome's "what Backbloom will drive" summary. */
export interface SummaryRow {
  label: string;
  value: string;
  /** Further lines (one per controller). */
  details?: string[];
}

type Obj = Record<string, unknown>;

const str = (v: unknown): string | undefined => (typeof v === 'string' && v.trim() ? v.trim() : undefined);

/** Where a controller's driver sends to: its host, port or file, in words. */
export function driverTarget(device: Obj | undefined): string | undefined {
  if (!device) return undefined;
  const type = str(device.type);
  if (type === 'file') {
    const output = str(device.output);
    return output && /^(\\\\\.\\NUL|\/dev\/null)$/i.test(output) ? 'colors discarded' : output;
  }
  return str(device.host) ?? str(device.output) ?? str(device.homeAssistantHost);
}

/** `systemGrabber.device` in words: "auto", HyperHDR's "\\.\DISPLAY1|GPU" ids, a multi-monitor capture. */
export function monitorName(device: unknown): string {
  const d = str(device);
  if (!d || d === 'auto') return 'Main monitor';
  const [first, adapter] = d.split('|');
  if (first === 'MULTI-MONITOR') return `All monitors${adapter ? ` of ${adapter}` : ''}`;
  const display = /DISPLAY(\d+)/i.exec(first)?.[1];
  const name = display ? `Display ${display}` : first;
  return adapter ? `${name} (${adapter})` : name;
}

function effectText(section: unknown, withDuration: boolean): string {
  const s = (section ?? {}) as Obj;
  if (s.enable !== true) return 'off';
  if (s.type === 'color') {
    const c = Array.isArray(s.color) ? (s.color as number[]) : [];
    return `color rgb(${c.slice(0, 3).join(', ')})`;
  }
  const name = str(s.effect) ?? 'effect';
  const ms = Number(s.duration_ms);
  return withDuration && ms > 0 ? `${name} (${Math.round(ms / 100) / 10} s)` : name;
}

/** A device in one line for people: "Desk (WLED at wled-desk.local)", or "WLED at wled-desk.local" when unnamed. */
export function deviceLine(c: Controller): string {
  const type = String(c.device?.type ?? c.kind);
  const kind = driverName(type);
  const address = addressOf(c);
  const what = address ? `${kind} at ${address}` : kind;
  return c.name && c.name !== kind && c.name !== 'LED controller' ? `${c.name} (${what})` : what;
}

/** What came over from HyperHDR, in a few plain lines for the setup's first step. */
export function importedItems(controllers: readonly Controller[], ledCount: number): string[] {
  const real = controllers.filter((c) => !isTestDevice(c));
  const items: string[] = [];
  if (real.length === 1) items.push(`Your device: ${deviceLine(real[0])}`);
  else if (real.length > 1) items.push(`Your ${real.length} devices: ${real.map((c) => c.name).join(', ')}`);
  if (ledCount > 0) items.push(`${ledCount} LED${ledCount === 1 ? '' : 's'} and where they are`);
  items.push('Your screen sync, color and effect settings');
  return items;
}

/**
 * Technical details for expert tools: what the lighting will do once started, from the imported (or default) settings: LEDs, controllers with their
 * driver and address, the captured monitor and the startup / background effects.
 */
export function welcomeSummary(config: BackendConfig | null, serverInfo: ServerInfo | null): SummaryRow[] {
  const rows: SummaryRow[] = [];
  const leds = (config?.leds as unknown[] | undefined)?.length ?? serverInfo?.leds?.length ?? 0;
  const controllers = serverInfo?.controllers ?? [];
  const layout = config?.layout as LayoutSpec | undefined;
  const strips = controllers.reduce((n, c) => n + c.strips.length, 0);
  rows.push({
    label: 'LEDs',
    value: `${leds} LED${leds === 1 ? '' : 's'}${strips > 0 ? ` on ${strips} strip${strips === 1 ? '' : 's'}` : ''}`,
  });

  const details = controllers.map((c, i) => {
    const device = (layout?.controllers?.find((x) => x.id === c.id)?.device ?? (i === 0 ? config?.device : undefined)) as Obj | undefined;
    const type = c.type || str(device?.type) || '';
    const target = driverTarget(device);
    const driver = type ? driverName(type) : 'no driver';
    const where = target ? (type === 'file' ? `, ${target}` : ` at ${target}`) : '';
    return `${c.name}: ${driver}${where}, ${c.ledCount} LEDs${c.enabled ? '' : ' (turned off)'}`;
  });
  rows.push({
    label: controllers.length === 1 ? 'Controller' : 'Controllers',
    value: controllers.length === 0 ? 'none configured' : controllers.length === 1 ? details[0] : `${controllers.length} controllers`,
    details: controllers.length > 1 ? details : undefined,
  });

  if (config) {
    const capture = (config.systemGrabber ?? {}) as Obj;
    const fps = Number(capture.fps);
    rows.push({ label: 'Screen capture', value: `${monitorName(capture.device)}${fps > 0 ? `, up to ${fps} frames per second` : ''}` });
    rows.push({ label: 'Startup effect', value: effectText(config.foregroundEffect, true) });
    rows.push({ label: 'Background effect', value: effectText(config.backgroundEffect, false) });
  }
  return rows;
}
