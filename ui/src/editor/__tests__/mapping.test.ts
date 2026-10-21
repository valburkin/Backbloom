import { describe, expect, it } from 'vitest';
import { aroundScreen, defaultRoom, type PathPoint, type Strip } from '../../layout';
import { hasAdjustedMapping, ledColor, mappingJoints, moveJoint, roleLabel, setStripShows, stripShows } from '../mapping';

const room = defaultRoom();
const strip = (path: PathPoint[], ledCount = 40): Strip => ({ id: 's', name: 's', controllerId: 'c', output: 0, ledCount, path });

describe('mapping', () => {
  it('names roles briefly', () => {
    expect(roleLabel({ kind: 'effect', effect: 'Music: fullscreen pulse (MULTI COLOR)' })).toBe('♪ Fullscreen pulse');
    expect(roleLabel({ kind: 'edge', from: 0, to: 1 })).toBeNull();
  });

  it('colors LEDs from red to violet', () => {
    expect(ledColor(0, 10)).toBe('hsl(0 95% 60%)');
    expect(ledColor(9, 10)).toBe('hsl(280 95% 60%)');
  });

  it('puts one dot where parts meet and at the open ends', () => {
    // Around the screen from the bottom-right corner, clockwise: four parts meeting at the corners.
    const ring = strip(aroundScreen(room, { start: 'bottom-right', clockwise: true }));
    const joints = mappingJoints(room, ring);
    expect(joints.map((j) => [j.pos, j.ends.length])).toEqual([
      [2, 1],
      [3, 2],
      [4, 2], // the top-left corner, wrapping round to 0
      [1, 2],
      [2, 1],
    ]);
  });

  it('moves every end of a dot together, and keeps the gap and other parts out of it', () => {
    const ring = aroundScreen(room, { start: 'bottom-right', clockwise: true });
    const [, corner] = mappingJoints(room, strip(ring));
    const moved = moveJoint(room, ring, corner.ends, 3.25);
    expect(moved[0].next).toEqual({ kind: 'edge', from: 2, to: 3.25 });
    expect(moved[1].next).toEqual({ kind: 'edge', from: 3.25, to: 4 });
    expect(hasAdjustedMapping(strip(moved))).toBe(true);
    const withGap = setStripShows(moved.map((p, i) => (i === 2 ? { ...p, next: { kind: 'gap' as const } } : p)), undefined);
    expect(withGap.map((p) => p.next?.kind)).toEqual([undefined, undefined, 'gap', undefined, undefined]);
  });

  it('sums up what a strip shows and sets it for every part with LEDs', () => {
    const ring = aroundScreen(room, { start: 'bottom-right', clockwise: true });
    expect(stripShows(room, strip(ring)).shows).toBe('screen');
    const whole = setStripShows(ring, { kind: 'whole' });
    expect(stripShows(room, strip(whole)).shows).toBe('whole');
    expect(stripShows(room, strip(setStripShows(whole, { kind: 'none' }))).shows).toBe('none');
    expect(stripShows(room, strip(whole.map((p, i) => (i === 0 ? { ...p, next: undefined } : p)))).shows).toBe('custom');
    const candle = { kind: 'effect' as const, effect: 'Candle' };
    expect(stripShows(room, strip(setStripShows(ring, candle)))).toEqual({ shows: 'effect', section: candle });
    const mixed = setStripShows(ring, candle).map((p, i) => (i === 1 ? { ...p, next: { kind: 'effect' as const, effect: 'Breath' } } : p));
    expect(stripShows(room, strip(mixed)).shows).toBe('custom');
  });
});
