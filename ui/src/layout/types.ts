// Layout authoring model.
//
// Users place their LED strips in a schematic room: each strip is a path on the walls, the ceiling or the floor,
// and the screen hangs on the front wall. The path's bend points split it into sections; each section repeats a
// stretch of the screen's outline (by default the stretch it faces, which the user can change), or the whole
// picture, one area of it, nothing, or is a gap without LEDs. The compiler turns that into the engine's per-LED
// sampling rectangles (LedRect), one array per controller. LedRect is an internal/compiled format.

/** Normalized screen rectangle, origin top-left, 0..1 on both axes. */
export interface Area {
  hmin: number;
  hmax: number;
  vmin: number;
  vmax: number;
}

/** Compiled sampling rect for one LED, in the shape the engine's `leds` setting consumes. */
export interface LedRect extends Area {
  group: number;
  /** LED is kept black. */
  disabled?: boolean;
}

/** A wall, the ceiling or the floor. The front wall is the one with the screen; left, right and back are as seen facing it. */
export type Surface = 'front' | 'left' | 'right' | 'back' | 'ceiling' | 'floor';

/**
 * A point on a surface, in meters, as the surface is drawn in the unfolded room: `x` from the surface's left edge,
 * `y` from its top edge. Walls are seen from inside the room (top = ceiling). The ceiling is drawn above the front
 * wall (top = back wall), the floor below it (top = front wall).
 */
export interface RoomPoint {
  surface: Surface;
  x: number;
  y: number;
}

/**
 * A position on the screen's outline: 0..4 clockwise from the top-left corner, one unit per edge (0..1 top, left to
 * right; 1..2 right, top to bottom; 2..3 bottom, right to left; 3..4 left, bottom to top). Values outside 0..4 wrap
 * around, so a stretch can cross the top-left corner (e.g. 3.5 to 4.5).
 */
export type OutlinePosition = number;

/** An sRGB color, 0..255 per channel. */
export type Rgb = [number, number, number];

/**
 * What a section of a strip (from a bend point to the next) does. Without one, the section repeats the stretch of
 * the screen's outline it faces ('auto').
 * - `edge`: repeats the outline from `from` to `to`, its LEDs spread evenly over that stretch in order.
 * - `whole`: every LED shows the average of the whole picture.
 * - `area`: every LED shows the average of one area of the picture.
 * - `none`: the LEDs stay dark.
 * - `effect`: the LEDs play an effect (by its name in the effect catalog, music effects included) on their own,
 *   while the layout shows (music, an effect or a color picked for every light plays on them too). Every section with the same effect shares one running effect, laid out as
 *   if it were the screen (each LED where 'auto' would put it), so it flows across strips.
 * - `color`: the LEDs show one color on their own.
 * - `gap`: there are no LEDs here (a cut and spliced corner, a cable around something); LEDs are spread over the
 *   other sections.
 */
export type Section =
  | { kind: 'edge'; from: OutlinePosition; to: OutlinePosition }
  | { kind: 'whole' }
  | { kind: 'area'; area: Area }
  | { kind: 'none' }
  | { kind: 'effect'; effect: string }
  | { kind: 'color'; color: Rgb }
  | { kind: 'gap' };

/** What a zone of LEDs plays on its own (sections of the kinds 'effect' and 'color'). */
export type ZoneRole = Extract<Section, { kind: 'effect' } | { kind: 'color' }>;

/**
 * LEDs that play something on their own instead of the screen (or whatever else shows): every LED of every section
 * with the same role, across strips and controllers.
 */
export interface CompiledZone {
  role: ZoneRole;
  /** Indices in the engine's LED list: the controllers one after the other, each as long as its outputs reach. */
  leds: number[];
  /** Where each of those LEDs would sample the screen (as 'auto' would), the layout an effect's picture is mapped with. */
  rects: LedRect[];
}

/** A bend point of a strip's path. */
export interface PathPoint extends RoomPoint {
  /**
   * Where along the strip this point is, in LEDs: LED k spans [k, k + 1), so its middle is k + 0.5. Set when the
   * point was matched with the real lights; points without it are spaced by length between their neighbors (gaps
   * count no length; the first point defaults to 0, the last to the strip's LED count).
   */
  at?: number;
  /** The section from this point to the next one; none = 'auto'. Ignored on the last point. */
  next?: Section;
}

/** The schematic room. Sizes in meters. */
export interface Room {
  /** Left wall to right wall. */
  width: number;
  /** Front wall to back wall. */
  depth: number;
  /** Floor to ceiling. */
  height: number;
  /** The screen on the front wall, in front-wall coordinates (top-left corner and size). */
  screen: { x: number; y: number; width: number; height: number };
  /** Furniture drawn for orientation only. */
  furniture: 'desk' | 'tv' | 'none';
}

/** A run of LEDs on a strip: `count` LEDs from LED `start`. */
export interface LedRange {
  start: number;
  count: number;
}

/** One physical LED strip, wired to one output of a controller. */
export interface Strip {
  id: string;
  name: string;
  controllerId: string;
  /** ControllerOutput.index this strip is wired to. */
  output: number;
  /** LEDs on the strip, at most its output's length (the rest of the output stays dark). */
  ledCount: number;
  /** Where the strip runs, from its first LED to its last: two or more points; sections between them. */
  path: PathPoint[];
  /** LEDs kept dark, e.g. where the strip runs behind a stand. */
  dark?: LedRange[];
  /**
   * How far into the picture each LED looks, in screen heights (0..1]; on the left and right edges the same distance
   * (scaled by height / width). Default 0.1.
   */
  depth?: number;
  /** How wide each LED looks along the screen edge, as a multiple of the LED spacing (> 0). Default 1.2. */
  blend?: number;
  /** Scale for this strip's colors, 0..1 (e.g. a brighter ceiling strip). Default 1. */
  brightness?: number;
}

/** One physical data output of a controller, occupying [start, start + length) of the controller's LED index space. */
export interface ControllerOutput {
  index: number;
  start: number;
  length: number;
  pin?: number;
}

/**
 * 'wled': the outputs are read from the WLED controller at `host`; anything else (the driver type, e.g. 'ddp',
 * 'adalight', 'file') means the outputs are entered by hand.
 */
export type ControllerKind = 'wled' | (string & {});

/** LED driver settings of one controller: the backend's `device` section shape (`type` + the driver's settings). */
export type DriverSettings = Record<string, unknown> & { type?: string };

export interface Controller {
  id: string;
  name: string;
  kind: ControllerKind;
  /** Hostname or IP (WLED controllers; empty otherwise). */
  host: string;
  outputs: ControllerOutput[];
  /** How Backbloom talks to the controller. The backend fills it in when a layout without it is saved. */
  device?: DriverSettings;
  /** False: the controller keeps its place in the layout but nothing is sent to it. Default true. */
  enabled?: boolean;
}

/**
 * A preset's own Look: the transition time with its spring and the color values, applied on top of the saved
 * `smoothing` (on) and `color.channelAdjustment[0]` while the preset is on, without saving them (ui/src/settings/intensity.ts
 * has the five Looks).
 */
export interface ProfileLook {
  time_ms: number;
  stiffness: number;
  damping: number;
  saturationGain: number;
  luminanceGain: number;
  backlightThreshold: number;
}

/**
 * A preset (the UI's word; a profile here): what the strips show while it's on, an optional Look, and the programs
 * that turn it on while they run. It is turned on by hand (`profile select`) or by one of its programs. Only what the
 * sections do changes; the strips, their paths and LED counts are the layout's.
 */
export interface Profile {
  id: string;
  name: string;
  /**
   * Programs that turn the preset on while they run ("Used while running"): executable names such as "witcher3.exe" (case
   * doesn't matter); may be empty (turned on by hand only). When programs of several presets run, the first in the
   * list wins.
   */
  apps: string[];
  /** Its own Look; absent or null: the saved Look stays. */
  look?: ProfileLook | null;
  /**
   * What the profile changes, per strip id: what each section does (index = the section's index, i.e. the index of
   * the point it starts at; null = 'auto'). Strips not listed, gaps, and lists whose length doesn't match the strip's
   * sections any more keep the layout's sections. A profile can't add a gap.
   */
  sections: Record<string, Array<Section | null>>;
}

export interface LayoutSpec {
  version: 1;
  room: Room;
  controllers: Controller[];
  strips: Strip[];
  /** Per-program roles; without them, or while none of their programs runs, the layout as it is. */
  profiles?: Profile[];
}

/** Compiler output for one controller: one rect per LED, covering the controller's whole index space. */
export interface CompiledController {
  controllerId: string;
  leds: LedRect[];
}

export type IssueSeverity = 'error' | 'warning';

export interface LayoutIssue {
  severity: IssueSeverity;
  message: string;
  stripId?: string;
  controllerId?: string;
  /** The path point (or the section starting there) the issue is about. */
  pointIndex?: number;
  /** The issue is about the room (its size or the screen). */
  room?: boolean;
}
