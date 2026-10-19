import { stripLedCount } from './compile';
import type { Controller, ControllerOutput, LayoutIssue, LayoutSpec, Segment, Strip } from './types';

const EDGES = new Set(['top', 'bottom', 'left', 'right']);

/** "1 LED" / "3 LEDs". */
function leds(n: number): string {
  return `${n} LED${n === 1 ? '' : 's'}`;
}

function stripLabel(s: Strip): string {
  return `strip "${s.name || s.id}"`;
}

function controllerLabel(c: Controller): string {
  return `"${c.name || c.id}"`;
}

/** Outputs are shown 1-based, matching WLED's own UI. */
function outputLabel(o: ControllerOutput): string {
  return o.pin === undefined ? `output ${o.index + 1}` : `output ${o.index + 1} (pin ${o.pin})`;
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function isInt(x: number): boolean {
  return Number.isInteger(x);
}

function validateSegment(seg: Segment, where: string, push: (sev: LayoutIssue['severity'], msg: string) => void) {
  if (!isInt(seg.count) || seg.count < 1) {
    push('error', `${where} needs a whole number of LEDs, at least 1 (it has ${String(seg.count)}).`);
  }

  switch (seg.kind) {
    case 'edge': {
      if (!EDGES.has(seg.edge)) {
        push('error', `${where} is on an unknown screen edge "${String(seg.edge)}".`);
      }
      if (!Number.isFinite(seg.from) || !Number.isFinite(seg.to)) {
        push('error', `${where} needs numeric start and end positions.`);
      } else {
        if (seg.from === seg.to && seg.count > 1) {
          push(
            'error',
            `${where} starts and ends at the same position, so all its LEDs would show the same spot. ` +
              'Spread it along the edge, or use a fixed area instead.',
          );
        }
        if (Math.min(seg.from, seg.to) < -0.5 || Math.max(seg.from, seg.to) > 1.5) {
          push('warning', `${where} extends far past the edge of the screen; LEDs out there will mirror the corner.`);
        }
      }
      if (!Number.isFinite(seg.depth) || seg.depth <= 0 || seg.depth > 1) {
        push('error', `${where} has a sampling depth of ${String(seg.depth)}; it must be above 0 and at most 1 (the whole screen).`);
      }
      if (seg.blend !== undefined && (!Number.isFinite(seg.blend) || seg.blend <= 0)) {
        push('error', `${where} has a blend of ${String(seg.blend)}; it must be greater than 0 (1 = LEDs just touch).`);
      }
      break;
    }
    case 'fixed': {
      const a = seg.area;
      const values = a ? [a.hmin, a.hmax, a.vmin, a.vmax] : [];
      if (!a || !values.every(Number.isFinite)) {
        push('error', `${where} needs a screen area with numeric bounds.`);
        break;
      }
      if (a.hmin > a.hmax) push('error', `${where}: the area's left edge (${a.hmin}) is right of its right edge (${a.hmax}).`);
      if (a.vmin > a.vmax) push('error', `${where}: the area's top edge (${a.vmin}) is below its bottom edge (${a.vmax}).`);
      if (values.some((v) => v < 0 || v > 1)) {
        push('warning', `${where}: the area reaches outside the screen and will be cut to fit.`);
      }
      break;
    }
    case 'off':
      break;
    default:
      push('error', `${where} has an unknown kind "${String((seg as { kind?: unknown }).kind)}".`);
  }
}

/** Human-readable problems with a layout (LED count mismatches, unknown outputs, bad ranges...). */
export function validateLayout(spec: LayoutSpec): LayoutIssue[] {
  const issues: LayoutIssue[] = [];

  // Duplicate ids.
  const seenControllers = new Set<string>();
  for (const c of spec.controllers) {
    if (seenControllers.has(c.id)) {
      issues.push({ severity: 'error', controllerId: c.id, message: `Two controllers share the id "${c.id}". Each controller needs its own id.` });
    }
    seenControllers.add(c.id);
  }
  const seenStrips = new Set<string>();
  for (const s of spec.strips) {
    if (seenStrips.has(s.id)) {
      issues.push({ severity: 'error', stripId: s.id, message: `Two strips share the id "${s.id}". Each strip needs its own id.` });
    }
    seenStrips.add(s.id);
  }

  // Controllers and their outputs.
  for (const c of spec.controllers) {
    const add = (severity: LayoutIssue['severity'], message: string) =>
      issues.push({ severity, message, controllerId: c.id });
    if (c.outputs.length === 0) {
      add('warning', `Controller ${controllerLabel(c)} has no LED outputs.`);
      continue;
    }
    const seenIdx = new Set<number>();
    for (const o of c.outputs) {
      if (seenIdx.has(o.index)) add('error', `Controller ${controllerLabel(c)} lists ${outputLabel(o)} twice.`);
      seenIdx.add(o.index);
      if (!isInt(o.length) || o.length < 1) {
        add('error', `${capitalize(outputLabel(o))} of ${controllerLabel(c)} must drive at least 1 LED (it has ${String(o.length)}).`);
      }
      if (!isInt(o.start) || o.start < 0) {
        add('error', `${capitalize(outputLabel(o))} of ${controllerLabel(c)} has an invalid start index (${String(o.start)}).`);
      }
    }
    const sorted = [...c.outputs].sort((a, b) => a.start - b.start);
    for (let i = 1; i < sorted.length; i++) {
      const prev = sorted[i - 1];
      const cur = sorted[i];
      const prevEnd = prev.start + prev.length;
      if (cur.start < prevEnd) {
        const last = Math.min(prevEnd, cur.start + cur.length) - 1;
        add(
          'error',
          `${capitalize(outputLabel(prev))} and ${outputLabel(cur)} of ${controllerLabel(c)} overlap ` +
            `(LEDs ${cur.start}–${last} are claimed by both). Check each output's start and length on the controller.`,
        );
      }
    }
  }

  // Strips.
  const controllers = new Map(spec.controllers.map((c) => [c.id, c] as const));
  const outputOwner = new Map<string, Strip>();
  for (const s of spec.strips) {
    const add = (severity: LayoutIssue['severity'], message: string, segmentIndex?: number) =>
      issues.push({ severity, message, stripId: s.id, controllerId: s.controllerId, segmentIndex });
    const label = capitalize(stripLabel(s));

    s.segments.forEach((seg, i) =>
      validateSegment(seg, `Segment ${i + 1} of ${stripLabel(s)}`, (sev, msg) => add(sev, capitalize(msg), i)),
    );

    const c = controllers.get(s.controllerId);
    if (!c) {
      add('error', `${label} is wired to a controller that doesn't exist (id "${s.controllerId}").`);
      continue;
    }
    const o = c.outputs.find((x) => x.index === s.output);
    if (!o) {
      const avail = c.outputs.length
        ? `it has ${c.outputs.map(outputLabel).join(', ')}`
        : 'it has no outputs';
      add('error', `${label} is wired to output ${s.output + 1} of ${controllerLabel(c)}, but ${avail}.`);
      continue;
    }

    const key = `${c.id}\u0000${o.index}`;
    const owner = outputOwner.get(key);
    if (owner) {
      add(
        'error',
        `${capitalize(stripLabel(owner))} and ${stripLabel(s)} are both wired to ${outputLabel(o)} of ${controllerLabel(c)}. ` +
          'Each output drives one strip.',
      );
      continue;
    }
    outputOwner.set(key, s);

    const n = stripLedCount(s);
    if (s.segments.length === 0) {
      add('warning', `${label} has no segments yet, so its ${leds(o.length)} will stay dark.`);
    } else if (n > o.length) {
      add(
        'error',
        `${label} has ${leds(n)}, but ${outputLabel(o)} of ${controllerLabel(c)} only drives ${o.length} — ` +
          `${leds(n - o.length)} too many. The extra LEDs at the end of the strip will be ignored.`,
      );
    } else if (n < o.length) {
      const missing = o.length - n;
      add(
        'warning',
        `${label} has ${leds(n)}, but ${outputLabel(o)} of ${controllerLabel(c)} drives ${o.length} — ` +
          `${leds(missing)} missing. The last ${missing === 1 ? 'LED' : `${missing} LEDs`} on that output will stay dark.`,
      );
    }
  }

  return issues;
}
