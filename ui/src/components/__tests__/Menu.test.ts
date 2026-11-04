import { describe, expect, it } from 'vitest';
import { placeMenu } from '../Menu';

const viewport = { width: 1000, height: 800 };
const size = { width: 288, height: 300 };
const button = (left: number, top: number) => ({ left, top, right: left + 80, bottom: top + 32 });

describe('placeMenu', () => {
  it('opens below the button, lined up with the chosen edge', () => {
    expect(placeMenu(button(600, 100), size, viewport, 'right')).toEqual({ left: 680 - 288, top: 136, maxHeight: 800 - 132 - 4 - 8 });
    expect(placeMenu(button(600, 100), size, viewport, 'left')).toMatchObject({ left: 600, top: 136 });
  });

  it('opens above when it does not fit below and there is more room above', () => {
    const p = placeMenu(button(600, 600), size, viewport, 'right');
    expect(p).toEqual({ left: 392, bottom: 800 - 600 + 4, maxHeight: 600 - 4 - 8 });
  });

  it('stays below, capped, when there is no more room above', () => {
    const p = placeMenu(button(600, 300), { width: 288, height: 700 }, viewport, 'right');
    expect(p).toEqual({ left: 392, top: 336, maxHeight: 800 - 332 - 12 });
  });

  it('keeps inside the window edges', () => {
    expect(placeMenu(button(20, 100), size, viewport, 'right').left).toBe(8);
    expect(placeMenu(button(900, 100), size, viewport, 'left').left).toBe(1000 - 288 - 8);
  });

  it('never gets a negative height in a tiny window', () => {
    expect(placeMenu(button(0, 0), size, { width: 300, height: 20 }, 'left').maxHeight).toBe(0);
  });
});
