import { describe, expect, it } from 'vitest';
import type { BackendConfig, PriorityInfo } from '../../api/types';
import { defaultRoom, type LayoutSpec, type PathPoint, type Profile, type Section, type Strip } from '../../layout';
import { editorReducer, initialEditorState, type EditorAction } from '../../editor/state';
import { applyLook, profileLook } from '../../settings/intensity';
import { sectionsFor } from '../presets';
import { lookLabel, overlayOf, overlayText, pickNeeds, presetInUse, setupById, setupLook, setupLookValues, setupName, setupPlays, undoPick, withLookValues, withPresetPatch, writePick } from '../setup';

const at = (x: number, next?: Section): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });
const strip = (id: string, sections: Array<Section | undefined>): Strip => ({
  id,
  name: id,
  controllerId: 'c',
  output: 0,
  ledCount: 30,
  path: [...sections.map((s, i) => at(i * 0.3, s)), at(sections.length * 0.3)],
});
const off: Section = { kind: 'none' };
const gap: Section = { kind: 'gap' };
const candle: Section = { kind: 'effect', effect: 'Candle' };
const base: LayoutSpec = {
  version: 1,
  room: defaultRoom(),
  controllers: [{ id: 'c', name: 'C', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }] }],
  strips: [strip('a', [undefined, off, gap]), strip('b', [candle, undefined])],
};
const gaming: Profile = { id: 'gaming', name: 'Gaming', apps: [], sections: sectionsFor(base, { kind: 'edges' }), look: profileLook('intense') };
const spec: LayoutSpec = { ...base, profiles: [gaming] };
/** The layout after the edits, as the editor makes them. */
const applied = (s: LayoutSpec, actions: EditorAction[]) => actions.reduce((st, a) => editorReducer(st, a), initialEditorState(s)).spec;

describe('the setup in use', () => {
  it('is the preset the backend uses, else Everyday', () => {
    expect(presetInUse(spec, { id: 'gaming' })).toBe(gaming);
    expect(presetInUse(spec, null)).toBeUndefined();
    // a preset the layout doesn't have (yet): Everyday's strips are what the UI knows
    expect(presetInUse(spec, { id: 'gone' })).toBeUndefined();
    expect(setupName(gaming)).toBe('Gaming');
    expect(setupName(undefined)).toBe('Everyday');
    expect(setupPlays(spec, undefined)).toEqual({ kind: 'customized' });
    expect(setupPlays(spec, gaming)).toEqual({ kind: 'edges' });
  });
});

describe('a pick saved to the setup in use', () => {
  it('writes a preset’s sections, never Everyday’s, and undoes to what it had', () => {
    const write = writePick(spec, gaming, { kind: 'effect', effect: 'Rainbow' });
    expect(write.replaced).toBe(false);
    expect(write.spec.strips).toBe(spec.strips);
    expect(setupPlays(write.spec, write.spec.profiles![0])).toEqual({ kind: 'effect', effect: 'Rainbow' });
    expect(applied(spec, write.actions)).toEqual(write.spec);
    // the same pick again changes nothing
    expect(writePick(write.spec, write.spec.profiles![0], { kind: 'effect', effect: 'Rainbow' }).actions).toEqual([]);
    const back = undoPick(write.spec, write.undo);
    expect(back.spec.profiles![0].sections).toEqual(gaming.sections);
    expect(applied(write.spec, back.actions)).toEqual(back.spec);
    // the preset was removed meanwhile: nothing to undo
    expect(undoPick({ ...write.spec, profiles: [] }, write.undo).actions).toEqual([]);
  });

  it('writes Everyday’s strips, replacing their customizations (Off parts and gaps stay), with Undo', () => {
    const write = writePick(spec, undefined, { kind: 'color', color: [255, 0, 0] });
    expect(write.replaced).toBe(true);
    expect(write.spec.profiles).toBe(spec.profiles);
    const red: Section = { kind: 'color', color: [255, 0, 0] };
    expect(write.spec.strips.map((s) => s.path.slice(0, -1).map((p) => p.next))).toEqual([
      [red, off, gap],
      [red, red],
    ]);
    expect(setupPlays(write.spec, undefined)).toEqual({ kind: 'color', color: [255, 0, 0] });
    expect(applied(spec, write.actions)).toEqual(write.spec);
    const back = undoPick(write.spec, write.undo);
    expect(back.spec.strips.map((s) => s.path.map((p) => p.next))).toEqual(spec.strips.map((s) => s.path.map((p) => p.next)));
  });
});

describe('the Look of the setup in use', () => {
  const config: BackendConfig = applyLook({ smoothing: {}, color: { channelAdjustment: [{}] } }, 'rich');

  it('is a preset’s own, else Everyday’s', () => {
    expect(setupLook(config, undefined)).toBe('rich');
    expect(setupLook(config, gaming)).toBe('intense');
    const same = { ...gaming, look: undefined };
    expect(setupLook(config, same)).toBe('rich');
    expect(setupLookValues(config, same)).toEqual(profileLook('rich'));
    expect(setupLookValues(null, same)).toBeNull();
    expect(lookLabel('intense')).toBe('Intense');
    expect(lookLabel('custom')).toBe('Custom');
    expect(lookLabel(null)).toBeNull();
  });

  it('changes one value of a preset’s Look, starting from Everyday’s when it has none', () => {
    expect(withLookValues({ ...gaming, look: undefined }, profileLook('rich'), { saturationGain: 2 })).toEqual({ ...profileLook('rich'), saturationGain: 2 });
    expect(withLookValues(gaming, profileLook('rich'), { time_ms: 400 })).toEqual({ ...profileLook('intense'), time_ms: 400 });
    expect(withLookValues({ ...gaming, look: undefined }, null, { time_ms: 400 })).toBeNull();
  });
});

describe('something another app shows over the setup', () => {
  const p = (componentId: string, extra: Partial<PriorityInfo> = {}): PriorityInfo => ({ priority: 50, componentId, origin: 'Home Assistant@192.168.1.5', active: true, visible: true, ...extra });

  it('is a visible color, effect or picture that isn’t Backbloom’s own', () => {
    expect(overlayOf([p('SYSTEMGRABBER', { origin: 'System' })])).toBeNull();
    expect(overlayOf([])).toBeNull();
    expect(overlayOf(undefined)).toBeNull();
    // the startup and idle effects are Backbloom's
    expect(overlayOf([p('EFFECT', { origin: 'System', owner: 'Rainbow swirl' })])).toBeNull();
    const effect = overlayOf([p('EFFECT', { owner: 'Candle' }), p('SYSTEMGRABBER', { visible: false })]);
    expect(effect).toEqual({ what: 'An effect', app: 'Home Assistant', own: false, priority: 50 });
    expect(overlayText(effect!, 'Gaming')).toBe('An effect from Home Assistant shows over Gaming');
    expect(overlayOf([p('COLOR', { origin: '' })])).toEqual({ what: 'A color', app: null, own: false, priority: 50 });
    // a color tried on the Effects page: not while its picker is dragged there
    const tried = [p('COLOR', { origin: 'Backbloom UI@127.0.0.1', priority: 1 })];
    expect(overlayText(overlayOf(tried)!, 'Gaming')).toBe('A color you tried shows over Gaming');
    expect(overlayOf(tried, true)).toBeNull();
    expect(overlayText(overlayOf([p('COLOR', { origin: '' })])!, 'Everyday')).toBe('A color from another app shows over Everyday');
    expect(overlayOf([p('EFFECT', { owner: 'Music: spectrum' })])?.what).toBe('A music effect');
  });
});

describe('a pick on Effects or Presets', () => {
  it('turns screen sync on for the screen picks when it is off, and sound input on for music', () => {
    const off = [{ name: 'SYSTEMGRABBER', enabled: false }];
    const on = [{ name: 'SYSTEMGRABBER', enabled: true }];
    expect(pickNeeds({ kind: 'edges' }, off)).toEqual({ screen: true, sound: false });
    expect(pickNeeds({ kind: 'average' }, undefined)).toEqual({ screen: true, sound: false });
    expect(pickNeeds({ kind: 'average' }, on)).toEqual({ screen: false, sound: false });
    expect(pickNeeds({ kind: 'effect', effect: 'Music: spectrum' }, off)).toEqual({ screen: false, sound: true });
    expect(pickNeeds({ kind: 'effect', effect: 'Candle' }, off)).toEqual({ screen: false, sound: false });
    expect(pickNeeds({ kind: 'color', color: [1, 2, 3] }, off)).toEqual({ screen: false, sound: false });
  });

  it('goes to the setup named, never to Everyday instead of a preset the layout doesn’t have', () => {
    expect(setupById(spec, 'gaming', true)).toBe(gaming);
    expect(setupById(spec, null, true)).toBeUndefined();
    expect(() => setupById(spec, 'gone', true)).toThrow(/isn’t saved yet/);
    expect(() => setupById(spec, 'gone', false)).toThrow(/removed on Layout/);
    const changed = withPresetPatch(spec, 'gaming', { look: profileLook('rich') });
    expect(changed.profiles?.[0]).toEqual({ ...gaming, look: profileLook('rich') });
    expect(() => withPresetPatch(base, 'gaming', { name: 'X' })).toThrow();
  });
});
