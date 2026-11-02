import { describe, expect, it } from 'vitest';
import { gamesStayOpen, NAV, pageShown, sectionOf } from '../nav';

describe('sidebar', () => {
  it('offers Games only in Game mode, and Effects & Music only in Ambient, marking no section on the other', () => {
    expect(sectionOf(NAV.game, 'games')?.label).toBe('Games');
    expect(sectionOf(NAV.ambient, 'games')).toBeUndefined();
    expect(sectionOf(NAV.ambient, 'effects')?.label).toBe('Effects & Music');
    expect(sectionOf(NAV.game, 'effects')).toBeUndefined();
  });

  it('leads a page the mode does not offer Home, once the mode is known', () => {
    expect(pageShown('games', 'game', false)).toBe(true);
    expect(pageShown('games', 'ambient', false)).toBe(false);
    expect(pageShown('effects', 'game', false)).toBe(false);
    expect(pageShown('games', undefined, false)).toBe(true);
    expect(pageShown('effects', undefined, false)).toBe(true);
    expect(pageShown('layout', 'ambient', false)).toBe(true);
  });

  it('keeps Games open when Game mode turns off under it', () => {
    const opened = gamesStayOpen('games', 'game', false);
    expect(opened).toBe(true);
    const afterQuit = gamesStayOpen('games', 'ambient', opened);
    expect(afterQuit).toBe(true);
    expect(pageShown('games', 'ambient', afterQuit)).toBe(true);
    expect(gamesStayOpen('home', 'ambient', afterQuit)).toBe(false);
    expect(gamesStayOpen('games', undefined, false)).toBe(false);
    expect(gamesStayOpen('games', 'ambient', false)).toBe(false);
  });
});
