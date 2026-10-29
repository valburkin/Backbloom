import { describe, expect, it } from 'vitest';
import { hrefFor, parseHash } from '../router';

describe('router', () => {
  it('reads routes, tabs and a control to show', () => {
    expect(parseHash('#/capture')).toEqual({ route: 'capture', sub: null, focus: null });
    expect(parseHash('#/settings/support')).toEqual({ route: 'settings', sub: 'support', focus: null });
    expect(parseHash('#/capture?focus=crop')).toEqual({ route: 'capture', sub: null, focus: 'crop' });
    expect(parseHash('#/settings/support?focus=log&x=1')).toEqual({ route: 'settings', sub: 'support', focus: 'log' });
    expect(parseHash('#/nowhere/else?focus=crop')).toEqual({ route: 'home', sub: null, focus: 'crop' });
    expect(parseHash('')).toEqual({ route: 'home', sub: null, focus: null });
  });

  it('writes links that read back the same', () => {
    expect(hrefFor('layout')).toBe('#/layout');
    expect(hrefFor('settings', 'support')).toBe('#/settings/support');
    expect(hrefFor('colors', null, 'white-balance')).toBe('#/colors?focus=white-balance');
    expect(parseHash(hrefFor('devices', null, 'connection'))).toEqual({ route: 'devices', sub: null, focus: 'connection' });
  });
});
