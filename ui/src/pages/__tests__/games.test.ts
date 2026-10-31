import { describe, expect, it } from 'vitest';
import { addGame, removeGame } from '../GamesPage';
import type { LayoutSpec, Profile } from '../../layout';
import specJson from '../../../../backend/tests/fixtures/layouts/ui-multi-controller.spec.json';

const spec = specJson as unknown as LayoutSpec;

describe('games', () => {
  it('adds a running game as a profile marked as a game, named by its window', () => {
    expect(addGame(spec, 'C:/Games/Elden.exe', 'ELDEN RING™')).toEqual({
      action: { type: 'addProfile', profile: { id: expect.any(String), name: 'ELDEN RING™', apps: ['elden.exe'], game: true, sections: {} } },
      message: 'ELDEN RING™ added',
    });
    expect(addGame(spec, 'cs2', '')?.action).toMatchObject({ profile: { name: 'cs2', apps: ['cs2.exe'] } });
    expect(addGame(spec, '  ', '')).toBeNull();
  });

  it('marks the profile that already lists the program (any case), once, by its name', () => {
    expect(addGame(spec, 'Witcher3.EXE', 'The Witcher 3')).toEqual({
      action: { type: 'updateProfile', profileId: 'witcher', patch: { game: true } },
      message: 'Witcher 3 added',
    });
    const marked = { ...spec, profiles: spec.profiles!.map((p) => (p.id === 'witcher' ? { ...p, game: true } : p)) };
    expect(addGame(marked, 'witcher3.exe', '')).toEqual({ action: null, message: 'Witcher 3 is already in your games' });
  });

  it('removes a game, but keeps its profile when it changes strips', () => {
    const plain: Profile = { id: 'p', name: 'P', apps: ['p.exe'], game: true, sections: {} };
    expect(removeGame(plain)).toEqual({ action: { type: 'removeProfile', profileId: 'p' }, message: 'P removed' });
    expect(removeGame({ ...plain, sections: { s: [null, null] } }).action).toEqual({ type: 'removeProfile', profileId: 'p' });
    expect(removeGame({ ...plain, sections: { s: [null, { kind: 'off' } as never] } })).toEqual({
      action: { type: 'updateProfile', profileId: 'p', patch: { game: false } },
      message: 'P is no longer a game. What its strips show stays on Layout.',
    });
  });
});
