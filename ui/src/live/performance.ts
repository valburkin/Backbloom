import type { PerformanceReport } from '../api/types';

/** Report types of the backend's PerformanceReportType. */
export const REPORT = { INSTANCE: 2, LED: 3, CPU: 4, RAM: 5, TEMPERATURE: 6 } as const;

export type PerformanceState = Record<string, PerformanceReport>;

const keyOf = (r: PerformanceReport) => `${r.type}:${r.id}`;

/**
 * Applies one `performance-update` payload: a single report or `{ broadcast: [...] }`; `remove` drops a report. A
 * broadcast with LED reports lists every controller, so LED reports of controllers it doesn't mention are dropped.
 */
export function performanceReducer(state: PerformanceState, update: unknown): PerformanceState {
  const broadcast = update && typeof update === 'object' && Array.isArray((update as { broadcast?: unknown }).broadcast);
  const list: unknown[] = broadcast ? (update as { broadcast: unknown[] }).broadcast : [update];
  let next = state;
  const isReport = (r: unknown): r is PerformanceReport => !!r && typeof r === 'object' && typeof (r as PerformanceReport).type === 'number';
  if (broadcast && list.some((r) => isReport(r) && r.type === REPORT.LED)) {
    const listed = new Set(list.filter(isReport).map(keyOf));
    const stale = Object.keys(state).filter((k) => state[k].type === REPORT.LED && !listed.has(k));
    if (stale.length) {
      next = { ...state };
      for (const k of stale) delete next[k];
    }
  }
  for (const r of list) {
    if (!r || typeof r !== 'object' || typeof (r as PerformanceReport).type !== 'number') continue;
    const report = r as PerformanceReport;
    if (next === state) next = { ...state };
    if (report.remove) delete next[keyOf(report)];
    else next[keyOf(report)] = report;
  }
  return next;
}

/** One controller's LED report. */
export interface ControllerPerformance {
  /** The controller's id (older backends: the report's position). */
  id: string;
  name: string;
  /** Updates per second sent to it. */
  fps: number | null;
  /** Frames dropped on the way to it since the last report. */
  dropped: number;
  /** Average time from the engine handing over a frame until the controller had it (ms), when the backend reports it. */
  latencyMs: number | null;
}

export interface PerformanceSummary {
  /** Frames per second the lighting engine processes (screen capture or effects). */
  processingFps: number | null;
  /** Updates per second sent to the (first) LED controller. */
  ledFps: number | null;
  /** Frames dropped on the way to the (first) controller since the last report. */
  ledDropped: number;
  /** Every controller's LED report, in the backend's order. */
  controllers: ControllerPerformance[];
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
  const cpu = reports.find((r) => r.type === REPORT.CPU);
  const ram = reports.find((r) => r.type === REPORT.RAM);
  const temp = reports.find((r) => r.type === REPORT.TEMPERATURE);
  const cpuStat = cpu ? parse<{ total?: number }>(cpu.name) : null;
  const ramStat = ram ? parse<{ aspect?: number; takenMem?: number; totalPhysMem?: number }>(ram.name) : null;
  const fps = (r: PerformanceReport | null) => (r && r.param1 >= 0 ? Math.round(r.param1 * 10) / 10 : null);
  const controllers = reports
    .filter((r) => r.type === REPORT.LED && r.token > 0)
    .sort((a, b) => a.id - b.id)
    .map((r) => ({
      id: r.controller ?? String(r.id),
      name: r.controllerName ?? r.name,
      fps: fps(r),
      dropped: r.param4 > 0 ? r.param4 : 0,
      latencyMs: typeof r.latencyMs === 'number' && r.latencyMs >= 0 ? Math.round(r.latencyMs * 100) / 100 : null,
    }));
  return {
    processingFps: fps(instance),
    ledFps: controllers[0]?.fps ?? null,
    ledDropped: controllers[0]?.dropped ?? 0,
    controllers,
    cpuPercent: typeof cpuStat?.total === 'number' ? Math.round(cpuStat.total) : null,
    ram:
      ramStat && typeof ramStat.takenMem === 'number' && typeof ramStat.totalPhysMem === 'number'
        ? { usedMb: ramStat.takenMem, totalMb: ramStat.totalPhysMem, percent: ramStat.aspect ?? Math.round((100 * ramStat.takenMem) / ramStat.totalPhysMem) }
        : null,
    temperature: temp ? Number(temp.name) || null : null,
  };
}
