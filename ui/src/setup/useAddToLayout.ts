import { useEditor } from '../app/editor';
import { useExpertTools } from '../app/expert';
import { defaultShape } from '../editor/state';
import { shapePath, type Controller, type LayoutSpec, type PathPoint, type Room } from '../layout';
import { isTestDevice } from '../pages/devices/drivers';
import { withRole, type PlaceRole } from './roles';

/**
 * A strip to add with a device: on which output, its name, and where it runs (else a ready-made shape). `role` sets
 * what all of it shows (see roles.ts); without it a given path keeps its parts' roles, and a ready-made shape shows
 * the screen's average (the way the strip runs isn't known yet).
 */
export interface StripToAdd {
  output: number;
  name: string;
  path?: PathPoint[];
  role?: PlaceRole;
}

export interface DeviceToAdd {
  controller: Controller;
  /** Default: one strip per output, named after the device. */
  strips?: StripToAdd[];
}

/** `wanted`, or `wanted-2`, `-3`… when taken. */
export function uniqueId(taken: ReadonlySet<string>, wanted: string): string {
  const base = wanted || 'controller';
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base}-${n}`)) n++;
  return `${base}-${n}`;
}

/** One strip per output, named after the device ("Desk 1", "Desk 2" when it has several). */
export function defaultStrips(c: Controller): StripToAdd[] {
  return c.outputs.map((o, i) => ({ output: o.index, name: c.outputs.length > 1 ? `${c.name} ${i + 1}` : c.name }));
}

/** The test devices a first real device replaces (a fresh install's `file` device), unless expert tools are on. */
export function replacedTestDevices(spec: LayoutSpec, expert: boolean): Controller[] {
  return !expert && spec.controllers.length > 0 && spec.controllers.every(isTestDevice) ? spec.controllers : [];
}

/** Strips in the layout that stay when devices are added (those of a replaced test device don't). */
export function placedStrips(spec: LayoutSpec, expert: boolean): LayoutSpec['strips'] {
  const replaced = new Set(replacedTestDevices(spec, expert).map((c) => c.id));
  return spec.strips.filter((s) => !replaced.has(s.controllerId));
}

/**
 * Where a strip goes and what it shows: its own path (with `role` on every part, if given), else the next ready-made
 * shape (around the screen, the ceiling, behind the furniture, a line) showing the screen's average unless `role`
 * says otherwise. `before`: the strips in the layout before it.
 */
export function stripPath(s: StripToAdd, room: Room, before: number): PathPoint[] {
  if (s.path) return s.role ? withRole(s.path, s.role) : s.path;
  const shape = defaultShape(before);
  return withRole(shapePath(room, shape), s.role ?? 'soft');
}

/**
 * Adds devices and their strips to the layout (unsaved until the layout is saved), so the new LEDs show up on the
 * Layout page right away; `screen` resizes the room's screen first (from a strip plan). The first real device replaces
 * the test device of a fresh install. Each strip shows the screen's average unless it says otherwise (roles.ts).
 * Resolves to the ids the devices got.
 */
export function useAddToLayout() {
  const { state, dispatch } = useEditor();
  const expert = useExpertTools();
  return (devices: readonly DeviceToAdd[], screen?: Room['screen']): string[] => {
    const replaced = replacedTestDevices(state.spec, expert);
    replaced.forEach((c) => dispatch({ type: 'removeController', controllerId: c.id }));
    if (screen) dispatch({ type: 'updateRoom', patch: { screen } });
    const room = screen ? { ...state.spec.room, screen } : state.spec.room;
    let before = placedStrips(state.spec, expert).length;
    const taken = new Set(state.spec.controllers.filter((c) => !replaced.includes(c)).map((c) => c.id));
    return devices.map(({ controller, strips }) => {
      const id = uniqueId(taken, controller.id);
      taken.add(id);
      dispatch({ type: 'addController', controller: { ...controller, id } });
      for (const s of strips ?? defaultStrips(controller))
        dispatch({ type: 'addStrip', controllerId: id, output: s.output, name: s.name, path: stripPath(s, room, before++) });
      return id;
    });
  };
}
