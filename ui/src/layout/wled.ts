import type { Controller, ControllerOutput } from './types';

type Json = Record<string, unknown>;

function isObject(x: unknown): x is Json {
  return typeof x === 'object' && x !== null && !Array.isArray(x);
}

function toInt(x: unknown): number | undefined {
  const n = typeof x === 'string' && x.trim() !== '' ? Number(x) : x;
  return typeof n === 'number' && Number.isFinite(n) ? Math.floor(n) : undefined;
}

/** Stable id from a hostname or IP: "WLED-Living.local" -> "wled-living-local". */
export function slugifyHost(host: string): string {
  return host
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/** Display name for a host: drops a trailing ".local" (mDNS). */
export function displayNameForHost(host: string): string {
  return host.trim().replace(/\.$/, '').replace(/\.local$/i, '');
}

/**
 * Build a Controller from a WLED `GET /json/cfg` response body.
 * Outputs come from `hw.led.ins` (index = array position); entries without a usable length are skipped.
 * Throws an Error with a user-facing message when the response has no usable outputs.
 */
export function controllerFromWledConfig(host: string, cfg: unknown, name?: string): Controller {
  const id = typeof host === 'string' ? slugifyHost(host) : '';
  if (!id) throw new Error('A WLED controller needs a hostname or IP address.');

  const hw = isObject(cfg) ? cfg.hw : undefined;
  const led = isObject(hw) ? hw.led : undefined;
  const ins = isObject(led) ? led.ins : undefined;
  if (!Array.isArray(ins)) {
    throw new Error(`${host} did not return a WLED LED configuration (hw.led.ins is missing). Is this a WLED device?`);
  }

  const outputs: ControllerOutput[] = [];
  let nextStart = 0;
  ins.forEach((entry, index) => {
    if (!isObject(entry)) return;
    const length = toInt(entry.len);
    if (length === undefined || length < 1) return;
    const start = Math.max(0, toInt(entry.start) ?? nextStart);
    const pin = toInt(Array.isArray(entry.pin) ? entry.pin[0] : entry.pin);
    outputs.push(pin === undefined ? { index, start, length } : { index, start, length, pin });
    nextStart = start + length;
  });
  if (outputs.length === 0) {
    throw new Error(`${host} reports no LED outputs. Add at least one LED output in WLED's LED preferences first.`);
  }

  return {
    id,
    name: name?.trim() || displayNameForHost(host),
    kind: 'wled',
    host: host.trim(),
    outputs,
  };
}
