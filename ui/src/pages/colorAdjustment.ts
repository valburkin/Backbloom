// The first channel adjustment (`color.channelAdjustment[0]`), shared by the Colors and Calibration pages: what the
// `adjustment` command takes, values that differ from their defaults, and the live preview ahead of the save.

import { useEffect, useRef } from 'react';
import type { PageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useThrottled } from '../app/useThrottled';
import { differsFromDefault, propertyAt, withDefaults } from '../schema/schema';
import { deepEqual } from '../settings/drafts';
import type { JsonSchema } from '../api/types';

export type Obj = Record<string, unknown>;

/** Fields the `adjustment` command accepts (it applies them immediately). */
const LIVE_KEYS = [
  'classic_config', 'red', 'green', 'blue', 'cyan', 'magenta', 'yellow', 'white', 'black', 'scaleOutput', 'gamma', 'backlightThreshold',
  'backlightColored', 'luminanceGain', 'saturationGain', 'temperatureSetting', 'temperatureRed', 'temperatureGreen', 'temperatureBlue', 'powerLimit',
];

function liveAdjustment(adj: Obj): Obj {
  const out: Obj = {};
  for (const k of LIVE_KEYS) if (adj[k] !== undefined) out[k] = adj[k];
  if (typeof out.gamma === 'number') out.gamma = Math.min(5, out.gamma);
  return out;
}

/** The first channel adjustment as the page shows it ({} until loaded). */
export function adjustmentOf(color: Obj): Obj {
  return ((color.channelAdjustment as Obj[] | undefined)?.[0] ?? {}) as Obj;
}

/** Keys of `value` that differ from their defaults in `schema` (an object schema). */
export function changedKeys(schema: JsonSchema | undefined, value: Obj, keys: readonly string[]): string[] {
  return keys.filter((k) => {
    const s = propertyAt(schema, k);
    return !!s && differsFromDefault(s, value[k]);
  });
}

/** `value` with `keys` back at their defaults. */
export function withDefaultsFor(schema: JsonSchema | undefined, value: Obj, keys: readonly string[]): Obj {
  return { ...value, ...Object.fromEntries(keys.map((k) => [k, withDefaults(propertyAt(schema, k), undefined)])) };
}

/**
 * Live preview: color changes go to the lights right away, ahead of the automatic save. Returns what Discard runs
 * (for values that need attention): the lights go back to the saved adjustment.
 */
export function useLiveAdjustment(ps: PageSettings): () => void {
  const { api } = useConnection();
  const adj = adjustmentOf(ps.value<Obj>('color'));
  const colorDirty = ps.dirtySections.includes('color');
  const sendLive = useThrottled((a: Obj) => void api.adjust(a).catch(() => undefined), 150);
  const lastSent = useRef<Obj | null>(null);
  useEffect(() => {
    if (!colorDirty) {
      lastSent.current = null;
      return;
    }
    const live = liveAdjustment(adj);
    if (lastSent.current && deepEqual(live, lastSent.current)) return;
    lastSent.current = live;
    sendLive(live);
  }, [adj, colorDirty, sendLive]);
  return () => {
    const base = (ps.baseline.color as { channelAdjustment?: Obj[] } | undefined)?.channelAdjustment?.[0];
    if (base) void api.adjust(liveAdjustment(base)).catch(() => undefined);
  };
}
