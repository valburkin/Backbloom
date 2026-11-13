import { describe, expect, it } from 'vitest';
import { defaultRoom, shapePath, type LayoutSpec } from '../../../layout';
import { controllerIndex, locateLed } from '../model';

const room = defaultRoom();
// one controller, output 0: LEDs 0-29 (a 20-LED strip, the rest dark), output 1: LEDs 30-79; output 2 has no strip
const spec: LayoutSpec = {
  version: 1,
  room,
  controllers: [
    {
      id: 'c',
      name: 'Desk',
      kind: 'wled',
      host: 'wled.local',
      outputs: [
        { index: 0, start: 0, length: 30 },
        { index: 1, start: 30, length: 50 },
        { index: 2, start: 80, length: 10 },
      ],
    },
  ],
  strips: [
    { id: 'b', name: 'Behind', controllerId: 'c', output: 1, ledCount: 50, path: shapePath(room, 'behind-furniture') },
    { id: 'a', name: 'Around', controllerId: 'c', output: 0, ledCount: 20, path: shapePath(room, 'around-screen') },
  ],
};

describe('locateLed', () => {
  it('finds the strip and the LED on it for a controller-wide index', () => {
    expect(locateLed(spec, 'c', 0)).toEqual({ strip: spec.strips[1], stripIndex: 1, local: 0 });
    expect(locateLed(spec, 'c', 29)).toEqual({ strip: spec.strips[1], stripIndex: 1, local: 29 });
    expect(locateLed(spec, 'c', 30)).toEqual({ strip: spec.strips[0], stripIndex: 0, local: 0 });
    expect(locateLed(spec, 'c', 79)).toEqual({ strip: spec.strips[0], stripIndex: 0, local: 49 });
  });

  it('finds nothing outside the outputs, on an output without a strip, or on another controller', () => {
    expect(locateLed(spec, 'c', 90)).toBeNull();
    expect(locateLed(spec, 'c', -1)).toBeNull();
    expect(locateLed(spec, 'c', 85)).toBeNull();
    expect(locateLed(spec, 'other', 0)).toBeNull();
  });
});

describe('controllerIndex', () => {
  it('is the inverse of locateLed', () => {
    for (const index of [0, 12, 29, 30, 55, 79]) {
      const at = locateLed(spec, 'c', index)!;
      expect(controllerIndex(spec, at.strip, at.local)).toBe(index);
    }
  });

  it("is null for a strip on an output the controller doesn't have", () => {
    expect(controllerIndex(spec, { ...spec.strips[0], output: 7 }, 0)).toBeNull();
    expect(controllerIndex(spec, { ...spec.strips[0], controllerId: 'gone' }, 0)).toBeNull();
  });
});
