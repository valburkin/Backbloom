import { describe, expect, it } from 'vitest';
import { buttonClass, rangeFill } from '../ui';

describe('rangeFill', () => {
  it('fills the track up to the value', () => {
    expect(rangeFill(25, 0, 100)).toEqual({ '--fill': '25%' });
    expect(rangeFill(3, 1, 5)).toEqual({ '--fill': '50%' });
  });

  it('clamps values outside the range and handles an empty range', () => {
    expect(rangeFill(1.5, 0, 1)).toEqual({ '--fill': '100%' });
    expect(rangeFill(-1, 0, 1)).toEqual({ '--fill': '0%' });
    expect(rangeFill(1, 1, 1)).toEqual({ '--fill': '0%' });
  });
});

describe('buttonClass', () => {
  it('gives the primary button the brand look (index.css drops it while disabled)', () => {
    expect(buttonClass('primary')).toContain('btn-brand');
    expect(buttonClass('secondary')).not.toContain('btn-brand');
  });
});
