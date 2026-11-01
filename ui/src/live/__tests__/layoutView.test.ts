import { describe, expect, it } from 'vitest';
import { behindFurniture, ceilingFront, defaultRoom, type LayoutSpec } from '../../layout';
import { buildLayoutView, standardFrameSpec, stripLedRanges } from '../layoutView';

const output = (index: number, start: number, length: number) => ({ index, start, length });

describe('layoutView', () => {
  it('draws the strips where they are in the room, with the LED indices of the engine', () => {
    // a strip behind the desk on one controller and a ceiling strip on another, both right to left
    const room = defaultRoom();
    const spec: LayoutSpec = {
      version: 1,
      room,
      controllers: [
        { id: 'desk', name: 'Desk', kind: 'ddp', host: '', outputs: [output(0, 0, 6)] },
        { id: 'ceiling', name: 'Ceiling', kind: 'ddp', host: '', outputs: [output(0, 2, 3)] },
      ],
      strips: [
        { id: 'a', name: 'Desk', controllerId: 'desk', output: 0, ledCount: 6, path: behindFurniture(room) },
        // four LEDs on an output of three: the last one isn't driven; the second is dark
        { id: 'b', name: 'Ceiling', controllerId: 'ceiling', output: 0, ledCount: 4, path: ceilingFront(room), dark: [{ start: 1, count: 1 }] },
      ],
    };

    const view = buildLayoutView(spec);

    const desk = view.leds.filter((l) => l.led < 6);
    const ceiling = view.leds.filter((l) => l.led >= 6);
    expect(desk.map((l) => l.led)).toEqual([0, 1, 2, 3, 4, 5]);
    expect(ceiling.map((l) => l.led)).toEqual([8, 10]);
    expect(desk[0].x).toBeGreaterThan(desk[5].x);
    // The ceiling is above the front wall, the desk below the screen.
    expect(ceiling[0].y).toBeLessThan(view.screen.y);
    expect(desk[0].y).toBeGreaterThan(view.screen.y + view.screen.h);
    expect(view.surfaces.length).toBeGreaterThan(1);
    expect(view.width).toBeGreaterThan(view.screen.w);
  });

  it('places each strip in the engine LED list after the controllers before it', () => {
    const room = defaultRoom();
    const spec: LayoutSpec = {
      version: 1,
      room,
      controllers: [
        { id: 'desk', name: 'Desk', kind: 'ddp', host: '', outputs: [output(0, 0, 6)] },
        { id: 'ceiling', name: 'Ceiling', kind: 'ddp', host: '', outputs: [output(0, 2, 3)] },
      ],
      strips: [
        { id: 'a', name: 'Desk', controllerId: 'desk', output: 0, ledCount: 6, path: behindFurniture(room) },
        { id: 'b', name: 'Ceiling', controllerId: 'ceiling', output: 0, ledCount: 4, path: ceilingFront(room) },
        { id: 'c', name: 'Unplugged', controllerId: 'gone', output: 0, ledCount: 4, path: ceilingFront(room) },
      ],
    };
    const ranges = stripLedRanges(spec);
    expect(ranges.get('a')).toEqual({ first: 0, length: 6 });
    expect(ranges.get('b')).toEqual({ first: 8, length: 3 });
    expect(ranges.has('c')).toBe(false);
  });

  it('matches the backend standard frame: 50 LEDs clockwise from the top left', () => {
    const view = buildLayoutView(standardFrameSpec());
    expect(view.leds).toHaveLength(50);
    expect(view.leds[0].x).toBeLessThan(view.leds[15].x);
    expect(view.leds[16].x).toBeGreaterThanOrEqual(view.screen.x + view.screen.w - 1e-6);
    expect(view.leds[30].y).toBeGreaterThanOrEqual(view.screen.y + view.screen.h - 1e-6);
    expect(view.leds[49].x).toBeLessThanOrEqual(view.screen.x + 1e-6);
  });
});
