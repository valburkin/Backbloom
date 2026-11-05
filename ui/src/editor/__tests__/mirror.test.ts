import { describe, expect, it } from 'vitest';
import { defaultRoom, type LayoutSpec, type PathPoint, type Profile, type Section, type Strip } from '../../layout';
import { profileLook } from '../../settings/intensity';
import { whatPlays } from '../../control/presets';
import { writePick } from '../../control/setup';
import type { MainPick } from '../../control/show';
import { editorReducer, initialEditorState, isDirty, type EditorAction, type EditorState } from '../state';
import { mirrorId, revertTouches, sameValue, touchesOf } from '../mirror';

const at = (x: number, next?: Section): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });
const strip = (id: string, sections: Array<Section | undefined>): Strip => ({
  id,
  name: id,
  controllerId: 'c',
  output: 0,
  ledCount: 30,
  path: [...sections.map((s, i) => at(i * 0.3, s)), at(sections.length * 0.3)],
});
const gaming: Profile = { id: 'p', name: 'Gaming', apps: [], sections: { a: [null, null] } };
const saved: LayoutSpec = {
  version: 1,
  room: defaultRoom(),
  controllers: [{ id: 'c', name: 'C', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }] }],
  strips: [strip('a', [undefined, undefined])],
  profiles: [gaming],
};
const red: MainPick = { kind: 'color', color: [255, 0, 0] };
const blue: MainPick = { kind: 'color', color: [0, 0, 255] };
const run = (s: EditorState, ...actions: EditorAction[]) => actions.reduce(editorReducer, s);
const loaded = () => initialEditorState(saved, saved);

/** A pick for Gaming (or Everyday) as usePick makes it: mirrored into the edited layout; its id, for mirrorDone. */
function pick(st: EditorState, what: MainPick, preset: string | null = 'p'): [EditorState, string] {
  const id = mirrorId();
  const target = preset === null ? undefined : st.spec.profiles?.find((p) => p.id === preset);
  return [run(st, { type: 'mirror', id, actions: writePick(st.spec, target, what).actions, coalesce: id }), id];
}
/** The layout the backend saved for that pick, made to the layout saved last. */
const savedPick = (st: EditorState, what: MainPick) => writePick(st.saved!, st.saved!.profiles!.find((p) => p.id === 'p'), what).spec;

describe('changes saved by themselves, mirrored into the edited layout', () => {
  it('aren’t unsaved edits while they are saved; Layout edits still are', () => {
    const [st, id] = pick(loaded(), red);
    expect(whatPlays(st.spec, st.spec.profiles![0])).toEqual(red);
    expect(isDirty(st)).toBe(false);
    // a Layout edit meanwhile counts
    const edited = run(st, { type: 'updateStrip', stripId: 'a', patch: { name: 'Desk' } });
    expect(isDirty(edited)).toBe(true);
    // a Layout edit of the same value counts too
    const recolored = run(st, { type: 'updateProfile', profileId: 'p', patch: { sections: { a: [{ kind: 'color', color: [0, 255, 0] }, null] } } });
    expect(isDirty(recolored)).toBe(true);
    // saved: nothing left to save
    const done = run(st, { type: 'markSaved', spec: savedPick(st, red) }, { type: 'mirrorDone', id, saved: true });
    expect(done.mirrors).toEqual([]);
    expect(isDirty(done)).toBe(false);
  });

  it('two picks that both fail: the preset goes back to the saved one, not to the first pick', () => {
    const [a, first] = pick(loaded(), red);
    const [b, second] = pick(a, blue);
    const afterFirst = run(b, { type: 'mirrorDone', id: first, saved: false });
    // the second pick is still being saved: it stays
    expect(whatPlays(afterFirst.spec, afterFirst.spec.profiles![0])).toEqual(blue);
    const afterBoth = run(afterFirst, { type: 'mirrorDone', id: second, saved: false });
    expect(afterBoth.spec.profiles).toEqual(saved.profiles);
    expect(isDirty(afterBoth)).toBe(false);
  });

  it('the first pick fails and the second is saved: the second stays', () => {
    const [a, first] = pick(loaded(), red);
    const [b, second] = pick(a, blue);
    const failed = run(b, { type: 'mirrorDone', id: first, saved: false });
    const done = run(failed, { type: 'markSaved', spec: savedPick(failed, blue) }, { type: 'mirrorDone', id: second, saved: true });
    expect(whatPlays(done.spec, done.spec.profiles![0])).toEqual(blue);
    expect(isDirty(done)).toBe(false);
  });

  it('a failed Everyday pick takes back only the parts it wrote, from the layout as it is then', () => {
    const [st, id] = pick(loaded(), red, null);
    expect(whatPlays(st.spec)).toEqual(red);
    // on Layout meanwhile: the strip renamed, a point moved and its second part made Off
    const path = st.spec.strips[0].path.map((p, i) => (i === 1 ? { ...p, x: 0.4, next: { kind: 'none' } as Section } : p));
    const edited = run(st, { type: 'updateStrip', stripId: 'a', patch: { name: 'Desk', path } });
    const back = run(edited, { type: 'mirrorDone', id, saved: false });
    const a = back.spec.strips[0];
    expect(a.name).toBe('Desk');
    expect(a.path[0].next).toBeUndefined();
    expect(a.path[1]).toEqual({ surface: 'front', x: 0.4, y: 1, next: { kind: 'none' } });
  });

  it('Look edits in one mirror, then another, both failing: the saved Look comes back', () => {
    const look = (id: string, values: Profile['look']): EditorAction => ({ type: 'mirror', id, actions: [{ type: 'updateProfile', profileId: 'p', patch: { look: values } }] });
    const first = mirrorId();
    const second = mirrorId();
    const st = run(loaded(), look(first, profileLook('intense')), look(first, { ...profileLook('intense'), time_ms: 900 }), look(second, profileLook('relaxed')));
    expect(st.mirrors.map((m) => m.touches.length)).toEqual([1, 1]);
    expect(isDirty(st)).toBe(false);
    const back = run(st, { type: 'mirrorDone', id: first, saved: false }, { type: 'mirrorDone', id: second, saved: false });
    expect(back.spec.profiles![0]).toEqual(gaming);
    expect(isDirty(back)).toBe(false);
  });

  it('a mirror whose save ends after the layout was loaded again leaves it alone', () => {
    const [st, id] = pick(loaded(), red);
    const reloaded = run(st, { type: 'load', spec: saved });
    expect(run(reloaded, { type: 'mirrorDone', id, saved: false })).toBe(reloaded);
  });
});

describe('what a mirror wrote', () => {
  it('is kept per preset list and per part, and taken back where it is unchanged', () => {
    const after: LayoutSpec = {
      ...saved,
      strips: [strip('a', [{ kind: 'whole' }, undefined])],
      profiles: [{ ...gaming, sections: { a: [null, null], b: [null] }, look: profileLook('rich') }],
    };
    const touches = touchesOf(saved, after);
    expect(touches.map((t) => t.kind).sort()).toEqual(['look', 'part', 'sections']);
    expect(revertTouches(after, saved, touches)).toEqual(saved);
    expect(revertTouches(saved, saved, touches)).toBe(saved);
  });

  it('compares values as JSON does', () => {
    expect(sameValue({ a: 1, b: [1, { c: 2 }] }, { b: [1, { c: 2 }], a: 1 })).toBe(true);
    expect(sameValue({ a: 1, b: undefined }, { a: 1 })).toBe(true);
    expect(sameValue({ a: null }, {})).toBe(false);
    expect(sameValue([1, 2], [2, 1])).toBe(false);
  });
});
