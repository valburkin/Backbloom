import { describe, expect, it } from 'vitest';
import type { LayoutSpec } from '../../layout';
import {
  canRedo,
  canUndo,
  COALESCE_MS,
  editorReducer,
  HISTORY_LIMIT,
  initialEditorState,
  isDirty,
  selectedStrip,
  type EditorAction,
  type EditorState,
} from '../state';
import { newEdgeSegment, newOffSegment, convertSegment, describeSegment } from '../segments';

const spec: LayoutSpec = {
  version: 1,
  controllers: [
    {
      id: 'wled-a',
      name: 'Desk',
      kind: 'wled',
      host: 'wled-a.local',
      outputs: [
        { index: 0, start: 0, length: 91 },
        { index: 1, start: 91, length: 77 },
      ],
    },
  ],
  strips: [
    { id: 'strip-1', name: 'Bottom', controllerId: 'wled-a', output: 0, segments: [newEdgeSegment(40, 'bottom'), newOffSegment(2)] },
    { id: 'strip-2', name: 'Top', controllerId: 'wled-a', output: 1, segments: [newEdgeSegment(77, 'top')] },
  ],
};

const run = (s: EditorState, ...actions: EditorAction[]) => actions.reduce(editorReducer, s);
const loaded = () => run(initialEditorState(), { type: 'load', spec });

describe('editor reducer', () => {
  it('load sets a clean baseline with empty history and a valid selection', () => {
    const s = loaded();
    expect(s.spec).toBe(spec);
    expect(isDirty(s)).toBe(false);
    expect(canUndo(s) || canRedo(s)).toBe(false);
    expect(s.selection).toEqual({ stripId: 'strip-1', segmentIndex: null });
  });

  it('load keeps the selected strip when it still exists', () => {
    const s = run(loaded(), { type: 'selectStrip', stripId: 'strip-2' }, { type: 'load', spec });
    expect(s.selection.stripId).toBe('strip-2');
  });

  it('edits mark dirty; undo restores and clears dirty; redo reapplies', () => {
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-1', patch: { name: 'Under desk' } });
    expect(s.spec.strips[0].name).toBe('Under desk');
    expect(isDirty(s)).toBe(true);
    s = run(s, { type: 'undo' });
    expect(s.spec.strips[0].name).toBe('Bottom');
    expect(isDirty(s)).toBe(false);
    expect(canRedo(s)).toBe(true);
    s = run(s, { type: 'redo' });
    expect(s.spec.strips[0].name).toBe('Under desk');
    expect(canRedo(s)).toBe(false);
  });

  it('a new edit after undo clears the redo stack', () => {
    const s = run(
      loaded(),
      { type: 'updateStrip', stripId: 'strip-1', patch: { name: 'A' } },
      { type: 'undo' },
      { type: 'updateStrip', stripId: 'strip-1', patch: { name: 'B' } },
    );
    expect(canRedo(s)).toBe(false);
    expect(s.past).toHaveLength(1);
  });

  it('coalesces rapid edits with the same key into one undo step', () => {
    const seg = (from: number) => ({ ...newEdgeSegment(40, 'bottom'), from });
    let s = loaded();
    for (let i = 1; i <= 5; i++)
      s = run(s, { type: 'updateSegment', stripId: 'strip-1', index: 0, segment: seg(i / 10), coalesce: 'drag-from', now: 1000 + i * 50 });
    expect(s.past).toHaveLength(1);
    expect((s.spec.strips[0].segments[0] as { from: number }).from).toBe(0.5);
    s = run(s, { type: 'undo' });
    expect((s.spec.strips[0].segments[0] as { from: number }).from).toBe(0);
  });

  it('does not coalesce across different keys, gaps longer than COALESCE_MS, or after undo', () => {
    const seg = (from: number) => ({ ...newEdgeSegment(40, 'bottom'), from });
    let s = run(
      loaded(),
      { type: 'updateSegment', stripId: 'strip-1', index: 0, segment: seg(0.1), coalesce: 'a', now: 1000 },
      { type: 'updateSegment', stripId: 'strip-1', index: 0, segment: seg(0.2), coalesce: 'b', now: 1010 },
      { type: 'updateSegment', stripId: 'strip-1', index: 0, segment: seg(0.3), coalesce: 'b', now: 1010 + COALESCE_MS + 1 },
    );
    expect(s.past).toHaveLength(3);
    s = run(s, { type: 'undo' }, { type: 'updateSegment', stripId: 'strip-1', index: 0, segment: seg(0.4), coalesce: 'b', now: 2020 });
    expect(s.past).toHaveLength(3);
  });

  it('caps history at HISTORY_LIMIT', () => {
    let s = loaded();
    for (let i = 0; i < HISTORY_LIMIT + 20; i++) s = run(s, { type: 'updateStrip', stripId: 'strip-1', patch: { name: `n${i}` } });
    expect(s.past).toHaveLength(HISTORY_LIMIT);
  });

  it('markSaved makes the current spec the baseline without touching history', () => {
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-1', patch: { name: 'X' } });
    s = run(s, { type: 'markSaved', spec: s.spec });
    expect(isDirty(s)).toBe(false);
    expect(canUndo(s)).toBe(true);
    s = run(s, { type: 'undo' });
    expect(isDirty(s)).toBe(true);
  });

  it('selection-only actions do not create history', () => {
    const s = run(loaded(), { type: 'selectStrip', stripId: 'strip-2' }, { type: 'selectSegment', index: 0 });
    expect(s.past).toHaveLength(0);
    expect(s.selection).toEqual({ stripId: 'strip-2', segmentIndex: 0 });
    expect(selectedStrip(s)?.name).toBe('Top');
  });

  it('selectSegment clamps out-of-range indexes', () => {
    const s = run(loaded(), { type: 'selectSegment', index: 9 });
    expect(s.selection.segmentIndex).toBeNull();
  });

  it('addSegment / removeSegment / moveSegment update segments and selection', () => {
    let s = run(loaded(), { type: 'addSegment', stripId: 'strip-1', segment: newOffSegment(5), index: 0 });
    expect(s.spec.strips[0].segments.map((x) => x.kind)).toEqual(['off', 'edge', 'off']);
    expect(s.selection.segmentIndex).toBe(0);
    s = run(s, { type: 'moveSegment', stripId: 'strip-1', from: 0, to: 2 });
    expect(s.spec.strips[0].segments.map((x) => x.count)).toEqual([40, 2, 5]);
    expect(s.selection.segmentIndex).toBe(2);
    s = run(s, { type: 'removeSegment', stripId: 'strip-1', index: 2 });
    expect(s.spec.strips[0].segments).toHaveLength(2);
    expect(s.selection.segmentIndex).toBe(1);
    expect(s.past).toHaveLength(3);
  });

  it('ignores invalid moves and unknown ids without creating history', () => {
    const s = run(
      loaded(),
      { type: 'moveSegment', stripId: 'strip-1', from: 0, to: 5 },
      { type: 'removeSegment', stripId: 'nope', index: 0 },
      { type: 'updateStrip', stripId: 'nope', patch: { name: 'x' } },
      { type: 'removeStrip', stripId: 'nope' },
    );
    expect(s.past).toHaveLength(0);
  });

  it('addStrip picks a free output and a unique id, and selects it', () => {
    const base: LayoutSpec = { ...spec, strips: [spec.strips[0]] };
    const s = run(initialEditorState(base, base), { type: 'addStrip' });
    const added = s.spec.strips[1];
    expect(added.id).toBe('strip-2');
    expect(added.output).toBe(1);
    expect(added.controllerId).toBe('wled-a');
    expect(s.selection).toEqual({ stripId: 'strip-2', segmentIndex: 0 });
  });

  it('removeStrip moves selection to a neighbor', () => {
    const s = run(loaded(), { type: 'selectStrip', stripId: 'strip-2' }, { type: 'removeStrip', stripId: 'strip-2' });
    expect(s.spec.strips.map((x) => x.id)).toEqual(['strip-1']);
    expect(s.selection.stripId).toBe('strip-1');
  });

  it('moveStrip reorders strips', () => {
    const s = run(loaded(), { type: 'moveStrip', stripId: 'strip-2', to: 0 });
    expect(s.spec.strips.map((x) => x.id)).toEqual(['strip-2', 'strip-1']);
  });

  it('setSegments replaces a strip (template apply) as one undo step', () => {
    let s = run(loaded(), { type: 'setSegments', stripId: 'strip-1', segments: [newEdgeSegment(91, 'left')] });
    expect(s.spec.strips[0].segments).toHaveLength(1);
    s = run(s, { type: 'undo' });
    expect(s.spec.strips[0].segments).toHaveLength(2);
  });

  it('controllers: add with id de-duplication, update, remove with their strips', () => {
    let s = run(loaded(), { type: 'addController', controller: { ...spec.controllers[0], name: 'Second' } });
    expect(s.spec.controllers.map((c) => c.id)).toEqual(['wled-a', 'controller-1']);
    s = run(s, { type: 'updateController', controllerId: 'controller-1', patch: { name: 'TV' } });
    expect(s.spec.controllers[1].name).toBe('TV');
    s = run(s, { type: 'removeController', controllerId: 'wled-a' });
    expect(s.spec.controllers.map((c) => c.id)).toEqual(['controller-1']);
    expect(s.spec.strips).toHaveLength(0);
    expect(s.selection.stripId).toBeNull();
    s = run(s, { type: 'undo' });
    expect(s.spec.strips).toHaveLength(2);
  });

  it('undo with no history is a no-op', () => {
    const s = loaded();
    expect(run(s, { type: 'undo' })).toBe(s);
    expect(run(s, { type: 'redo' })).toBe(s);
  });
});

describe('segment helpers', () => {
  it('convertSegment keeps the count and maps an edge to its screen area', () => {
    const fixed = convertSegment({ ...newEdgeSegment(12, 'right'), from: 0.2, to: 0.8, depth: 0.15 }, 'fixed');
    expect(fixed).toEqual({ kind: 'fixed', count: 12, area: { hmin: 0.85, hmax: 1, vmin: 0.2, vmax: 0.8 } });
    expect(convertSegment(fixed, 'off')).toEqual({ kind: 'off', count: 12 });
    expect(convertSegment(fixed, 'edge')).toMatchObject({ kind: 'edge', count: 12 });
  });

  it('describeSegment uses plain language', () => {
    expect(describeSegment({ ...newEdgeSegment(18, 'bottom'), from: 1, to: 0 })).toBe('18 LEDs · Bottom, right → left');
    expect(describeSegment(newOffSegment(1))).toBe('1 LED · kept dark');
  });
});
