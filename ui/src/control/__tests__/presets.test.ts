import { describe, expect, it } from 'vitest';
import { applyProfile, defaultRoom, validateLayout, type LayoutSpec, type PathPoint, type Profile, type Section, type Strip } from '../../layout';
import { profileLook } from '../../settings/intensity';
import { editorReducer, initialEditorState, isDirty, type EditorState } from '../../editor/state';
import { writePick } from '../setup';
import type { MainPick } from '../show';
import {
  addGameToGaming,
  COZY_COLOR,
  currentSections,
  defaultMusicEffect,
  defaultPresets,
  followStrips,
  freeName,
  newPreset,
  presetStatus,
  presetSummary,
  sectionsFor,
  statusText,
  switchToEveryday,
  whatPlays,
  withDefaultPresets,
} from '../presets';

const at = (x: number, next?: Section): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });
const strip = (id: string, sections: Array<Section | undefined>): Strip => ({
  id,
  name: id,
  controllerId: 'c',
  output: 0,
  ledCount: 30,
  path: [...sections.map((s, i) => at(i * 0.3, s)), at(sections.length * 0.3)],
});
const edge: Section = { kind: 'edge', from: 0.2, to: 0.8 };
const off: Section = { kind: 'none' };
const gap: Section = { kind: 'gap' };
const base: LayoutSpec = {
  version: 1,
  room: defaultRoom(),
  controllers: [{ id: 'c', name: 'C', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }] }],
  strips: [strip('a', [undefined, off, edge, gap])],
};
const preset = (sections: Profile['sections'], extra: Partial<Profile> = {}): Profile => ({ id: 'p', name: 'Gaming', apps: [], sections, ...extra });

describe('what a preset plays', () => {
  it('writes a choice into every strip, keeping the layout’s Off parts off and its gaps', () => {
    expect(sectionsFor(base, { kind: 'edges' })).toEqual({ a: [null, off, edge, null] });
    expect(sectionsFor(base, { kind: 'average' })).toEqual({ a: [{ kind: 'whole' }, off, { kind: 'whole' }, null] });
    expect(sectionsFor(base, { kind: 'effect', effect: 'Candle' })).toEqual({ a: [{ kind: 'effect', effect: 'Candle' }, off, { kind: 'effect', effect: 'Candle' }, null] });
    const p = preset(sectionsFor(base, { kind: 'color', color: [1, 2, 3] }));
    expect(validateLayout({ ...base, profiles: [p] })).toEqual([]);
    expect(applyProfile({ ...base, profiles: [p] }, 'p').strips[0].path.map((x) => x.next?.kind)).toEqual(['color', 'none', 'color', 'gap', undefined]);
  });

  it('reads it back, customized when the strips differ', () => {
    const plays: Array<Parameters<typeof sectionsFor>[1]> = [{ kind: 'edges' }, { kind: 'average' }, { kind: 'effect', effect: 'Candle' }, { kind: 'color', color: [1, 2, 3] }];
    for (const play of plays)
      expect(whatPlays(base, preset(sectionsFor(base, play)))).toEqual(play);
    // nothing changed: what the layout plays
    expect(whatPlays(base, preset({}))).toEqual({ kind: 'edges' });
    expect(whatPlays(base, preset({ a: [{ kind: 'whole' }, off, null, null] }))).toEqual({ kind: 'customized' });
    // off where the layout isn't
    expect(whatPlays(base, preset({ a: [off, off, null, null] }))).toEqual({ kind: 'customized' });
    expect(presetSummary(base, preset(sectionsFor(base, { kind: 'average' }), { look: profileLook('relaxed') }))).toBe('Screen average · Relaxed');
    expect(presetSummary(base, preset({}))).toBe('Screen edges');
  });

  it('starts a new one from the setup in use and its Look', () => {
    const look = profileLook('rich');
    const music = preset(sectionsFor(base, { kind: 'effect', effect: 'Candle' }));
    const withMusic = { ...base, profiles: [music] };
    const fromPreset = newPreset(withMusic, { profileId: 'p', look });
    expect(fromPreset).toMatchObject({ name: 'Preset 2', apps: [], look });
    expect(whatPlays(withMusic, fromPreset)).toEqual({ kind: 'effect', effect: 'Candle' });
    const fromEveryday = newPreset(base, { profileId: null, look: null });
    expect(fromEveryday.sections).toEqual(currentSections(base, null));
    expect(fromEveryday).not.toHaveProperty('look');
  });

  it('adds your game to Gaming, making Gaming again when it was deleted', () => {
    const withDefaults = withDefaultPresets(base, []);
    const change = addGameToGaming(withDefaults, 'C:/Games/Witcher3.EXE', []);
    expect(change).toEqual({ actions: [{ type: 'updateProfile', profileId: 'gaming', patch: { apps: ['witcher3.exe'] } }], message: 'Gaming is used while witcher3.exe runs' });
    const withGame = { ...withDefaults, profiles: withDefaults.profiles!.map((p) => (p.id === 'gaming' ? { ...p, apps: ['witcher3.exe'] } : p)) };
    expect(addGameToGaming(withGame, 'witcher3.exe', [])).toEqual({ actions: [], message: 'Gaming is used while witcher3.exe runs already' });
    expect(addGameToGaming(withGame, 'cs2', [])?.actions).toEqual([{ type: 'updateProfile', profileId: 'gaming', patch: { apps: ['witcher3.exe', 'cs2.exe'] } }]);
    // Gaming deleted: made again
    const gone = addGameToGaming({ ...base, profiles: [] }, 'cs2', []);
    expect(gone?.message).toBe("Gaming added: it's used while cs2.exe runs");
    expect(gone?.actions).toEqual([{ type: 'addProfile', profile: { id: 'gaming', name: 'Gaming', apps: ['cs2.exe'], sections: sectionsFor(base, { kind: 'edges' }), look: profileLook('intense') } }]);
    // never had presets: the defaults first
    const fresh = addGameToGaming(base, 'cs2', []);
    expect(fresh?.actions.map((a) => a.type)).toEqual(['addProfile', 'addProfile', 'addProfile', 'updateProfile']);
    expect(addGameToGaming(base, '  ', [])).toBeNull();
    expect(freeName(withGame, 'Movies')).toBe('Movies');
  });

  it('says what turned it on, or keeps it off', () => {
    const p = preset({}, { apps: ['witcher3.exe'] });
    expect(presetStatus('p', { id: 'p', name: 'Gaming', manual: false }, null)).toBe('program');
    expect(presetStatus('p', { id: 'p', name: 'Gaming', manual: true }, null)).toBe('hand');
    expect(presetStatus('p', null, { id: 'p', name: 'Gaming' })).toBe('dismissed');
    expect(presetStatus('p', { id: 'q', name: 'Q', manual: true }, null)).toBe('off');
    expect(statusText(p, 'program')).toBe('In use while witcher3.exe runs');
    expect(statusText(p, 'hand')).toBe('In use until you switch');
    expect(statusText(p, 'dismissed')).toBe('Not used until witcher3.exe quits');
    expect(statusText(p, 'off')).toBeNull();
  });

  it('switches to Everyday until no preset is in use', async () => {
    const replies = [
      { profile: { id: 'b', name: 'B', manual: false }, dismissed: { id: 'a', name: 'A' } },
      { profile: null, dismissed: { id: 'b', name: 'B' } },
    ];
    let calls = 0;
    const state = await switchToEveryday({ dismissProfile: async () => replies[calls++] });
    expect(calls).toBe(2);
    expect(state.profile).toBeNull();
  });
});

describe('the default presets', () => {
  const effects = ['Candle', 'Music: spectrum', 'Music: fullscreen pulse (MULTI COLOR)', 'Music: quatro for LED strip (MULTI COLOR)'];

  it('prefers a music effect along the strip in many colors', () => {
    expect(defaultMusicEffect(effects)).toBe('Music: quatro for LED strip (MULTI COLOR)');
    expect(defaultMusicEffect(['Music: spectrum', 'Music: fullscreen pulse (MULTI COLOR)'])).toBe('Music: fullscreen pulse (MULTI COLOR)');
    expect(defaultMusicEffect(['Candle', 'Music: spectrum'])).toBe('Music: spectrum');
    expect(defaultMusicEffect(['Candle'])).toBeNull();
  });

  it('are Gaming, Movie, Music and Cozy, with their Looks and what they play', () => {
    const list = defaultPresets(base, effects);
    const spec = { ...base, profiles: list };
    expect(list.map((p) => [p.id, p.name, p.apps])).toEqual([
      ['gaming', 'Gaming', []],
      ['movie', 'Movie', []],
      ['music', 'Music', []],
      ['cozy', 'Cozy', []],
    ]);
    expect(list.map((p) => presetSummary(spec, p))).toEqual(['Screen edges · Intense', 'Screen edges · Relaxed', 'Music: Quatro · Rich', 'Color · Relaxed']);
    expect(whatPlays(spec, list[3])).toEqual({ kind: 'color', color: COZY_COLOR });
    expect(validateLayout(spec)).toEqual([]);
    // without music effects there is no Music preset
    expect(defaultPresets(base, []).map((p) => p.name)).toEqual(['Gaming', 'Movie', 'Cozy']);
  });

  it('come once, with the first save that has strips', () => {
    expect(withDefaultPresets(base, effects).profiles).toHaveLength(4);
    const kept = { ...base, profiles: [] };
    expect(withDefaultPresets(kept, effects)).toBe(kept);
    const noStrips = { ...base, strips: [] };
    expect(withDefaultPresets(noStrips, effects)).toBe(noStrips);
  });
});

describe('presets that play one thing everywhere follow the strips', () => {
  const effects = ['Music: quatro for LED strip (MULTI COLOR)'];
  const whole: Section = { kind: 'whole' };
  // the guided setup saves the strip before it is traced (every light the screen's average), so the presets are made then
  const first: LayoutSpec = { ...base, strips: [strip('a', [whole, whole])] };
  const made = withDefaultPresets(first, effects);
  // traced: the strip has other parts now, and a second strip came
  const traced: LayoutSpec = { ...made, strips: [strip('a', [edge, edge, off, gap]), strip('b', [whole, whole, whole])] };

  it('rewrites a uniform preset for strips reshaped or added since the last save', () => {
    expect(validateLayout(traced).some((i) => /another shape/.test(i.message))).toBe(true);
    const saved = followStrips(traced, made);
    expect(validateLayout(saved).filter((i) => /Gaming|Movie|Music|Cozy/.test(i.message))).toEqual([]);
    const byId = (id: string) => saved.profiles!.find((p) => p.id === id)!;
    expect(byId('gaming').sections).toEqual(sectionsFor(saved, { kind: 'edges' }));
    expect(byId('music').sections).toEqual(sectionsFor(saved, { kind: 'effect', effect: effects[0] }));
    expect(whatPlays(saved, byId('cozy'))).toEqual({ kind: 'color', color: COZY_COLOR });
    expect(byId('gaming').look).toEqual(profileLook('intense'));
    // nothing more to do: the same layout comes back
    expect(followStrips(saved, saved)).toBe(saved);
  });

  it('leaves presets customized in Layout and presets without lists alone, and keeps lists that fit', () => {
    const custom = preset({ a: [{ kind: 'effect', effect: 'Candle' }, null] }, { id: 'custom' });
    const empty = preset({}, { id: 'empty' });
    const fits = preset({ a: [whole, whole], b: [whole, whole, whole] }, { id: 'fits' });
    const spec: LayoutSpec = { ...first, strips: [strip('a', [whole, whole]), strip('b', [whole, whole, whole])], profiles: [custom, empty, fits] };
    expect(followStrips(spec, first)).toBe(spec);
    // a strip added later: the uniform one gets it, the customized one doesn't
    const added = { ...spec, strips: [...spec.strips, strip('c', [undefined])] };
    const next = followStrips(added, spec);
    expect(next.profiles![0]).toBe(custom);
    expect(next.profiles![1]).toBe(empty);
    expect(next.profiles![2].sections).toEqual({ ...fits.sections, c: [{ kind: 'whole' }] });
    expect(next.profiles![2].sections.a).toBe(fits.sections.a);
    // a list made for neither shape of its strip: customized, so it stays (Layout warns)
    const stale = { ...spec, profiles: [preset({ a: [null, null, null, null, null] })] };
    expect(followStrips(stale, spec)).toBe(stale);
  });

  it('a pick made while a strip is drawn still plays on every strip once it is drawn further and saved', () => {
    const color: MainPick = { kind: 'color', color: [255, 0, 0] };
    const savedSpec: LayoutSpec = { ...base, strips: [strip('a', [undefined, undefined])], profiles: [preset({ a: [null, null] })] };
    /** Draws on Layout (`draw`, not saved), picks for the preset, draws again (`again`) and saves. */
    const drawPickDraw = (draw: (st: EditorState) => EditorState, again: (st: EditorState) => EditorState) => {
      let st = draw(initialEditorState(savedSpec, savedSpec));
      // the pick: mirrored into the edited layout (sized for the strips as drawn so far), saved to the layout saved last
      st = editorReducer(st, { type: 'mirror', id: 'pick', actions: writePick(st.spec, st.spec.profiles![0], color).actions });
      const picked = followStrips(writePick(st.saved!, st.saved!.profiles![0], color).spec, st.saved);
      st = editorReducer(editorReducer(st, { type: 'markSaved', spec: picked }), { type: 'mirrorDone', id: 'pick', saved: true });
      st = again(st);
      return followStrips(st.spec, st.saved);
    };
    const points = (n: number) => Array.from({ length: n }, (_, i) => at(i * 0.2));
    const reshape = (id: string, n: number) => (st: EditorState) => editorReducer(st, { type: 'updateStrip', stripId: id, patch: { path: points(n) } });

    // a new strip: two parts drawn, then a third
    const added = drawPickDraw((st) => editorReducer(st, { type: 'addStrip', path: points(3) }), reshape('strip-1', 4));
    expect(added.strips.map((s) => s.id)).toEqual(['a', 'strip-1']);
    expect(whatPlays(added, added.profiles![0])).toEqual(color);
    expect(added.profiles![0].sections['strip-1']).toHaveLength(3);
    // the only strip reshaped: three parts, then four
    const reshaped = drawPickDraw(reshape('a', 4), reshape('a', 5));
    expect(whatPlays(reshaped, reshaped.profiles![0])).toEqual(color);
    expect(reshaped.profiles![0].sections.a).toHaveLength(4);
    expect(validateLayout(reshaped).filter((i) => /another shape/.test(i.message))).toEqual([]);
  });

  it('follow in the edited layout too when it is saved, so it isn’t left with changes', () => {
    const editor = editorReducer(initialEditorState(traced, made), { type: 'updateRoom', patch: {} });
    const after = editorReducer(editor, { type: 'markSaved', spec: followStrips(editor.spec, editor.saved) });
    expect(after.spec.profiles).toEqual(followStrips(traced, made).profiles);
    expect(isDirty(after)).toBe(false);
  });
});
