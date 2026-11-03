import { describe, expect, it } from 'vitest';
import type { WledChange, WledState } from '../../api/types';
import type { Controller } from '../../layout';
import { addedThisSession } from '../../pages/welcome/ready';
import { colorCheckTypes } from '../ChecksStep';
import { keepFoundStrips } from '../FindStripsStep';
import { boardSetupInProgress, forgetBoardSetup, rememberBoardSetup, startingProgress } from '../SetUpBoard';
import { boardChange, initialBoard, outputsAsRead, stripProblem, stripsFromFound, wledController, withStrip } from '../wled';

const owner: WledState = {
  name: 'Desk',
  version: '0.15.0',
  chip: 'ESP32',
  mac: '',
  ledCount: 300,
  outputs: [
    { index: 0, strip: 'ws2812b', pin: 16, length: 91, start: 0, colorOrder: 'GRB', reverse: false },
    // not right after the first: WLED lets an output start anywhere
    { index: 1, strip: 'ws2812b', pin: 17, length: 77, start: 200, colorOrder: 'GRB', reverse: false },
  ],
  maxCurrent: 8500,
  ledCurrent: 55,
  wifi: { ssid: 'Home' },
};

describe('a board’s setup left this session', () => {
  it('goes on only from Continue setting up your board; LED strips on a WLED board starts afresh', () => {
    forgetBoardSetup();
    const p = { view: { kind: 'power' as const }, host: '192.168.1.77', read: owner, board: initialBoard(owner) };
    rememberBoardSetup(p);
    expect(boardSetupInProgress()).toBe(true);
    expect(startingProgress(true)).toEqual(p);
    // the plain choice: a new board, and the one left is forgotten
    expect(startingProgress(false)).toBeNull();
    expect(boardSetupInProgress()).toBe(false);
    expect(startingProgress(true)).toBeNull();
  });
});

describe('Add it as it is', () => {
  it('adds the outputs where the board has them, not one after the other', () => {
    const c = wledController('wled-desk.local', 'Desk', outputsAsRead(owner));
    expect(c.outputs).toEqual([
      { index: 0, start: 0, length: 91, pin: 16 },
      { index: 1, start: 200, length: 77, pin: 17 },
    ]);
  });
});

describe('keeping the strips found', () => {
  const board = answeredFound();
  function answeredFound() {
    return stripsFromFound(initialBoard({ ...owner, name: 'WLED', outputs: [owner.outputs[0]] }), [
      { candidate: { pin: 16 }, places: ['desk'] },
      { candidate: { pin: 2 }, places: ['ceiling'] },
    ]);
  }

  it('writes the strips first (waiting while the board restarts), then ends the search keeping them', async () => {
    const calls: string[] = [];
    let ended = false;
    await keepFoundStrips(
      {
        wledWrite: async (_h: string, change: WledChange) => {
          calls.push(`write ${change.outputs?.length}`);
          return true;
        },
        wledRead: async () => void calls.push('read'),
        wledProbeEnd: async (_h: string, keep: boolean) => void calls.push(`end ${keep}`),
      },
      'h',
      board,
      new AbortController().signal,
      () => (ended = true),
    );
    expect(calls).toEqual(['write 2', 'read', 'end true']);
    expect(ended).toBe(true);
    expect(boardChange(board).outputs.map((o) => o.pin)).toEqual([16, 2]);
  });

  it('leaves the search on when the write fails (leaving puts the board’s settings back)', async () => {
    const calls: string[] = [];
    let ended = false;
    await expect(
      keepFoundStrips(
        {
          wledWrite: async () => {
            throw new Error('Couldn’t reach h');
          },
          wledRead: async () => undefined,
          wledProbeEnd: async () => void calls.push('end'),
        },
        'h',
        board,
        new AbortController().signal,
        () => (ended = true),
      ),
    ).rejects.toThrow('Couldn’t reach h');
    expect(calls).toEqual([]);
    expect(ended).toBe(false);
  });
});

describe('the color check’s other strip types', () => {
  it('offers only types wired the same way that fit the supply, and checks them like the type step', () => {
    const data = colorCheckTypes('ws2812b', 5).map((p) => p.id);
    expect(data).toContain('sk6812-rgbw');
    expect(data).not.toContain('apa102');
    // 12 V strips don't fit a 5 V supply
    expect(data).not.toContain('ws2815');
    expect(colorCheckTypes('apa102', 5).every((p) => p.clock)).toBe(true);
    const b = { ...initialBoard(owner), supply: { kind: 'printed' as const, volts: 5 as const, amps: 4 } };
    expect(stripProblem(withStrip(b, 0, { preset: 'ws2815' }), 0)).toMatch(/takes 12 V/);
    expect(stripProblem(withStrip(b, 0, { preset: 'sk6812-rgbw' }), 0)).toBeNull();
  });
});

describe('the first-run setup’s own saves', () => {
  const wled: Controller = { id: 'desk', name: 'Desk', kind: 'wled', host: 'wled-desk.local', outputs: [{ index: 0, start: 0, length: 91 }] };
  const test: Controller = { id: 'controller-1', name: 'LED controller', kind: 'file', host: '', outputs: [{ index: 0, start: 0, length: 1 }], device: { type: 'file' } };

  it('saves by itself only once the setup added a device (an imported layout stays as it was)', () => {
    expect(addedThisSession(new Set(['desk']), [wled])).toBe(false);
    expect(addedThisSession(new Set(['controller-1']), [wled])).toBe(true);
    expect(addedThisSession(new Set(['controller-1']), [test])).toBe(false);
    expect(addedThisSession(null, [wled])).toBe(false);
  });
});
