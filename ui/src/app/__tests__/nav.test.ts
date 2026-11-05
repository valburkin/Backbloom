import { describe, expect, it } from 'vitest';
import { NAV, sectionOf, SHARED_NAV } from '../nav';
import { parseHash } from '../router';

describe('sidebar', () => {
  it('has one set of sections: Home, Effects, Presets, Colors, then Setup and Settings', () => {
    expect(NAV.map((n) => n.label)).toEqual(['Home', 'Effects', 'Presets', 'Colors', 'Setup', 'Settings']);
    expect(SHARED_NAV[0].label).toBe('Setup');
    expect(sectionOf(NAV, 'presets')?.label).toBe('Presets');
    expect(sectionOf(NAV, 'capture')?.label).toBe('Setup');
  });

  it('every route has its section', () => {
    for (const r of ['home', 'effects', 'presets', 'colors', 'layout', 'devices', 'capture', 'calibration', 'settings'] as const) {
      expect(sectionOf(NAV, r)).toBeDefined();
      expect(parseHash(`#/${r}`).route).toBe(r);
    }
    // the old Games page leads Home
    expect(parseHash('#/games').route).toBe('home');
  });
});
