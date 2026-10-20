import { describe, expect, it } from 'vitest';
import type { LayoutSpec } from '../../layout';
import { buildLayoutView, standardFrameSpec } from '../layoutView';

const output = (index: number, start: number, length: number) => ({ index, start, length });

describe('layoutView', () => {
  it('draws strips on the same edge in their own lanes, with the LED indices of the engine', () => {
    // a desk strip around the monitor on one controller and a ceiling strip on another, both right to left
    const spec: LayoutSpec = {
      version: 1,
      controllers: [
        { id: 'desk', name: 'Desk', kind: 'ddp', host: '', outputs: [output(0, 0, 6)] },
        { id: 'ceiling', name: 'Ceiling', kind: 'ddp', host: '', outputs: [output(0, 2, 3)] },
      ],
      strips: [
        { id: 'a', name: 'Desk', controllerId: 'desk', output: 0, segments: [{ kind: 'edge', edge: 'top', count: 6, from: 1, to: 0, depth: 0.1 }] },
        // four LEDs on an output of three: the last one isn't driven
        { id: 'b', name: 'Ceiling', controllerId: 'ceiling', output: 0, segments: [{ kind: 'edge', edge: 'top', count: 4, from: 1, to: 0, depth: 0.1 }] },
      ],
    };

    const view = buildLayoutView(spec);

    const desk = view.leds.filter((l) => l.led < 6);
    const ceiling = view.leds.filter((l) => l.led >= 6);
    expect(desk.map((l) => l.led)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(ceiling.map((l) => l.led)).toEqual([8, 9, 10]);
    expect(desk[0].x).toBeGreaterThan(desk[5].x);
    expect(ceiling[0].y).toBeLessThan(desk[0].y);
    expect(view.leds.every((l) => l.y < view.screen.y)).toBe(true);
    expect(view.width).toBeGreaterThan(view.screen.w);
  });

  it('matches the backend standard frame: 50 LEDs clockwise from the top left', () => {
    const view = buildLayoutView(standardFrameSpec());
    expect(view.leds).toHaveLength(50);
    expect(view.leds[0].x).toBeLessThan(view.leds[15].x);
    expect(view.leds[16].x).toBeGreaterThan(view.screen.x + view.screen.w);
    expect(view.leds[49].x).toBeLessThan(view.screen.x);
  });
});
