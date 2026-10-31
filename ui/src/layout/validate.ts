import { stripLedCount } from './compile';
import { litLength, pathStations, SURFACES, surfaceSize } from './room';
import type { Area, Controller, ControllerOutput, LayoutIssue, LayoutSpec, Room, Section, Strip } from './types';

/** Room sizes accepted, in meters. */
export const ROOM_MIN = 0.5;
export const ROOM_MAX = 50;

/** How far a point may sit outside its surface (rounding while dragging). */
const SURFACE_TOL = 1e-3;

const SURFACE_SET = new Set<string>(SURFACES);

/** "1 LED" / "3 LEDs". */
function leds(n: number): string {
  return `${n} LED${n === 1 ? '' : 's'}`;
}

function stripLabel(s: Strip): string {
  return `strip “${s.name || s.id}”`;
}

function controllerLabel(c: Controller): string {
  return `“${c.name || c.id}”`;
}

/** Outputs are shown 1-based, matching WLED's own UI. */
function outputLabel(o: ControllerOutput): string {
  return o.pin === undefined ? `output ${o.index + 1}` : `output ${o.index + 1} (pin ${o.pin})`;
}

/** The LED a matched position (PathPoint.at, in LEDs from the start) falls on, numbered from 1. */
function matchedLed(at: number): string {
  return Number.isFinite(at) ? `LED ${Math.max(1, Math.ceil(at))}` : `LED ${String(at)}`;
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function isInt(x: number): boolean {
  return Number.isInteger(x);
}

function finite(...xs: unknown[]): boolean {
  return xs.every((x) => typeof x === 'number' && Number.isFinite(x));
}

function validateRoom(room: Room | undefined, push: (msg: string) => void) {
  if (!room || typeof room !== 'object') {
    push('The layout has no room.');
    return;
  }
  const dims: Array<[keyof Pick<Room, 'width' | 'depth' | 'height'>, string]> = [
    ['width', 'width'],
    ['depth', 'depth'],
    ['height', 'height'],
  ];
  let sized = true;
  for (const [k, label] of dims) {
    const v = room[k];
    if (!finite(v) || v < ROOM_MIN || v > ROOM_MAX) {
      push(`The room’s ${label} must be from ${ROOM_MIN} to ${ROOM_MAX} m (it is ${String(v)}).`);
      sized = false;
    }
  }
  const s = room.screen;
  if (!s || !finite(s.x, s.y, s.width, s.height) || s.width <= 0 || s.height <= 0) {
    push('The screen needs a position and a size above 0.');
  } else if (sized && (s.x < -SURFACE_TOL || s.y < -SURFACE_TOL || s.x + s.width > room.width + SURFACE_TOL || s.y + s.height > room.height + SURFACE_TOL)) {
    push('The screen reaches past the front wall. Make it smaller or the room bigger.');
  }
  if (!['desk', 'tv', 'none'].includes(room.furniture)) push(`Unknown furniture “${String(room.furniture)}”.`);
}

function validArea(a: Area | undefined): a is Area {
  return !!a && finite(a.hmin, a.hmax, a.vmin, a.vmax);
}

const SECTION_KINDS = new Set(['edge', 'whole', 'area', 'none', 'effect', 'color', 'gap']);

function validateSection(section: Section, where: string, add: (sev: LayoutIssue['severity'], msg: string) => void) {
  if (!section || typeof section !== 'object' || !SECTION_KINDS.has(section.kind)) {
    add('error', `${where} does “${String((section as { kind?: unknown } | undefined)?.kind)}”, which isn’t known.`);
    return;
  }
  if (section.kind === 'edge' && !finite(section.from, section.to)) {
    add('error', `${where} needs a start and an end on the screen’s outline.`);
  }
  if (section.kind === 'effect' && (typeof section.effect !== 'string' || section.effect.trim() === '')) {
    add('error', `${where} needs the name of the effect it plays.`);
  }
  if (
    section.kind === 'color' &&
    !(Array.isArray(section.color) && section.color.length === 3 && section.color.every((c) => Number.isInteger(c) && c >= 0 && c <= 255))
  ) {
    add('error', `${where} needs a color: three whole numbers from 0 to 255.`);
  }
  if (section.kind === 'area') {
    const a = section.area;
    if (!validArea(a)) add('error', `${where} needs an area of the picture to show.`);
    else {
      if (a.hmin > a.hmax || a.vmin > a.vmax) add('error', `${where}: the area of the picture is turned inside out.`);
      if ([a.hmin, a.hmax, a.vmin, a.vmax].some((v) => v < 0 || v > 1)) add('warning', `${where}: the area reaches outside the picture and will be cut to fit.`);
    }
  }
}

function validateStripShape(s: Strip, room: Room | undefined, add: (sev: LayoutIssue['severity'], msg: string, pointIndex?: number) => void) {
  const label = capitalize(stripLabel(s));
  const count = s.ledCount;
  if (!isInt(count) || count < 1) {
    add('error', `${label} needs a whole number of LEDs, at least 1 (it has ${String(count)}).`);
  }

  if (!Array.isArray(s.path) || s.path.length < 2) {
    add('error', `${label} needs a path with a start and an end. Draw it in the room.`);
  } else {
    let pointsOk = true;
    s.path.forEach((p, i) => {
      if (!SURFACE_SET.has(p.surface) || !finite(p.x, p.y)) {
        add('error', `Bend ${i + 1} of ${stripLabel(s)} isn’t on a wall, the ceiling or the floor.`, i);
        pointsOk = false;
        return;
      }
      if (room) {
        const { w, h } = surfaceSize(room, p.surface);
        if (p.x < -SURFACE_TOL || p.y < -SURFACE_TOL || p.x > w + SURFACE_TOL || p.y > h + SURFACE_TOL) {
          add('error', `Bend ${i + 1} of ${stripLabel(s)} is outside the room.`, i);
          pointsOk = false;
        }
      }
      if (p.at !== undefined && (!finite(p.at) || p.at < 0 || (isInt(count) && p.at > count))) {
        const where = finite(p.at) && p.at < 0 ? 'before the start of the strip' : 'past the end of the strip';
        add('error', `Bend ${i + 1} of ${stripLabel(s)} is matched to ${matchedLed(p.at)}, ${where}.`, i);
        pointsOk = false;
      }
      if (p.next !== undefined && i < s.path.length - 1) {
        validateSection(p.next, `Part ${i + 1} of ${stripLabel(s)}`, (sev, msg) => add(sev, msg, i));
      }
    });
    if (pointsOk && room && isInt(count) && count >= 1) {
      if (!(litLength(room, s.path) > 0)) {
        add('error', `${label} has no length with LEDs on it: it starts and ends at the same spot, or is all gaps.`);
      } else {
        const stations = pathStations(room, s.path, count);
        for (let i = 1; i < stations.length; i++) {
          if (stations[i] < stations[i - 1]) {
            add('error', `The LEDs matched to the bends of ${stripLabel(s)} go backwards at bend ${i + 1}. Match them again in order.`, i);
            break;
          }
        }
        s.path.forEach((p, i) => {
          if (p.next?.kind === 'gap' && i + 1 < stations.length && stations[i + 1] - stations[i] >= 1) {
            add('warning', `Part ${i + 1} of ${stripLabel(s)} is a gap, but the LEDs matched at its ends put LEDs in it; they stay dark.`, i);
          }
        });
      }
    }
  }

  for (const r of s.dark ?? []) {
    if (!isInt(r.start) || !isInt(r.count) || r.start < 0 || r.count < 1 || (isInt(count) && r.start + r.count > count)) {
      add('error', `${label} turns off LEDs ${String(r.start + 1)}–${String(r.start + r.count)}, but it doesn’t have them.`);
    }
  }
  if (s.depth !== undefined && (!finite(s.depth) || s.depth <= 0 || s.depth > 1)) {
    add('error', `${label} looks ${String(s.depth)} into the picture; it must be above 0 and at most 1 (the whole picture).`);
  }
  if (s.blend !== undefined && (!finite(s.blend) || s.blend <= 0)) {
    add('error', `${label} has a softness of ${String(s.blend)}; it must be greater than 0 (1 = LEDs just touch).`);
  }
  if (s.brightness !== undefined && !(finite(s.brightness) && s.brightness >= 0 && s.brightness <= 1)) {
    add('error', `${label} has a brightness of ${String(s.brightness)}; it must be between 0 and 100%.`);
  }
}

function validateProfiles(spec: LayoutSpec, push: (severity: LayoutIssue['severity'], message: string) => void) {
  if (spec.profiles === undefined) return;
  if (!Array.isArray(spec.profiles)) {
    push('error', 'The profiles must be a list.');
    return;
  }
  const ids = new Set<string>();
  for (const p of spec.profiles) {
    const label = `Profile “${p?.name || p?.id}”`;
    if (!p || typeof p.id !== 'string' || !p.id.trim()) {
      push('error', 'A profile needs an id.');
      continue;
    }
    if (ids.has(p.id)) push('error', `Two profiles share the id “${p.id}”.`);
    ids.add(p.id);
    if (typeof p.name !== 'string' || !p.name.trim()) push('error', `Profile “${p.id}” needs a name.`);
    if (!Array.isArray(p.apps) || p.apps.some((a) => typeof a !== 'string' || !a.trim())) {
      push('error', `${label} needs its programs as a list of program names.`);
    } else if (p.apps.length === 0) {
      push('warning', `${label} has no programs, so it never turns on by itself.`);
    }
    if (p.game !== undefined && typeof p.game !== 'boolean') push('error', `${label} needs “game” to be true or false.`);
    if (!p.sections || typeof p.sections !== 'object' || Array.isArray(p.sections)) {
      push('error', `${label} needs what each strip’s parts show.`);
      continue;
    }
    for (const [stripId, sections] of Object.entries(p.sections)) {
      const strip = spec.strips.find((s) => s.id === stripId);
      if (!Array.isArray(sections)) {
        push('error', `${label}: the parts of strip “${stripId}” must be a list.`);
        continue;
      }
      sections.forEach((section, i) => {
        if (section === null) return;
        if (section?.kind === 'gap') push('error', `${label} can’t turn a part into a gap; gaps belong to the strip.`);
        else validateSection(section, `${label}, part ${i + 1} of strip “${strip?.name ?? stripId}”`, push);
      });
      if (!strip) push('warning', `${label} changes a strip that isn’t in the layout anymore (“${stripId}”).`);
      else if (sections.length !== strip.path.length - 1) {
        push('warning', `${label} was made for another shape of strip “${strip.name}”; set what its parts show again.`);
      }
    }
  }
}

/** Human-readable problems with a layout (room, LED count mismatches, unknown outputs, bad paths and parts...). */
export function validateLayout(spec: LayoutSpec): LayoutIssue[] {
  const issues: LayoutIssue[] = [];
  validateProfiles(spec, (severity, message) => issues.push({ severity, message }));

  validateRoom(spec.room, (message) => issues.push({ severity: 'error', message, room: true }));
  const room = issues.some((i) => i.room) ? undefined : spec.room;

  // Duplicate ids.
  const seenControllers = new Set<string>();
  for (const c of spec.controllers) {
    if (seenControllers.has(c.id)) {
      issues.push({ severity: 'error', controllerId: c.id, message: `Two devices share the id “${c.id}”. Each device needs its own id.` });
    }
    seenControllers.add(c.id);
  }
  const seenStrips = new Set<string>();
  for (const s of spec.strips) {
    if (seenStrips.has(s.id)) {
      issues.push({ severity: 'error', stripId: s.id, message: `Two strips share the id “${s.id}”. Each strip needs its own id.` });
    }
    seenStrips.add(s.id);
  }

  // Controllers and their outputs.
  for (const c of spec.controllers) {
    const add = (severity: LayoutIssue['severity'], message: string) =>
      issues.push({ severity, message, controllerId: c.id });
    if (c.outputs.length === 0) {
      add('warning', `Device ${controllerLabel(c)} has no LED outputs.`);
      continue;
    }
    const seenIdx = new Set<number>();
    for (const o of c.outputs) {
      if (seenIdx.has(o.index)) add('error', `Device ${controllerLabel(c)} lists ${outputLabel(o)} twice.`);
      seenIdx.add(o.index);
      if (!isInt(o.length) || o.length < 1) {
        add('error', `${capitalize(outputLabel(o))} of ${controllerLabel(c)} must drive at least 1 LED (it has ${String(o.length)}).`);
      }
      if (!isInt(o.start) || o.start < 0) {
        add('error', `${capitalize(outputLabel(o))} of ${controllerLabel(c)} has an invalid start (${String(o.start)}).`);
      }
    }
    const sorted = [...c.outputs].sort((a, b) => a.start - b.start);
    // The controller's LEDs are its outputs one after the other, so its LED count is their sum (as the backend checks).
    let end = 0;
    for (const o of sorted) {
      if (isInt(o.start) && o.start > end) {
        add(
          'error',
          `${capitalize(outputLabel(o))} of ${controllerLabel(c)} starts at LED ${o.start + 1}, leaving LEDs ${end + 1}–${o.start} ` +
            'without an output. Outputs must follow each other from LED 1.',
        );
      }
      end = Math.max(end, o.start + o.length);
    }
    for (let i = 1; i < sorted.length; i++) {
      const prev = sorted[i - 1];
      const cur = sorted[i];
      const prevEnd = prev.start + prev.length;
      if (cur.start < prevEnd) {
        const last = Math.min(prevEnd, cur.start + cur.length) - 1;
        add(
          'error',
          `${capitalize(outputLabel(prev))} and ${outputLabel(cur)} of ${controllerLabel(c)} overlap ` +
            `(LEDs ${cur.start + 1}–${last + 1} are claimed by both). Check each output’s start and length on the device.`,
        );
      }
    }
  }

  // Strips.
  const controllers = new Map(spec.controllers.map((c) => [c.id, c] as const));
  const outputOwner = new Map<string, Strip>();
  for (const s of spec.strips) {
    const add = (severity: LayoutIssue['severity'], message: string, pointIndex?: number) =>
      issues.push({ severity, message, stripId: s.id, controllerId: s.controllerId, ...(pointIndex !== undefined ? { pointIndex } : {}) });
    const label = capitalize(stripLabel(s));

    validateStripShape(s, room, add);

    const c = controllers.get(s.controllerId);
    if (!c) {
      add('error', `${label} is plugged into a device that doesn’t exist (id “${s.controllerId}”).`);
      continue;
    }
    const o = c.outputs.find((x) => x.index === s.output);
    if (!o) {
      const avail = c.outputs.length
        ? `it has ${c.outputs.map(outputLabel).join(', ')}`
        : 'it has no outputs';
      add('error', `${label} is plugged into output ${s.output + 1} of ${controllerLabel(c)}, but ${avail}.`);
      continue;
    }

    const key = `${c.id}\u0000${o.index}`;
    const owner = outputOwner.get(key);
    if (owner) {
      add(
        'error',
        `${capitalize(stripLabel(owner))} and ${stripLabel(s)} are both plugged into ${outputLabel(o)} of ${controllerLabel(c)}. ` +
          'Each output drives one strip.',
      );
      continue;
    }
    outputOwner.set(key, s);

    const n = stripLedCount(s);
    if (n > o.length) {
      add(
        'error',
        `${label} has ${leds(n)}, but ${outputLabel(o)} of ${controllerLabel(c)} only drives ${o.length} — ` +
          `${leds(n - o.length)} too many.`,
      );
    } else if (n > 0 && n < o.length) {
      const missing = o.length - n;
      add(
        'warning',
        `${label} has ${leds(n)}, but ${outputLabel(o)} of ${controllerLabel(c)} drives ${o.length}. ` +
          `The last ${missing === 1 ? 'LED' : `${missing} LEDs`} on that output stay unused.`,
      );
    }
  }

  return issues;
}
