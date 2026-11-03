import { describe, expect, it } from 'vitest';
import type { ColorOrder } from '../../api/types';
import { defaultRoom, compileLayout, validateLayout, type LayoutSpec } from '../../layout';
import { DEFAULT_PLAN_INPUT, planStrips } from '../plan';
import type { WledState } from '../../api/types';
import {
  DEFAULT_LEDS,
  USB_SAFE_MA,
  maxOutputs,
  unplacedStrips,
  placesWords,
  boardChange,
  boardName,
  boardOutputs,
  colorOrderFromAnswers,
  hostOf,
  initialBoard,
  ledsFromLength,
  placedPath,
  plannedPath,
  plannedScreen,
  stripName,
  stripsFromFound,
  stripProblem,
  supplyAmps,
  waitForBoard,
  withStrip,
  wledController,
  type SeenColor,
} from '../wled';

const ORDERS: ColorOrder[] = ['GRB', 'RGB', 'BRG', 'RBG', 'BGR', 'GBR'];
const NAME: Record<string, SeenColor> = { R: 'red', G: 'green', B: 'blue' };

/** What a strip whose chips take `actual` shows for a color sent with the order `setting`. */
function seen(setting: ColorOrder, actual: ColorOrder, color: 'R' | 'G'): SeenColor {
  return NAME[actual[setting.indexOf(color)]];
}

describe('color order from the color check', () => {
  it('finds every strip order from what red and green looked like, whatever the board sent', () => {
    for (const setting of ORDERS)
      for (const actual of ORDERS) expect(colorOrderFromAnswers(setting, seen(setting, actual, 'R'), seen(setting, actual, 'G')), `${setting} on ${actual}`).toBe(actual);
  });

  it('keeps the order when red looked red and green green', () => {
    expect(colorOrderFromAnswers('GRB', 'red', 'green')).toBe('GRB');
    // WLED's default on an RGB strip: red looks green and green red
    expect(colorOrderFromAnswers('GRB', 'green', 'red')).toBe('RGB');
  });

  it('gives up on answers no color order explains', () => {
    expect(colorOrderFromAnswers('GRB', 'red', 'red')).toBeNull();
    expect(colorOrderFromAnswers('GRB', 'mixed', 'green')).toBeNull();
    expect(colorOrderFromAnswers('GRB', 'red', 'none')).toBeNull();
  });
});

describe('board settings', () => {
  const read: WledState = {
    name: 'WLED',
    version: '0.15.0',
    chip: 'ESP32',
    mac: '',
    ledCount: 30,
    outputs: [{ index: 0, strip: 'ws2812b', pin: 16, length: 30, start: 0, colorOrder: 'GRB', reverse: false }],
    maxCurrent: 850,
    ledCurrent: 55,
    wifi: { ssid: 'Home' },
  };
  const owner: WledState = {
    ...read,
    name: 'Desk',
    ledCount: 168,
    outputs: [
      { index: 0, strip: 'ws2812b', pin: 16, length: 91, start: 0, colorOrder: 'GRB', reverse: false },
      { index: 1, strip: 'ws2812b', pin: 17, length: 77, start: 91, colorOrder: 'RGB', reverse: false },
    ],
  };

  /** A board's settings with every required answer given. */
  const answered = (x: ReturnType<typeof initialBoard>) => ({
    ...x,
    supply: { kind: 'printed', volts: 5, amps: 6 } as const,
    strips: x.strips.map((st, i) => ({ ...st, places: st.places.length ? st.places : [i === 0 ? ('screen' as const) : ('ceiling' as const)], preset: st.preset ?? 'ws2812b' })),
  });

  it('starts a new board with one strip on its own pin, asking where it is, its type, its LEDs and the supply', () => {
    const b = initialBoard(read);
    expect(b.strips).toEqual([{ places: [], preset: null, ledCount: null, pin: 16, clockPin: null, colorOrder: null, plan: null }]);
    expect(b).toMatchObject({ supply: null, chip: 'ESP32' });
    // a strip is written once its type is picked
    expect(unplacedStrips(b)).toEqual([0]);
    expect(stripProblem(b, 0)).toBe('Pick the strip’s type.');
    expect(stripProblem(answered(b), 0)).toBeNull();
    // unknown count: the check starts at the default
    expect(boardOutputs(answered(b))[0].length).toBe(DEFAULT_LEDS);
    expect(boardChange(answered(b))).toMatchObject({ name: 'Monitor lights', maxCurrent: 5100, ledCurrent: 60 });
    expect(boardChange({ ...answered(b), supply: { kind: 'usb' } }).maxCurrent).toBe(USB_SAFE_MA);
  });

  it('keeps the strips of a board that is set up: two strips, 91 and 77 LEDs, places still asked', () => {
    const b = initialBoard(owner);
    expect(b.strips.map((x) => [x.places, x.preset, x.ledCount, x.pin, x.colorOrder])).toEqual([
      [[], 'ws2812b', 91, 16, 'GRB'],
      [[], 'ws2812b', 77, 17, 'RGB'],
    ]);
    expect(unplacedStrips(b)).toEqual([0, 1]);
    expect(boardOutputs(b).map((o) => [o.index, o.start, o.length])).toEqual([
      [0, 0, 91],
      [1, 91, 77],
    ]);
    const placed = answered(b);
    // its own name stays: none is written
    expect(boardChange(placed).name).toBeUndefined();
    expect(boardName(placed)).toBe('Desk');
    expect([stripName(placed, 0), stripName(placed, 1)]).toEqual(['Monitor', 'Ceiling']);
    expect([stripName(b, 0), stripName(b, 1)]).toEqual(['Strip 1', 'Strip 2']);
  });

  it('names the device after its strips', () => {
    const two = stripsFromFound(initialBoard(read), [
      { candidate: { pin: 16 }, places: [] },
      { candidate: { pin: 2 }, places: [] },
    ]);
    expect(boardName(answered(two))).toBe('Monitor and ceiling');
    const twoCeilings = withStrip(answered(two), 0, { places: ['ceiling'] });
    expect([stripName(twoCeilings, 0), stripName(twoCeilings, 1)]).toEqual(['Ceiling 1', 'Ceiling 2']);
    expect([maxOutputs('ESP32'), maxOutputs('ESP8266'), maxOutputs('ESP32-C3'), maxOutputs('ESP32-S3'), maxOutputs('ESP32-S2'), maxOutputs(undefined)]).toEqual([16, 3, 2, 12, 12, 16]);
  });

  it('writes outputs as the protocol takes them: no index, a clock pin for two-wire strips', () => {
    const b = answered(initialBoard(read));
    expect(boardChange(b).outputs).toEqual([{ strip: 'ws2812b', pin: 16, length: DEFAULT_LEDS, start: 0, reverse: false }]);
    const apa = withStrip(b, 0, { preset: 'apa102', colorOrder: null });
    expect(boardChange(apa).outputs[0]).toMatchObject({ strip: 'apa102', pin: 16, clockPin: 2 });
    // WS2815 only: the board's own current model
    expect(boardChange(withStrip(b, 0, { preset: 'ws2815' })).ledCurrent).toBeUndefined();
    expect(boardChange({ ...b, supply: { kind: 'printed', volts: 5, amps: 0.1 } }).maxCurrent).toBe(250);
    expect(boardChange({ ...b, supply: { kind: 'printed', volts: 5, amps: 100 } }).maxCurrent).toBe(65000);
  });

  it('won’t write a supply whose volts don’t fit a strip, or two strips on one pin', () => {
    const b = answered(initialBoard(read));
    expect(stripProblem({ ...b, supply: { kind: 'printed', volts: 12, amps: 5 } }, 0)).toMatch(/burn the LEDs out/);
    // over USB is 5 V
    expect(stripProblem(withStrip({ ...b, supply: { kind: 'usb' } }, 0, { preset: 'ws2815' }), 0)).toMatch(/stay dark/);
    // WS2814 RGBW runs on 12 and 24 V
    expect(stripProblem(withStrip({ ...b, supply: { kind: 'printed', volts: 12, amps: 5 } }, 0, { preset: 'ws2814-rgbw' }), 0)).toBeNull();
    const two = answered(stripsFromFound(b, [
      { candidate: { pin: 16 }, places: ['desk'] },
      { candidate: { pin: 16 }, places: ['ceiling'] },
    ]));
    expect(stripProblem(two, 1)).toMatch(/same pin/);
    // a two-wire strip's clock gets a free pin, never a data pin in use
    expect(stripProblem(withStrip(withStrip(two, 1, { pin: 2 }), 0, { preset: 'apa102' }), 0)).toBeNull();
  });

  it('counts LEDs from a length', () => {
    expect(ledsFromLength(150, 60)).toBe(90);
    expect(ledsFromLength(205, 60)).toBe(123);
    expect(ledsFromLength(0, 60)).toBeNull();
  });

  it('reads a supply in amps or watts', () => {
    expect(supplyAmps(5, 4, 'A')).toBe(4);
    expect(supplyAmps(12, 60, 'W')).toBe(5);
    expect(supplyAmps(5, 0, 'A')).toBe(0);
  });

  it('places each strip by where it is', () => {
    const room = defaultRoom();
    const strip = { ...initialBoard(read).strips[0], places: ['screen' as const] };
    expect(placedPath(strip, room, 60)[0].surface).toBe('front');
    expect(placedPath({ ...strip, places: ['ceiling'] }, room, 60)[0].surface).toBe('ceiling');
    const desk = placedPath({ ...strip, places: ['desk'] }, room, 60);
    expect(desk[0].y).toBeGreaterThan(room.screen.y + room.screen.height);
    // a plan places it as planned, matched at the corners while the count is the plan's
    const plan = planStrips(DEFAULT_PLAN_INPUT)!;
    const planned = placedPath({ ...strip, plan }, room, plan.totalLeds);
    expect(planned.some((p) => p.at !== undefined)).toBe(true);
    expect(placedPath({ ...strip, plan }, room, plan.totalLeds + 3).some((p) => p.at !== undefined)).toBe(false);
    // one strip along several places, in order from its start: each place's part, a gap between them
    const both = placedPath({ ...strip, places: ['desk', 'screen'] }, room, 120);
    expect(both[0].y).toBeGreaterThan(room.screen.y + room.screen.height);
    expect(both.filter((p) => p.next?.kind === 'gap')).toHaveLength(1);
    expect(stripName({ ...answered(initialBoard(read)), strips: [{ ...strip, places: ['desk', 'screen'] }] }, 0)).toBe('Desk and monitor');
    expect(placesWords(['desk', 'screen'])).toBe('along the desk and behind the monitor');
  });

  it('strips the address the board reports down to its host', () => {
    expect(hostOf('http://192.168.1.50/')).toBe('192.168.1.50');
    expect(hostOf(' wled-desk.local ')).toBe('wled-desk.local');
  });
});

describe('waiting for a restarting board', () => {
  it('reads until it answers', async () => {
    let calls = 0;
    let t = 0;
    const read = () => (++calls < 3 ? Promise.reject(new Error('down')) : Promise.resolve('up'));
    await expect(waitForBoard(read, { sleep: async (ms) => void (t += ms), now: () => t, intervalMs: 1000, timeoutMs: 10000 })).resolves.toBe('up');
    expect(calls).toBe(3);
  });

  it('gives up after the timeout with the last error', async () => {
    let t = 0;
    await expect(
      waitForBoard(() => Promise.reject(new Error('still down')), { sleep: async (ms) => void (t += ms), now: () => t, intervalMs: 1000, timeoutMs: 3000 }),
    ).rejects.toThrow('still down');
  });
});

describe('the planned strip in the layout', () => {
  it('draws the run around the screen, matched at the corners, with a gap across the stand', () => {
    const plan = planStrips({ ...DEFAULT_PLAN_INPUT, screen: { widthMm: 597, heightMm: 336 }, sides: 4, start: 'top-left', standGapMm: 200 })!;
    const room = defaultRoom();
    const screen = plannedScreen(plan, room);
    expect(screen.width).toBeCloseTo(0.597);
    expect(screen.y + screen.height).toBeCloseTo(room.screen.y + room.screen.height);
    const path = plannedPath(plan, screen);
    // top, right, bottom right, (stand), bottom left, left: 6 corners and the stand's two ends
    expect(path).toHaveLength(7);
    expect(path.filter((p) => p.next?.kind === 'gap')).toHaveLength(1);
    expect(path.map((p) => p.at)).toEqual([undefined, 33, 50, 60, 60, 70, undefined]);
    expect(path[0]).toMatchObject({ surface: 'front' });
    expect(path[0].x).toBeCloseTo(screen.x + 0.02);

    const controller = wledController('192.168.1.77', 'Screen', [{ index: 0, pin: 2, length: plan.totalLeds, start: 0 }]);
    const spec: LayoutSpec = {
      version: 1,
      room: { ...room, screen },
      controllers: [controller],
      strips: [{ id: 'strip-1', name: 'Screen', controllerId: controller.id, output: 0, ledCount: plan.totalLeds, path }],
    };
    expect(validateLayout(spec).filter((i) => i.severity === 'error')).toEqual([]);
    expect(compileLayout(spec)[0].leds).toHaveLength(plan.totalLeds);
    expect(controller).toMatchObject({ id: '192-168-1-77', kind: 'wled', device: { type: 'wled', host: '192.168.1.77' } });
  });
});
