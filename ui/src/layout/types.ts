// Layout authoring model.
//
// Users describe *strips* made of *segments* ("18 LEDs along the bottom, right to left").
// The compiler turns that into the engine's per-LED sampling rectangles (LedRect), one
// array per controller. LedRect is an internal/compiled format; users never edit it.

/** Screen edge. Positions along an edge run left→right (top/bottom) or top→bottom (left/right). */
export type Edge = 'top' | 'bottom' | 'left' | 'right';

/** Normalized screen rectangle, origin top-left, 0..1 on both axes. */
export interface Area {
  hmin: number;
  hmax: number;
  vmin: number;
  vmax: number;
}

/** Compiled sampling rectangle for one LED, in the shape the engine's `leds` setting consumes. */
export interface LedRect extends Area {
  group: number;
  /** LED is kept black. */
  disabled?: boolean;
}

/** A run of LEDs that mirrors part of a screen edge. */
export interface EdgeSegment {
  kind: 'edge';
  count: number;
  edge: Edge;
  /**
   * Where the run starts and ends along the edge (0..1). LED k samples around
   * from + (k + 0.5) * (to - from) / count. from > to means the strip runs backwards.
   * Values may slightly exceed 0..1 (strip longer than the screen); rects are clamped.
   */
  from: number;
  to: number;
  /** How far into the screen each LED samples, as a fraction of the screen (0..1]. */
  depth: number;
  /** Sampling width along the edge as a multiple of LED spacing. 1 = touching, >1 = overlapping (smoother). Default 1.2. */
  blend?: number;
}

/** A run of LEDs that all sample the same area (e.g. strip passing behind a monitor stand). */
export interface FixedSegment {
  kind: 'fixed';
  count: number;
  area: Area;
}

/** A run of LEDs that stays dark (e.g. strip routed around a corner or out of sight). */
export interface OffSegment {
  kind: 'off';
  count: number;
}

export type Segment = EdgeSegment | FixedSegment | OffSegment;

/** One physical LED strip, wired to one output of a controller. Segments are listed in wiring order. */
export interface Strip {
  id: string;
  name: string;
  controllerId: string;
  /** ControllerOutput.index this strip is wired to. */
  output: number;
  segments: Segment[];
}

/** One physical data output of a controller, occupying [start, start + length) of the controller's LED index space. */
export interface ControllerOutput {
  index: number;
  start: number;
  length: number;
  pin?: number;
}

export type ControllerKind = 'wled';

export interface Controller {
  id: string;
  name: string;
  kind: ControllerKind;
  /** Hostname or IP. */
  host: string;
  outputs: ControllerOutput[];
}

export interface LayoutSpec {
  version: 1;
  controllers: Controller[];
  strips: Strip[];
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
  segmentIndex?: number;
}
