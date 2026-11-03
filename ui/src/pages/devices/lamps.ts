import type { Controller, ControllerOutput, Strip } from '../../layout';
import { lampList } from './drivers';

/**
 * A lamp device with one output (and strip) per lamp, as Add lights makes them so each lamp has its own place: every
 * output drives one LED. A lamp device with one output for all its lamps isn't one (unless it has one lamp).
 */
export function isPerLampDevice(c: Controller): boolean {
  // no outputs (every lamp removed): still one per lamp, so the next lamp gets its own
  return lampList(c.device) !== null && c.outputs.every((o) => o.length === 1);
}

/** What changes in the layout when a per-lamp device's lamp list changes. */
export interface LampListChange {
  outputs: ControllerOutput[];
  /** Strips of lamps that were removed. */
  removeStrips: string[];
  /** Strips whose lamp moved to another output (a lamp before it was removed). */
  moveStrips: Array<{ stripId: string; output: number }>;
  /** New lamps: the output each gets, and its name for a new strip. */
  addStrips: Array<{ output: number; name: string }>;
}

function same(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function lampName(lamp: unknown, i: number): string {
  const name = (lamp as { name?: unknown } | null)?.name;
  return typeof name === 'string' && name.trim() ? name.trim() : `Lamp ${i + 1}`;
}

/**
 * The outputs and strips after the lamp list went from `before` to `after`: a removed lamp takes its output and strip
 * along and the lamps after it move up one output (with their strips, so each strip stays with its lamp); lamps added
 * get an output and a strip each. Lamps are matched in order: what was removed is where the lists first differ.
 */
export function lampListChange(c: Controller, strips: readonly Strip[], before: readonly unknown[], after: readonly unknown[]): LampListChange {
  const own = strips.filter((s) => s.controllerId === c.id);
  const stripOn = (output: number) => own.filter((s) => s.output === output).map((s) => s.id);
  // for each lamp after the change, the output it had before (null: new)
  const from: Array<number | null> = [];
  let i = 0;
  let j = 0;
  const removed: number[] = [];
  while (j < after.length) {
    if (i < before.length && same(before[i], after[j])) {
      from.push(i++);
      j++;
    } else if (before.length - i > after.length - j) removed.push(i++);
    else if (before.length - i === after.length - j && i < before.length) {
      // edited in place (a name, an address): the same lamp
      from.push(i++);
      j++;
    } else {
      from.push(null);
      j++;
    }
  }
  while (i < before.length) removed.push(i++);
  const outputs = after.map((_, k) => ({ index: k, start: k, length: 1 }));
  return {
    outputs,
    removeStrips: removed.flatMap(stripOn),
    moveStrips: from.flatMap((was, k) => (was !== null && was !== k ? stripOn(was).map((stripId) => ({ stripId, output: k })) : [])),
    addStrips: from.flatMap((was, k) => (was === null ? [{ output: k, name: lampName(after[k], k) }] : [])),
  };
}
