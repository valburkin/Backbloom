import { describe, expect, it } from 'vitest';
import { MIN_SPRING_MS, springFor, withTransition } from '../smoothing';

describe('smoothing spring', () => {
  it('matches the tested spring at 350 ms and gets softer for longer times', () => {
    expect(springFor(350)).toEqual({ stiffness: 150, damping: 26 });
    expect(springFor(25)).toEqual(springFor(MIN_SPRING_MS));
    expect(springFor(MIN_SPRING_MS).stiffness).toBeLessThanOrEqual(1000);
    expect(springFor(1000).stiffness).toBeLessThan(springFor(550).stiffness);
  });

  it('sets the time with its spring, and keeps a hand-set spring when the time stays', () => {
    const custom = { type: 'HybridRgbInterpolator', time_ms: 350, stiffness: 400, damping: 30 };
    expect(withTransition(custom, 350)).toBe(custom);
    expect(withTransition(custom, 250)).toEqual({ ...custom, time_ms: 250, ...springFor(250) });
  });
});
