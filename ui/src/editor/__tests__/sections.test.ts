import { describe, expect, it } from 'vitest';
import { defaultRoom, type PathPoint } from '../../layout';
import { insertPoint, removePoint, setSection } from '../sections';

const room = defaultRoom();
const front = (x: number, y: number, next?: PathPoint['next']): PathPoint => ({ surface: 'front', x, y, ...(next ? { next } : {}) });

describe('sections', () => {
  it('splits an outline stretch where a bend point is added, and joins it again when it is removed', () => {
    const path = [front(1, 1, { kind: 'edge', from: 0, to: 1 }), front(3, 1)];
    const split = insertPoint(room, path, 0, front(1.5, 1));
    expect(split.map((p) => p.next)).toEqual([{ kind: 'edge', from: 0, to: 0.25 }, { kind: 'edge', from: 0.25, to: 1 }, undefined]);
    expect(removePoint(split, 1)).toEqual(path);
    // Other kinds carry over to both halves; removing the last point drops the section that ended there.
    const gap = insertPoint(room, [front(1, 1, { kind: 'gap' }), front(3, 1)], 0, front(2, 1));
    expect(gap.map((p) => p.next?.kind)).toEqual(['gap', 'gap', undefined]);
    expect(removePoint(gap, 2).map((p) => p.next?.kind)).toEqual(['gap', undefined]);
  });

  it('sets a section and back to automatic', () => {
    const path = setSection([front(0, 1), front(1, 1), front(2, 1), front(3, 1)], 1, { kind: 'gap' });
    expect(path[1].next).toEqual({ kind: 'gap' });
    expect(setSection(path, 1, undefined)[1].next).toBeUndefined();
  });
});
