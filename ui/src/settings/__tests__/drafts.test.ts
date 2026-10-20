import { describe, expect, it } from 'vitest';
import { deepEqual, dirtySections, draftsReducer, isPageDirty, setIn, type DraftsState } from '../drafts';

const smoothing = { enable: true, time_ms: 150, type: 'Stepper' };

describe('draftsReducer', () => {
  it('tracks an edit against the section as it was when editing started', () => {
    let s: DraftsState = {};
    s = draftsReducer(s, { type: 'edit', page: 'colors', section: 'smoothing', value: { ...smoothing, time_ms: 200 }, current: smoothing });
    expect(dirtySections(s.colors)).toEqual(['smoothing']);
    // the live config changes meanwhile (e.g. a preview); the baseline stays the original
    s = draftsReducer(s, { type: 'edit', page: 'colors', section: 'smoothing', value: { ...smoothing, time_ms: 300 }, current: { ...smoothing, time_ms: 200 } });
    expect(s.colors.baseline.smoothing).toEqual(smoothing);
    expect(s.colors.edits.smoothing).toEqual({ ...smoothing, time_ms: 300 });
  });

  it('becomes clean again when the value returns to the baseline', () => {
    let s: DraftsState = {};
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'smoothing', value: { ...smoothing, enable: false }, current: smoothing });
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'smoothing', value: { time_ms: 150, type: 'Stepper', enable: true }, current: smoothing });
    expect(isPageDirty(s.p)).toBe(false);
  });

  it('discards a page and marks saved sections clean', () => {
    let s: DraftsState = {};
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'a', value: 1, current: 0 });
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'b', value: 2, current: 0 });
    s = draftsReducer(s, { type: 'saved', page: 'p', sections: ['a'] });
    expect(dirtySections(s.p)).toEqual(['b']);
    s = draftsReducer(s, { type: 'discard', page: 'p' });
    expect(s.p).toBeUndefined();
    expect(draftsReducer(s, { type: 'discard', page: 'p' })).toBe(s);
  });

  it('keeps a section edited again while it was being saved', () => {
    let s: DraftsState = {};
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'a', value: 1, current: 0 });
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'b', value: 1, current: 0 });
    // save sent a=1, b=1; a changed to 2 before the reply
    s = draftsReducer(s, { type: 'edit', page: 'p', section: 'a', value: 2, current: 0 });
    s = draftsReducer(s, { type: 'saved', page: 'p', sections: ['a', 'b'], values: { a: 1, b: 1 } });
    expect(s.p.edits).toEqual({ a: 2 });
    expect(s.p.baseline).toEqual({ a: 1 });
  });
});

describe('helpers', () => {
  it('compares deeply regardless of key order', () => {
    expect(deepEqual({ a: [1, { b: 2 }], c: 'x' }, { c: 'x', a: [1, { b: 2 }] })).toBe(true);
    expect(deepEqual({ a: [1, 2] }, { a: [2, 1] })).toBe(false);
    expect(deepEqual({ a: 1 }, { a: 1, b: undefined })).toBe(true);
  });

  it('sets nested values immutably, including inside arrays', () => {
    const color = { channelAdjustment: [{ gamma: 1.5, red: [255, 0, 0] }] };
    const next = setIn(color, ['channelAdjustment', 0, 'gamma'], 2);
    expect(next.channelAdjustment[0]).toEqual({ gamma: 2, red: [255, 0, 0] });
    expect(color.channelAdjustment[0].gamma).toBe(1.5);
    expect(next.channelAdjustment[0].red).toBe(color.channelAdjustment[0].red);
  });
});
