import { describe, expect, it } from 'vitest';
import { activeProfile, applyProfile, stripInProfile, withStripSections } from './profiles';
import { defaultRoom } from './room';
import { validateLayout } from './validate';
import type { LayoutSpec, Profile, Strip } from './types';

const strip: Strip = {
  id: 's',
  name: 'Ceiling',
  controllerId: 'c',
  output: 0,
  ledCount: 10,
  path: [
    { surface: 'ceiling', x: 0.5, y: 3 },
    { surface: 'ceiling', x: 1.5, y: 3, next: { kind: 'gap' } },
    { surface: 'ceiling', x: 2.5, y: 3, next: { kind: 'whole' } },
    { surface: 'ceiling', x: 3.5, y: 3 },
  ],
};
const profile = (sections: Profile['sections'], apps = ['witcher3.exe']): Profile => ({ id: 'p', name: 'Game', apps, sections });
const spec = (profiles: Profile[]): LayoutSpec => ({
  version: 1,
  room: defaultRoom(),
  controllers: [{ id: 'c', name: 'C', kind: 'wled', host: 'c', outputs: [{ index: 0, start: 0, length: 10 }] }],
  strips: [strip],
  profiles,
});

describe('profiles', () => {
  it('replace what sections do, keep gaps, and null means automatic', () => {
    const s = stripInProfile(strip, profile({ s: [{ kind: 'effect', effect: 'Candle' }, { kind: 'color', color: [1, 2, 3] }, null] }));
    expect(s.path.map((p) => p.next)).toEqual([{ kind: 'effect', effect: 'Candle' }, { kind: 'gap' }, undefined, undefined]);
  });

  it('leave a strip alone when the profile was made for another shape of it, or for another strip', () => {
    expect(stripInProfile(strip, profile({ s: [null] }))).toBe(strip);
    expect(stripInProfile(strip, profile({ other: [null, null, null] }))).toBe(strip);
    expect(applyProfile(spec([profile({ s: [null, null, null] })]), null).strips[0]).toBe(strip);
    expect(applyProfile(spec([profile({ s: [null, null, null] })]), 'p').strips[0].path[2].next).toBeUndefined();
  });

  it('pick the first profile whose program runs, ignoring case', () => {
    const a = { ...profile({}, ['Witcher3.exe']), id: 'a' };
    const b = { ...profile({}, ['witcher3.exe', 'vlc.exe']), id: 'b' };
    expect(activeProfile(spec([a, b]), ['WITCHER3.EXE', 'vlc.exe'])?.id).toBe('a');
    expect(activeProfile(spec([a, b]), ['vlc.exe'])?.id).toBe('b');
    expect(activeProfile(spec([a, b]), ['notepad.exe'])).toBeUndefined();
  });

  it('take a strip edited in a profile, leaving its gaps to the strip', () => {
    const edited = stripInProfile(strip, profile({}));
    const path = edited.path.map((p, i) => (i === 0 ? { ...p, next: { kind: 'none' as const } } : p));
    const p = withStripSections(profile({}), 's', path);
    expect(p.sections.s).toEqual([{ kind: 'none' }, null, { kind: 'whole' }]);
    expect(validateLayout(spec([p]))).toEqual([]);
    expect(stripInProfile(strip, p).path.map((q) => q.next)).toEqual([{ kind: 'none' }, { kind: 'gap' }, { kind: 'whole' }, undefined]);
  });

  it('are validated', () => {
    const msgs = (p: unknown) => validateLayout(spec([p as Profile])).map((i) => `${i.severity}: ${i.message}`);
    expect(msgs(profile({ s: [{ kind: 'gap' }, null, null] }))).toEqual([expect.stringMatching(/^error: .*can’t turn a part into a gap/)]);
    expect(msgs({ ...profile({}), name: '' })[0]).toMatch(/needs a name/);
    // a preset may have no programs: it is turned on by hand
    expect(msgs(profile({}, []))).toEqual([]);
    expect(msgs(profile({ s: [null] }))[0]).toMatch(/^warning: .*another shape/);
    expect(validateLayout(spec([profile({ s: [null, null, { kind: 'color', color: [0, 0, 0] }] }), { ...profile({}), id: 'p' }])).map((i) => i.message)).toContain('Two presets share the id “p”.');
  });
});
