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
  it('drops the brand gradient on a disabled primary button', () => {
    expect(buttonClass('primary')).toContain('bg-brand');
    expect(buttonClass('primary')).toContain('disabled:bg-none');
  });
});
