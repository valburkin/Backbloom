import { describe, expect, it } from 'vitest';
import type { PathPoint } from '../../../layout';
import { partChanges, undoChanges } from '../keepShow';

const at = (x: number, next?: PathPoint['next']): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });

describe('the parts a change gave other roles', () => {
  it('undoes exactly the parts it changed, leaving the ones changed since', () => {
    const music = { kind: 'effect' as const, effect: 'Music: spectrum' };
    const strip = (id: string, path: PathPoint[]) => ({ id, name: id, controllerId: 'c', output: 0, ledCount: 10, path });
    const before = [strip('tv', [at(0), at(1, { kind: 'edge', from: 0, to: 1 }), at(2)]), strip('ceiling', [at(0), at(1)])];
    const next = new Map([
      ['ceiling', [at(0, { kind: 'whole' }), at(1)]],
      ['tv', [at(0, music), at(1, music), at(2)]],
    ]);
    const changes = partChanges(before, next);
    expect(changes.map((c) => `${c.stripId}:${c.part}`)).toEqual(['ceiling:0', 'tv:0', 'tv:1']);

    // later the user gave the TV's first part a color: Undo leaves it, and puts back the rest (the dragged edge too)
    const now = { strips: [strip('tv', [at(0, { kind: 'color', color: [1, 1, 1] }), at(1, music), at(2)]), strip('ceiling', [at(0, { kind: 'whole' }), at(1)])] };
    const undone = undoChanges(now, changes);
    expect(undone.get('ceiling')!.map((p) => p.next)).toEqual([undefined, undefined]);
    expect(undone.get('tv')!.map((p) => p.next)).toEqual([{ kind: 'color', color: [1, 1, 1] }, { kind: 'edge', from: 0, to: 1 }, undefined]);
  });
});
