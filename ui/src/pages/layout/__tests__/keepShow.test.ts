import { describe, expect, it } from 'vitest';
import type { PathPoint } from '../../../layout';
import { keepPlaying, partChanges, showRole, undoChanges } from '../keepShow';

const at = (x: number, next?: PathPoint['next']): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });

describe('keeping the Show on the parts a choice is not about', () => {
  it('reads what plays on every light as a part’s own role', () => {
    expect(showRole({ priority: 50, componentId: 'EFFECT', origin: 'ui', owner: 'Music: spectrum', active: true, visible: true })).toEqual({
      kind: 'effect',
      effect: 'Music: spectrum',
    });
    expect(showRole({ priority: 50, componentId: 'COLOR', origin: 'ui', active: true, visible: true, value: { RGB: [1, 2, 3] } })).toEqual({
      kind: 'color',
      color: [1, 2, 3],
    });
    expect(showRole({ priority: 240, componentId: 'SYSTEMGRABBER', origin: 'System', owner: 'DirectX11', active: true, visible: true })).toBeNull();
    expect(showRole(null)).toBeNull();
  });

  it('gives the music to the parts that follow the Show, leaving the chosen part, own roles, Off and gaps alone', () => {
    const music = { kind: 'effect' as const, effect: 'Music: spectrum' };
    const path = [at(0), at(1, { kind: 'whole' }), at(2, { kind: 'none' }), at(3, { kind: 'gap' }), at(4, { kind: 'color', color: [9, 9, 9] }), at(5, { kind: 'edge', from: 0, to: 1 }), at(6)];
    const { path: kept, parts } = keepPlaying(path, music, (i) => i === 1);
    expect(kept.map((p) => p.next)).toEqual([music, { kind: 'whole' }, { kind: 'none' }, { kind: 'gap' }, { kind: 'color', color: [9, 9, 9] }, music, undefined]);
    expect(parts).toEqual([0, 5]);
    expect(keepPlaying([at(0, { kind: 'none' }), at(1)], music).parts).toEqual([]);
  });

  it('undoes exactly the parts it changed, leaving the ones changed since', () => {
    const music = { kind: 'effect' as const, effect: 'Music: spectrum' };
    const strip = (id: string, path: PathPoint[]) => ({ id, name: id, controllerId: 'c', output: 0, ledCount: 10, path });
    const before = [strip('tv', [at(0), at(1, { kind: 'edge', from: 0, to: 1 }), at(2)]), strip('ceiling', [at(0), at(1)])];
    const next = new Map([
      ['ceiling', [at(0, { kind: 'whole' }), at(1)]],
      ['tv', keepPlaying(before[0].path, music).path],
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
