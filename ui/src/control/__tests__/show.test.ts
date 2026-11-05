import { describe, expect, it } from 'vitest';
import { defaultRoom, type LayoutSpec, type PathPoint, type Section, type Strip } from '../../layout';
import { whatPlays } from '../presets';
import { canShowAverage, isPicked, pickPaths, withPaths } from '../show';

const at = (x: number, next?: Section): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });
const strip = (id: string, sections: Array<Section | undefined>): Strip => ({
  id,
  name: id,
  controllerId: 'c',
  output: 0,
  ledCount: 30,
  path: [...sections.map((s, i) => at(i, s)), at(sections.length)],
});
const spec = (...strips: Strip[]): LayoutSpec => ({ version: 1, room: defaultRoom(), controllers: [], strips });
const edge: Section = { kind: 'edge', from: 0.2, to: 0.8 };
const candle: Section = { kind: 'effect', effect: 'Candle' };
const off: Section = { kind: 'none' };
const gap: Section = { kind: 'gap' };
const whole: Section = { kind: 'whole' };
const sections = (s: LayoutSpec) => s.strips.map((x) => x.path.slice(0, -1).map((p) => p.next));

describe('what Everyday plays', () => {
  it('reads Screen edges, Screen average, an effect, a color or customized, ignoring off parts and gaps', () => {
    expect(whatPlays(spec())).toEqual({ kind: 'edges' });
    expect(whatPlays(spec(strip('a', [undefined, edge, off]), strip('b', [gap, undefined])))).toEqual({ kind: 'edges' });
    expect(whatPlays(spec(strip('a', [whole, off]), strip('b', [{ kind: 'area', area: { hmin: 0, hmax: 1, vmin: 0, vmax: 1 } }])))).toEqual({ kind: 'average' });
    expect(whatPlays(spec(strip('a', [candle, off]), strip('b', [candle])))).toEqual({ kind: 'effect', effect: 'Candle' });
    expect(whatPlays(spec(strip('a', [whole]), strip('b', [undefined])))).toEqual({ kind: 'customized' });
    expect(whatPlays(spec(strip('a', [undefined, candle])))).toEqual({ kind: 'customized' });
    expect(whatPlays(spec(strip('a', [off, off])))).toEqual({ kind: 'edges' });
  });

  it('offers Screen average only with strips placed', () => {
    expect(canShowAverage(spec())).toBe(false);
    expect(canShowAverage(spec(strip('a', [undefined])))).toBe(true);
  });
});

describe('a pick written into Everyday’s strips', () => {
  const custom = spec(strip('a', [candle, off, edge, gap, { kind: 'color', color: [1, 2, 3] }]), strip('b', [undefined, undefined]));

  it('plays an effect on every part that isn’t off or a gap, as one zone', () => {
    const paths = pickPaths(custom, { kind: 'effect', effect: 'Rainbow swirl' });
    const next = withPaths(custom, paths);
    const rainbow: Section = { kind: 'effect', effect: 'Rainbow swirl' };
    expect(sections(next)).toEqual([[rainbow, off, rainbow, gap, rainbow], [rainbow, rainbow]]);
    expect(whatPlays(next)).toEqual({ kind: 'effect', effect: 'Rainbow swirl' });
    // nothing left to change
    expect(pickPaths(next, { kind: 'effect', effect: 'Rainbow swirl' }).size).toBe(0);
    expect(whatPlays(withPaths(next, pickPaths(next, { kind: 'color', color: [9, 9, 9] })))).toEqual({ kind: 'color', color: [9, 9, 9] });
  });

  it('turns them into the screen’s edges, keeping a moved mapping, or its average', () => {
    const edges = withPaths(custom, pickPaths(custom, { kind: 'edges' }));
    expect(sections(edges)).toEqual([[undefined, off, edge, gap, undefined], [undefined, undefined]]);
    expect(edges.strips[1]).toBe(custom.strips[1]);
    expect(pickPaths(edges, { kind: 'edges' }).size).toBe(0);
    const average = withPaths(custom, pickPaths(custom, { kind: 'average' }));
    expect(sections(average)).toEqual([[whole, off, whole, gap, whole], [whole, whole]]);
    expect(whatPlays(average)).toEqual({ kind: 'average' });
  });

  it('says whether the setup plays a pick (any color counts as the Color choice)', () => {
    expect(isPicked({ kind: 'effect', effect: 'Candle' }, { kind: 'effect', effect: 'Candle' })).toBe(true);
    expect(isPicked({ kind: 'effect', effect: 'Candle' }, { kind: 'effect', effect: 'Rainbow' })).toBe(false);
    expect(isPicked({ kind: 'color', color: [1, 2, 3] }, { kind: 'color', color: [0, 0, 0] })).toBe(true);
    expect(isPicked({ kind: 'customized' }, { kind: 'edges' })).toBe(false);
  });
});
