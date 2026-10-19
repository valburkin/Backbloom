import type { LogMessage } from '../api/types';

export type LogLevel = 'DEBUG' | 'INFO' | 'WARNING' | 'ERROR';
export const LOG_LEVELS: readonly LogLevel[] = ['DEBUG', 'INFO', 'WARNING', 'ERROR'];

export interface LogLine {
  id: number;
  time: number;
  level: LogLevel;
  source: string;
  message: string;
  location?: string;
}

export interface LogState {
  lines: LogLine[];
  nextId: number;
}

export const MAX_LOG_LINES = 2000;
const REPORT_TOKEN = '<Report>';

export function normalizeLevel(s: string): LogLevel {
  const u = s.toUpperCase();
  if (u.startsWith('WARN')) return 'WARNING';
  if (u.startsWith('ERR') || u === 'CRITICAL') return 'ERROR';
  if (u === 'DEBUG') return 'DEBUG';
  return 'INFO';
}

/** Appends the messages of one `logging-update` event, keeping the newest MAX_LOG_LINES. */
export function appendLogs(state: LogState, data: unknown, max = MAX_LOG_LINES): LogState {
  const messages = (data as { messages?: unknown } | null)?.messages;
  if (!Array.isArray(messages) || !messages.length) return state;
  let id = state.nextId;
  const added: LogLine[] = (messages as LogMessage[]).map((m) => {
    const raw = String(m.message ?? '');
    const report = raw.startsWith(REPORT_TOKEN);
    const level = normalizeLevel(String(m.levelString ?? 'INFO'));
    return {
      id: id++,
      time: Number(m.utime) || Date.now(),
      level,
      source: report ? '' : String(m.loggerName ?? '').trim(),
      message: report ? raw.slice(REPORT_TOKEN.length) : raw,
      location: level === 'DEBUG' && m.fileName ? `${m.fileName}:${m.line ?? ''}` : undefined,
    };
  });
  const lines = state.lines.length + added.length > max ? [...state.lines, ...added].slice(-max) : [...state.lines, ...added];
  return { lines, nextId: id };
}

export function filterLogs(lines: readonly LogLine[], levels: ReadonlySet<LogLevel>, query: string): LogLine[] {
  const q = query.trim().toLowerCase();
  return lines.filter((l) => levels.has(l.level) && (!q || l.message.toLowerCase().includes(q) || l.source.toLowerCase().includes(q)));
}

function pad(n: number, w = 2): string {
  return String(n).padStart(w, '0');
}

export function formatTime(ms: number): string {
  const d = new Date(ms);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/** Plain-text export (copy / download), one line per message, local time. */
export function formatLogs(lines: readonly LogLine[]): string {
  return lines
    .map((l) => {
      const d = new Date(l.time);
      const date = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
      return `${date} ${formatTime(l.time)} ${l.level.padEnd(7)} ${l.source ? `[${l.source}] ` : ''}${l.location ? `(${l.location}) ` : ''}${l.message}`;
    })
    .join('\n');
}
