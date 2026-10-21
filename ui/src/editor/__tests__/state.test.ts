import { describe, expect, it } from 'vitest';
import { applyProfile, ceilingFront, compileStrip, defaultRoom, aroundScreen, type LayoutSpec, type PathPoint } from '../../layout';
import {
  canRedo,
  canUndo,
  COALESCE_MS,
  editorReducer,
  HISTORY_LIMIT,
  initialEditorState,
  isDirty,
  lastStepAddedStrip,
  selectedStrip,
  type EditorAction,
  type EditorState,
} from '../state';
import { carryWithScreen, pinUnevenness, rangesFromMask, reverseStrip, toggleDark, unpinPoint } from '../strips';

const room = defaultRoom();

const spec: LayoutSpec = {
  version: 1,
  room,
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
    { id: 'strip-1', name: 'Bottom', controllerId: 'wled-a', output: 0, ledCount: 91, path: aroundScreen(room, { start: 'bottom-right', clockwise: true }) },
    { id: 'strip-2', name: 'Top', controllerId: 'wled-a', output: 1, ledCount: 77, path: ceilingFront(room) },
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
    expect(s.selection).toEqual({ stripId: 'strip-1', pointIndex: null, sectionIndex: null });
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
    const moved = (x: number): PathPoint[] => [{ surface: 'front', x, y: 1 }, ...spec.strips[0].path.slice(1)];
    let s = loaded();
    for (let i = 1; i <= 5; i++)
      s = run(s, { type: 'updateStrip', stripId: 'strip-1', patch: { path: moved(i / 10) }, coalesce: 'drag', now: 1000 + i * 50 });
    expect(s.past).toHaveLength(1);
    expect(s.spec.strips[0].path[0].x).toBe(0.5);
    s = run(s, { type: 'undo' });
    expect(s.spec.strips[0].path).toBe(spec.strips[0].path);
  });

  it('does not coalesce across different keys, gaps longer than COALESCE_MS, or after undo', () => {
    const named = (name: string, coalesce: string, now: number): EditorAction => ({ type: 'updateStrip', stripId: 'strip-1', patch: { name }, coalesce, now });
    let s = run(loaded(), named('a', 'a', 1000), named('b', 'b', 1010), named('c', 'b', 1010 + COALESCE_MS + 1));
    expect(s.past).toHaveLength(3);
    s = run(s, { type: 'undo' }, named('d', 'b', 2020));
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
    const s = run(loaded(), { type: 'selectStrip', stripId: 'strip-2' }, { type: 'selectPoint', index: 1 });
    expect(s.past).toHaveLength(0);
    expect(s.selection).toEqual({ stripId: 'strip-2', pointIndex: 1, sectionIndex: null });
    expect(selectedStrip(s)?.name).toBe('Top');
  });

  it('selectPoint and selectSection clamp out-of-range indexes', () => {
    const s = run(loaded(), { type: 'selectPoint', index: 9 }, { type: 'selectSection', index: 9 });
    expect(s.selection.pointIndex).toBeNull();
    expect(s.selection.sectionIndex).toBeNull();
    expect(run(loaded(), { type: 'selectSection', index: 1 }).selection.sectionIndex).toBe(1);
  });

  it('ignores unknown ids without creating history', () => {
    const s = run(
      loaded(),
      { type: 'updateStrip', stripId: 'nope', patch: { name: 'x' } },
      { type: 'removeStrip', stripId: 'nope' },
      { type: 'moveStrip', stripId: 'nope', to: 0 },
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
    expect(added.ledCount).toBe(77);
    expect(added.path).toEqual(ceilingFront(room));
    expect(s.selection).toEqual({ stripId: 'strip-2', pointIndex: null, sectionIndex: null });
  });

  it('a strip brightness back at 100 % is dropped from the spec', () => {
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-2', patch: { brightness: 0.6 } });
    expect(s.spec.strips[1].brightness).toBe(0.6);
    s = run(s, { type: 'updateStrip', stripId: 'strip-2', patch: { brightness: undefined } });
    expect('brightness' in s.spec.strips[1]).toBe(false);
    expect(isDirty(s)).toBe(false);
  });

  it('controllers of other drivers sit next to WLED ones, with their own driver settings', () => {
    const desk = { id: 'desk', name: 'Desk', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }], device: { type: 'ddp', host: '10.0.0.9' } };
    let s = run(loaded(), { type: 'addController', controller: desk }, { type: 'addStrip', controllerId: 'desk', output: 0 });
    s = run(s, { type: 'updateController', controllerId: 'desk', patch: { device: { type: 'ddp', host: '10.0.0.10' } } });
    expect(s.spec.controllers.map((c) => c.id)).toEqual(['wled-a', 'desk']);
    expect(s.spec.controllers[1].device).toEqual({ type: 'ddp', host: '10.0.0.10' });
    expect(s.spec.strips[2]).toMatchObject({ controllerId: 'desk', output: 0, ledCount: 30 });
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

  it('drops matched positions past a lowered LED count', () => {
    const path: PathPoint[] = [{ surface: 'front', x: 1, y: 1 }, { surface: 'front', x: 2, y: 1, at: 80 }, { surface: 'front', x: 3, y: 1 }];
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-1', patch: { path } });
    s = run(s, { type: 'updateStrip', stripId: 'strip-1', patch: { ledCount: 60 } });
    expect(s.spec.strips[0].path[1].at).toBeUndefined();
  });

  it('moving the screen carries the strips on it and keeps the others', () => {
    const screen = { ...room.screen, x: room.screen.x + 0.5 };
    const s = run(loaded(), { type: 'updateRoom', patch: { screen } });
    expect(s.spec.strips[0].path[0].x).toBeCloseTo(spec.strips[0].path[0].x + 0.5);
    expect(s.spec.strips[1]).toBe(spec.strips[1]);
  });

  it('a smaller room keeps the screen, seat and points inside it', () => {
    const s = run(loaded(), { type: 'updateRoom', patch: { width: 1.5 } });
    expect(s.spec.room.screen.x + s.spec.room.screen.width).toBeLessThanOrEqual(1.5);
    expect(s.spec.strips[1].path.every((p) => p.x <= 1.5)).toBe(true);
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

  it('profiles: add with a unique id, update, remove; removing a strip drops it from them', () => {
    const game = { id: '', name: 'Game', apps: ['game.exe'], sections: { 'strip-1': [null] } };
    let s = run(loaded(), { type: 'addProfile', profile: game }, { type: 'addProfile', profile: { ...game, id: 'profile-1', name: 'Movies' } });
    expect(s.spec.profiles?.map((p) => [p.id, p.name])).toEqual([['profile-1', 'Game'], ['profile-2', 'Movies']]);
    s = run(s, { type: 'updateProfile', profileId: 'profile-2', patch: { apps: ['vlc.exe'] } });
    expect(s.spec.profiles?.[1].apps).toEqual(['vlc.exe']);
    s = run(s, { type: 'removeStrip', stripId: 'strip-1' });
    expect(s.spec.profiles?.[0].sections).toEqual({});
    s = run(s, { type: 'removeProfile', profileId: 'profile-1' }, { type: 'removeProfile', profileId: 'profile-2' });
    expect('profiles' in s.spec).toBe(false);
  });

  it('removing a controller drops its strips from the profiles, so a new strip with a reused id starts clean', () => {
    const game = { id: 'game', name: 'Game', apps: ['game.exe'], sections: { 'strip-2': [{ kind: 'none' as const }] } };
    const second = { id: 'tv', name: 'TV', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }] };
    let s = run(loaded(), { type: 'addProfile', profile: game }, { type: 'addController', controller: second });
    s = run(s, { type: 'removeStrip', stripId: 'strip-2' }, { type: 'addStrip', controllerId: 'tv', output: 0 });
    expect(s.spec.strips[1].id).toBe('strip-2');
    s = run(s, { type: 'updateProfile', profileId: 'game', patch: { sections: { 'strip-2': [{ kind: 'none' }] } } });
    s = run(s, { type: 'removeController', controllerId: 'tv' });
    expect(s.spec.profiles?.[0].sections).toEqual({});
    s = run(s, { type: 'addStrip' });
    expect(s.spec.strips[1].id).toBe('strip-2');
    expect(s.spec.profiles?.[0].sections).toEqual({});
  });

  it('reversing a strip turns its profile sections around with it', () => {
    // a profile's list is per section index: unchanged, it would put the profile's roles on the other end of the strip
    const path: PathPoint[] = [
      { surface: 'front', x: 1, y: 1 },
      { surface: 'front', x: 2, y: 1, next: { kind: 'gap' } },
      { surface: 'front', x: 2.5, y: 1 },
      { surface: 'front', x: 3, y: 1 },
    ];
    const game = { id: 'game', name: 'Game', apps: [], sections: { 'strip-2': [{ kind: 'effect' as const, effect: 'Candle' }, null, { kind: 'edge' as const, from: 0.2, to: 0.8 }] } };
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-2', patch: { path, ledCount: 30 } }, { type: 'addProfile', profile: game });
    const before = applyProfile(s.spec, 'game').strips[1];
    s = run(s, { type: 'reverseStrip', stripId: 'strip-2' });
    expect(s.spec.profiles?.[0].sections['strip-2']).toEqual([{ kind: 'edge', from: 0.8, to: 0.2 }, null, { kind: 'effect', effect: 'Candle' }]);
    const after = applyProfile(s.spec, 'game').strips[1];
    expect(compileStrip(after, s.spec.room)).toEqual(compileStrip(before, s.spec.room).reverse());
    s = run(s, { type: 'undo' });
    expect(s.spec.profiles?.[0].sections['strip-2']).toEqual(game.sections['strip-2']);
  });

  it('tells whether undo would take back just the strip added last (canceling a drawing)', () => {
    let s = run(loaded(), { type: 'updateStrip', stripId: 'strip-1', patch: { name: 'Mine' } }, { type: 'addStrip', shape: 'line' });
    const added = s.spec.strips[2].id;
    expect(lastStepAddedStrip(s, added)).toBe(true);
    expect(lastStepAddedStrip(s, 'strip-1')).toBe(false);
    // added, then taken back with Ctrl+Z: another undo would take back the rename
    expect(lastStepAddedStrip(run(s, { type: 'undo' }), added)).toBe(false);
    s = run(s, { type: 'updateStrip', stripId: added, patch: { ledCount: 12 } });
    expect(lastStepAddedStrip(s, added)).toBe(false);
  });

  it('undo with no history is a no-op', () => {
    const s = loaded();
    expect(run(s, { type: 'undo' })).toBe(s);
    expect(run(s, { type: 'redo' })).toBe(s);
  });
});

describe('strip helpers', () => {
  it('measures how uneven pins make a strip, and unpins one point', () => {
    const line = (at?: number) => ({
      ...spec.strips[1],
      ledCount: 20,
      path: [{ surface: 'front' as const, x: 0, y: 1 }, { surface: 'front' as const, x: 1, y: 1, ...(at === undefined ? {} : { at }) }, { surface: 'front' as const, x: 2, y: 1 }],
    });
    expect(pinUnevenness(room, line())).toBe(1);
    expect(pinUnevenness(room, line(10))).toBe(1);
    expect(pinUnevenness(room, line(16))).toBe(4);
    expect(unpinPoint(line(16).path, 1)[1].at).toBeUndefined();
  });

  it('reverses a strip with its sections, matched points and dark parts', () => {
    const strip = {
      ...spec.strips[1],
      ledCount: 10,
      path: [
        { surface: 'front' as const, x: 1, y: 1, next: { kind: 'gap' as const } },
        { surface: 'front' as const, x: 2, y: 1, at: 3, next: { kind: 'edge' as const, from: 0, to: 1 } },
        { surface: 'front' as const, x: 3, y: 1 },
      ],
      dark: [{ start: 0, count: 2 }],
    };
    const r = reverseStrip(strip);
    expect(r.path.map((p) => [p.x, p.at, p.next])).toEqual([
      [3, undefined, { kind: 'edge', from: 1, to: 0 }],
      [2, 7, { kind: 'gap' }],
      [1, undefined, undefined],
    ]);
    expect(r.dark).toEqual([{ start: 8, count: 2 }]);
  });

  it('toggles single LEDs dark and merges neighbors', () => {
    const strip = { ...spec.strips[1], ledCount: 6, dark: [{ start: 1, count: 1 }] };
    expect(toggleDark(strip, 2)).toEqual([{ start: 1, count: 2 }]);
    expect(toggleDark(strip, 1)).toBeUndefined();
    expect(rangesFromMask([true, false, true, true])).toEqual([{ start: 0, count: 1 }, { start: 2, count: 2 }]);
  });

  it('scales points on the screen with it and keeps the distance of points around it', () => {
    const from = { x: 1, y: 1, width: 1, height: 0.5 };
    const to = { x: 2, y: 1, width: 2, height: 0.5 };
    const path: PathPoint[] = [
      { surface: 'front', x: 1.5, y: 1 },
      { surface: 'front', x: 2.05, y: 1.2 },
      { surface: 'front', x: 0.5, y: 1 },
      { surface: 'ceiling', x: 1.5, y: 1 },
    ];
    expect(carryWithScreen(path, from, to).map((p) => p.x)).toEqual([3, 4.05, 0.5, 1.5]);
  });
});
