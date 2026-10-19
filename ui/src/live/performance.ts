import type { PerformanceReport } from '../api/types';

/** Report types of the backend's PerformanceReportType. */
export const REPORT = { INSTANCE: 2, LED: 3, CPU: 4, RAM: 5, TEMPERATURE: 6 } as const;

export type PerformanceState = Record<string, PerformanceReport>;

const keyOf = (r: PerformanceReport) => `${r.type}:${r.id}`;

/** Applies one `performance-update` payload: a single report or `{ broadcast: [...] }`; `remove` drops a report. */
export function performanceReducer(state: PerformanceState, update: unknown): PerformanceState {
  const list: unknown[] =
    update && typeof update === 'object' && Array.isArray((update as { broadcast?: unknown }).broadcast)
      ? (update as { broadcast: unknown[] }).broadcast
      : [update];
  let next = state;
  for (const r of list) {
    if (!r || typeof r !== 'object' || typeof (r as PerformanceReport).type !== 'number') continue;
    const report = r as PerformanceReport;
    if (next === state) next = { ...state };
    if (report.remove) delete next[keyOf(report)];
    else next[keyOf(report)] = report;
  }
  return next;
}

export interface PerformanceSummary {
  /** Frames per second the lighting engine processes (screen capture or effects). */
  processingFps: number | null;
  /** Updates per second sent to the LED controller. */
  ledFps: number | null;
  /** Frames dropped on the way to the controller since the last report. */
  ledDropped: number;
  cpuPercent: number | null;
  ram: { usedMb: number; totalMb: number; percent: number } | null;
  temperature: number | null;
}

function parse<T>(json: string): T | null {
  try {
    return JSON.parse(json) as T;
  } catch {
    return null;
  }
}

/** Plain numbers for the Home page; reports still "warming up" (token ≤ 0) count as unknown. */
export function summarize(state: PerformanceState): PerformanceSummary {
  const reports = Object.values(state);
  const first = (type: number) => reports.find((r) => r.type === type && r.token > 0) ?? null;
  const instance = first(REPORT.INSTANCE);
  const led = first(REPORT.LED);
  const cpu = reports.find((r) => r.type === REPORT.CPU);
  const ram = reports.find((r) => r.type === REPORT.RAM);
  const temp = reports.find((r) => r.type === REPORT.TEMPERATURE);
  const cpuStat = cpu ? parse<{ total?: number }>(cpu.name) : null;
  const ramStat = ram ? parse<{ aspect?: number; takenMem?: number; totalPhysMem?: number }>(ram.name) : null;
  const fps = (r: PerformanceReport | null) => (r && r.param1 >= 0 ? Math.round(r.param1 * 10) / 10 : null);
  return {
    processingFps: fps(instance),
    ledFps: fps(led),
    ledDropped: led && led.param4 > 0 ? led.param4 : 0,
    cpuPercent: typeof cpuStat?.total === 'number' ? Math.round(cpuStat.total) : null,
    ram:
      ramStat && typeof ramStat.takenMem === 'number' && typeof ramStat.totalPhysMem === 'number'
        ? { usedMb: ramStat.takenMem, totalMb: ramStat.totalPhysMem, percent: ramStat.aspect ?? Math.round((100 * ramStat.takenMem) / ramStat.totalPhysMem) }
        : null,
    temperature: temp ? Number(temp.name) || null : null,
  };
}
