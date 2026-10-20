import { describe, expect, it } from 'vitest';
import legacyLeds from '../../layout/__fixtures__/my-layout.json';
import { appendLogs, filterLogs, formatLogs, normalizeLevel, type LogState } from '../logs';
import { averageColor, ledColor, PREVIEW_FRAME, previewPoints } from '../ledPreview';
import { performanceReducer, summarize } from '../performance';

const report = (type: number, extra: object = {}) => ({ type, id: 0, name: '', param1: 0, param2: 0, param3: 0, param4: 0, token: 1, remove: 0, ...extra });

describe('performance', () => {
  it('merges broadcasts and single reports, and drops removed ones', () => {
    let s = performanceReducer({}, { broadcast: [report(2, { param1: 29.94 }), report(3, { param1: 50, param4: 3 })] });
    s = performanceReducer(s, report(5, { id: -1, name: '{"aspect":36,"takenMem":23475,"totalPhysMem":64932}' }));
    s = performanceReducer(s, report(4, { id: -1, name: '{"cores":[0.1],"total":3.4}' }));
    expect(summarize(s)).toEqual({
      processingFps: 29.9,
      ledFps: 50,
      ledDropped: 3,
      controllers: [{ id: '0', name: '', fps: 50, dropped: 3, latencyMs: null }],
      cpuPercent: 3,
      ram: { usedMb: 23475, totalMb: 64932, percent: 36 },
      temperature: null,
    });
    s = performanceReducer(s, report(3, { remove: 1 }));
    expect(summarize(s).ledFps).toBeNull();
    expect(performanceReducer(s, { broadcast: [] })).toBe(s);
  });

  it('lists every controller and forgets the ones a broadcast no longer has', () => {
    const led = (id: number, controller: string, param1: number, extra: object = {}) =>
      report(3, { id, param1, controller, controllerName: controller.toUpperCase(), latencyMs: 0.42, ...extra });
    let s = performanceReducer({}, { broadcast: [report(2, { param1: 60 }), led(1, 'desk', 59.6, { param4: 2 }), led(0, 'tv', 60.04), led(2, 'shelf', 60, { latencyMs: -1 })] });
    const summary = summarize(s);
    expect(summary.controllers).toEqual([
      { id: 'tv', name: 'TV', fps: 60, dropped: 0, latencyMs: 0.42 },
      { id: 'desk', name: 'DESK', fps: 59.6, dropped: 2, latencyMs: 0.42 },
      { id: 'shelf', name: 'SHELF', fps: 60, dropped: 0, latencyMs: null },
    ]);
    expect(summary.ledFps).toBe(60);

    s = performanceReducer(s, { broadcast: [report(2, { param1: 60 }), led(0, 'tv', 60)] });
    expect(summarize(s).controllers.map((c) => c.id)).toEqual(['tv']);
    // a broadcast without LED reports (or a single report) leaves them alone
    expect(summarize(performanceReducer(s, { broadcast: [report(2)] })).controllers).toHaveLength(1);
  });

  it('treats warming-up reports as unknown', () => {
    expect(summarize(performanceReducer({}, report(2, { token: -1, param1: -1 }))).processingFps).toBeNull();
  });
});

describe('logs', () => {
  const msg = (levelString: string, message: string, extra: object = {}) => ({ levelString, message, loggerName: 'MAIN ', utime: '1792440440629', ...extra });

  it('appends, normalizes and caps the buffer', () => {
    let s: LogState = { lines: [], nextId: 1 };
    s = appendLogs(s, { messages: [msg('INFO', 'a'), msg('WARNING', 'b'), msg('DEBUG', 'c', { fileName: 'x.cpp', line: '7' })] });
    expect(s.lines.map((l) => [l.id, l.level, l.source, l.location])).toEqual([
      [1, 'INFO', 'MAIN', undefined],
      [2, 'WARNING', 'MAIN', undefined],
      [3, 'DEBUG', 'MAIN', 'x.cpp:7'],
    ]);
    s = appendLogs(s, { messages: [msg('ERROR', 'd'), msg('INFO', '<Report>raw')] }, 4);
    expect(s.lines.map((l) => l.message)).toEqual(['b', 'c', 'd', 'raw']);
    expect(s.lines[3].source).toBe('');
    expect(appendLogs(s, { nope: 1 })).toBe(s);
  });

  it('filters by level and text, and exports plain text', () => {
    const s = appendLogs({ lines: [], nextId: 1 }, { messages: [msg('INFO', 'Webserver started'), msg('ERROR', 'Device failed')] });
    expect(filterLogs(s.lines, new Set(['ERROR']), '').map((l) => l.message)).toEqual(['Device failed']);
    expect(filterLogs(s.lines, new Set(['INFO', 'ERROR']), 'web').map((l) => l.message)).toEqual(['Webserver started']);
    expect(formatLogs(s.lines).split('\n')[1]).toMatch(/^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ERROR {3}\[MAIN\] Device failed$/);
    expect(normalizeLevel('warn')).toBe('WARNING');
  });
});

describe('led preview', () => {
  it('pushes every LED of the real layout onto the frame around the screen', () => {
    const pts = previewPoints(legacyLeds);
    expect(pts).toHaveLength(legacyLeds.length);
    for (const p of pts) {
      const onFrame = p.x <= PREVIEW_FRAME || p.x >= 1 - PREVIEW_FRAME || p.y <= PREVIEW_FRAME || p.y >= 1 - PREVIEW_FRAME;
      expect(onFrame).toBe(true);
    }
    // first LED samples the bottom-right corner area
    expect(pts[0].y).toBeGreaterThan(0.9);
  });

  it('reads colors from a flat frame', () => {
    expect(ledColor([1, 2, 3, 4, 5, 6], 1)).toBe('rgb(4,5,6)');
    expect(ledColor(null, 0)).toBe('rgb(30,34,46)');
    expect(averageColor([0, 0, 0, 255, 255, 255])).toEqual([128, 128, 128]);
  });
});
