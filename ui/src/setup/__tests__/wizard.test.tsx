import { describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MockBackend } from '../../api/mock';
import { ConnectionProvider } from '../../app/connection';
import { ConfigProvider } from '../../app/config';
import { EditorProvider } from '../../app/editor';
import { ToastProvider } from '../../app/toast';
import type { WledState } from '../../api/types';
import { ChecksStep, lengthToLight } from '../ChecksStep';
import { StripTypeStep } from '../StripTypeStep';
import { PowerStep } from '../PowerStep';
import { FindStripsStep, identifyPattern, remainingCandidates, stepConnection } from '../FindStripsStep';
import { togglePlace } from '../StripPlaces';
import { FindStep, boardCard, needsWifi, sortBoards } from '../FindStep';
import { resumeView, withDefaultBack } from '../SetUpBoard';
import { SAFE_PINS, boardChange, boardOutputs, createSerial, initialBoard, safePin, setUpAlready, stripsFromFound, waitForBoard, withStrip } from '../wled';
import type { StepFrame } from '../parts';

function render(node: ReactNode): string {
  return renderToStaticMarkup(
    <ToastProvider>
      <ConnectionProvider backend={new MockBackend()}>
        <ConfigProvider>
          <EditorProvider>{node}</EditorProvider>
        </ConfigProvider>
      </ConnectionProvider>
    </ToastProvider>,
  );
}

/** A frame that remembers what the step gave it. */
function capture(): { frame: StepFrame; props: () => Parameters<StepFrame>[0] } {
  let last: Parameters<StepFrame>[0] | null = null;
  return {
    frame: (p) => {
      last = p;
      return <>{p.children}</>;
    },
    props: () => last!,
  };
}

const board: WledState = {
  name: 'WLED',
  version: '0.15.0',
  chip: 'ESP32',
  mac: '',
  ledCount: 30,
  outputs: [{ index: 0, strip: 'ws2812b', pin: 16, length: 30, start: 0, colorOrder: 'GRB', reverse: false }],
  maxCurrent: 0,
  ledCurrent: 55,
  wifi: { ssid: 'Home' },
};
const settings = initialBoard(board);

describe('wizard Back', () => {
  it('keeps the wizard’s Back on a strip’s first check, the check before on the others', () => {
    const f = capture();
    render(<ChecksStep frame={f.frame} host="h" board={settings} strip={0} onChange={() => {}} onLit={() => {}} onFinish={() => {}} />);
    expect(f.props().back).toBeUndefined();
    expect(f.props().backDisabled).toBe(false);
    let went = '';
    const merged = withDefaultBack(f.props(), () => (went = 'strips'));
    merged.back?.();
    expect(went).toBe('strips');
    // a step's own Back wins
    const own = withDefaultBack({ back: () => (went = 'own') }, () => (went = 'strips'));
    own.back?.();
    expect(went).toBe('own');
  });

  it('offers Back while nothing runs', () => {
    const f = capture();
    render(<StripTypeStep frame={f.frame} host="h" board={stripsFromFound(settings, [{ candidate: { pin: 16 }, places: ["desk"] }])} strip={0} onChange={() => {}} onPlan={() => {}} onNext={() => {}} />);
    expect(f.props().backDisabled).toBe(false);
  });
});

describe('Find', () => {
  it('opens no USB port by itself: the search waits for Search', () => {
    const f = capture();
    const html = render(<FindStep frame={f.frame} onPick={() => {}} onAddress={() => {}} />);
    expect(html).toContain('Power the board on');
    expect(html).toContain('Windows may ask');
    expect(renderToStaticMarkup(<>{f.props().actions}</>)).toContain('Search');
    // no address to type before a search
    expect(html).not.toContain('Its address');
  });

  it('shows boards in plain words with what tells them apart: new ones first, no addresses', () => {
    const boards = sortBoards([
      { id: 'network:A', via: 'network', name: 'Desk', isNew: false, host: '192.168.1.40', mdnsName: 'wled-a1b2c3', ledCount: 168, setUp: true },
      { id: 'hotspot:WLED-AP', via: 'hotspot', name: 'WLED', isNew: true, hotspot: 'WLED-AP', signal: 88 },
      { id: 'usb:COM5', via: 'usb', name: 'WLED', isNew: true, port: 'COM5', portName: 'Silicon Labs CP210x (COM5)', chip: 'ESP32' },
    ]);
    expect(boards.map(boardCard)).toEqual([
      { title: 'New board', line: 'Plugged into this PC · Silicon Labs CP210x (COM5) · ESP32' },
      { title: 'New board', line: 'Its own Wi-Fi “WLED-AP” · strong signal' },
      { title: 'WLED “Desk”', line: 'On your Wi-Fi · wled-a1b2c3 · 168 LEDs set up' },
    ]);
    expect(boards.map(needsWifi)).toEqual([true, true, false]);
    // a new board on the network already needs no Wi-Fi step
    expect(needsWifi({ id: 'network:B', via: 'network', name: 'WLED', isNew: true, host: 'x' })).toBe(false);
  });

  it('asks before setting up a board that is set up already', () => {
    expect(setUpAlready(board)).toBeNull();
    expect(setUpAlready({ ...board, name: 'Desk' })).toBe('“Desk”, 1 output, 30 LEDs');
    expect(setUpAlready({ ...board, outputs: [{ ...board.outputs[0], length: 91 }, { ...board.outputs[0], index: 1, start: 91, length: 77 }] })).toBe('2 outputs, 168 LEDs');
  });
});

describe('going on where it was left', () => {
  const base = { host: 'h', read: board, board: initialBoard(board) };
  it('resumes at the same step; a board on the Wi-Fi already after the Wi-Fi step; running steps start again', () => {
    expect(resumeView({ ...base, view: { kind: 'power' } })).toEqual({ kind: 'power' });
    expect(resumeView({ ...base, view: { kind: 'wifi', board: { id: 'x', via: 'hotspot', name: 'WLED', isNew: true }, ssid: null, savedPassword: false } })).toEqual({ kind: 'power' });
    expect(resumeView({ ...base, host: null, board: null, view: { kind: 'wifi', board: { id: 'x', via: 'hotspot', name: 'WLED', isNew: true }, ssid: null, savedPassword: false } })).toEqual({ kind: 'find' });
    expect(resumeView({ ...base, view: { kind: 'check', strip: 1 } })).toEqual({ kind: 'type', strip: 1 });
    expect(resumeView({ ...base, view: { kind: 'strips' } })).toEqual({ kind: 'strips' });
  });
});

describe('power, strips and types', () => {
  it('asks the power supply first, required, with nothing to put off', () => {
    const f = capture();
    const html = render(<PowerStep frame={f.frame} host="h" supply={null} flashed onChange={() => {}} onNext={() => {}} />);
    expect(html).toContain('Your board’s lights just flashed');
    expect(html).toContain('OUTPUT 5V');
    expect(html).toContain('Powered over USB');
    expect(html).not.toContain('Check later');
    expect(renderToStaticMarkup(<>{f.props().actions}</>)).toContain('disabled');
  });

  it('finds the strips by lighting the board’s connections, never asking for pins first', () => {
    const f = capture();
    const html = render(<FindStripsStep frame={f.frame} host="h" board={settings} onDone={() => {}} />);
    expect(html).toContain('Looking for your strips');
    expect(html).not.toMatch(/\bpins?\b/i);
    expect(f.props().backDisabled).toBe(true);
    const c = [
      { output: 0, pin: 16 },
      { output: 1, pin: 2 },
      { output: 2, pin: 17 },
    ];
    expect(remainingCandidates(c, [{ candidate: c[1], places: ['ceiling'] }])).toEqual([c[0], c[2]]);
    // connections keep their numbers over the whole search: stepping skips the found ones, wrapping round
    const found = [{ candidate: c[1], places: ['ceiling' as const] }];
    expect(stepConnection(c, found, 0, 1)).toBe(2);
    expect(stepConnection(c, found, 2, 1)).toBe(0);
    expect(stepConnection(c, found, 0, -1)).toBe(2);
    expect(stepConnection(c, [...found, { candidate: c[0], places: ['desk' as const] }, { candidate: c[2], places: ['wall' as const] }], 0, 1)).toBe(-1);
    // a connection is lit white at the setup's low brightness (full brightness is for the power check only)
    expect(identifyPattern(2)).toEqual({ output: 2, pattern: 'color', color: [255, 255, 255] });
  });

  it('keeps the strips found, in the order found, on their pins', () => {
    const b = stripsFromFound(settings, [
      { candidate: { pin: 2 }, places: ['desk', 'screen'] },
      { candidate: { pin: 16 }, places: ['ceiling'] },
    ]);
    expect(b.strips.map((s) => [s.pin, s.places, s.preset, s.ledCount])).toEqual([
      [2, ['desk', 'screen'], null, null],
      [16, ['ceiling'], null, null],
    ]);
    expect(boardOutputs(b).map((o) => [o.index, o.pin])).toEqual([
      [0, 2],
      [1, 16],
    ]);
  });

  it('picks every place a strip passes, as a set in any combination', () => {
    expect(togglePlace([], 'desk')).toEqual(['desk']);
    // kept in the cards' order, whatever order they were picked in
    expect(togglePlace(['desk'], 'screen')).toEqual(['screen', 'desk']);
    expect(togglePlace(['screen', 'desk', 'wall'], 'screen')).toEqual(['desk', 'wall']);
  });

  it('shows the strip being set up, its type in buyers’ words, nothing picked', () => {
    const f = capture();
    const b = stripsFromFound(settings, [{ candidate: { pin: 16 }, places: ['desk', 'screen'] }]);
    const html = render(<StripTypeStep frame={f.frame} host="h" board={b} strip={0} onChange={() => {}} onPlan={() => {}} onNext={() => {}} />);
    expect(html).toContain('This is the strip along the desk and behind the monitor');
    expect(html).toContain('most common');
    expect(html).toContain('Continuous light, no visible dots');
    expect(html).toContain('Not sure? Answer three questions');
    expect(html).toContain('Not cut yet? Plan the cuts and power');
    expect(html).not.toMatch(/min-h-11[^"]*border-transparent/);
    expect(renderToStaticMarkup(<>{f.props().actions}</>)).toContain('Next: find how many LEDs it has');
    expect(renderToStaticMarkup(<>{f.props().actions}</>)).toContain('disabled');
    // the next strip offers the first one's type
    const two = withStrip(stripsFromFound(settings, [
      { candidate: { pin: 16 }, places: ['desk'] },
      { candidate: { pin: 2 }, places: ['ceiling'] },
    ]), 0, { preset: 'ws2815' });
    expect(render(<StripTypeStep frame={f.frame} host="h" board={two} strip={1} onChange={() => {}} onPlan={() => {}} onNext={() => {}} />)).toContain('Same as strip 1');
  });

  it('suggest safe pins: never the serial console, the flash, or a pin in use', () => {
    const never: Record<keyof typeof SAFE_PINS, number[]> = {
      esp32: [0, 1, 3, 6, 7, 8, 9, 10, 11, 12, 34, 35, 36, 37, 38, 39],
      esp8266: [0, 1, 3, 6, 7, 8, 9, 10, 11, 15],
      'esp32-s3': [26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 43, 44],
      'esp32-s2': [26, 27, 28, 29, 30, 31, 32, 43, 44],
      'esp32-c3': [12, 13, 14, 15, 16, 17, 20, 21],
    };
    for (const [chip, bad] of Object.entries(never)) for (const pin of bad) expect(SAFE_PINS[chip as keyof typeof SAFE_PINS], `${chip} ${pin}`).not.toContain(pin);
    expect(safePin('ESP32', [16])).toBe(2);
    expect(safePin('ESP32', [16, 2])).toBe(17);
    expect(safePin('ESP8266', [2])).toBe(4);
    // APA102: the clock on a free safe pin, and a second strip's data pin not on that clock
    const apa = withStrip(settings, 0, { preset: 'apa102' });
    expect(boardOutputs(apa)[0].clockPin).toBe(2);
    const two = stripsFromFound(apa, [
      { candidate: { pin: 16 }, places: ['desk'] },
      { candidate: { pin: 17 }, places: ['ceiling'] },
    ]);
    const outs = boardOutputs(withStrip(two, 0, { preset: 'apa102' }));
    expect(new Set(outs.flatMap((o) => [o.pin, o.clockPin].filter((x) => x !== undefined))).size).toBe(3);
    expect(boardChange(apa).outputs[0].clockPin).toBe(2);
  });

  it('lights LEDs past the written length by writing a longer one first', () => {
    expect(lengthToLight(67)).toBe(117);
    expect(lengthToLight(3990)).toBe(4000);
  });
});

describe('board writes', () => {
  it('land in the order they were made, also when an earlier one is slower or fails', async () => {
    const serial = createSerial();
    const order: string[] = [];
    const slow = serial(() => new Promise<void>((r) => setTimeout(() => (order.push('length 117'), r()), 20)));
    const failing = serial(() => Promise.reject(new Error('busy')));
    const fast = serial(async () => void order.push('length 67'));
    await expect(failing).rejects.toThrow('busy');
    await Promise.all([slow, fast]);
    expect(order).toEqual(['length 117', 'length 67']);
  });
});

describe('waits that stop', () => {
  it('stops polling once the step is left', async () => {
    const c = new AbortController();
    let reads = 0;
    let t = 0;
    const done = waitForBoard(
      () => {
        reads++;
        if (reads === 2) c.abort();
        return Promise.reject(new Error('down'));
      },
      { sleep: async (ms) => void (t += ms), now: () => t, intervalMs: 1000, timeoutMs: 30000, signal: c.signal },
    );
    await expect(done).rejects.toThrow('down');
    expect(reads).toBe(2);
  });
});
