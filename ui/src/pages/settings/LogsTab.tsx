import { useEffect, useMemo, useRef, useState } from 'react';
import { ArrowDownToLine, Copy, Download, Eraser, Pause, Play, ScrollText, Search } from 'lucide-react';
import { cx, IconButton } from '../../components/ui';
import { Callout, Section, Select } from '../../components/form';
import type { PageSettings } from '../../app/config';
import { useToast } from '../../app/toast';
import { useLogStream } from '../../live/hooks';
import { filterLogs, formatLogs, formatTime, LOG_LEVELS, type LogLevel, type LogLine } from '../../live/logs';

type Obj = Record<string, unknown>;

const LEVEL_STYLE: Record<LogLevel, string> = {
  DEBUG: 'text-ink-faint',
  INFO: 'text-ink-muted',
  WARNING: 'text-warn',
  ERROR: 'text-bad',
};

const LEVEL_LABEL: Record<LogLevel, string> = { DEBUG: 'Debug', INFO: 'Info', WARNING: 'Warnings', ERROR: 'Errors' };

const LOGGER_LEVELS = [
  { value: 'silent', label: 'Errors only' },
  { value: 'warn', label: 'Warnings and errors' },
  { value: 'verbose', label: 'Everything except debug details' },
  { value: 'debug', label: 'Everything (for troubleshooting)' },
];

export function LogsTab({ ps }: { ps: PageSettings }) {
  const toast = useToast();
  const { logs, clear, error } = useLogStream(true);
  const [levels, setLevels] = useState<Set<LogLevel>>(() => new Set(LOG_LEVELS));
  const [query, setQuery] = useState('');
  const [paused, setPaused] = useState<LogLine[] | null>(null);
  const [follow, setFollow] = useState(true);
  const box = useRef<HTMLDivElement>(null);
  const source = paused ?? logs.lines;
  const shown = useMemo(() => filterLogs(source, levels, query), [source, levels, query]);
  const logger = ps.value<Obj>('logger');

  useEffect(() => {
    if (follow && box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [shown, follow]);

  const toggle = (l: LogLevel) =>
    setLevels((s) => {
      const n = new Set(s);
      if (n.has(l)) n.delete(l);
      else n.add(l);
      return n;
    });

  const text = () => formatLogs(shown);

  return (
    <>
      <Section
        title="Live log"
        icon={<ScrollText size={18} />}
        description="What Overglow is doing right now. Attach a download when you ask for help."
        actions={
          <div className="flex items-center gap-1">
            <IconButton label={paused ? 'Resume' : 'Pause'} onClick={() => setPaused((p) => (p ? null : logs.lines))}>
              {paused ? <Play size={16} /> : <Pause size={16} />}
            </IconButton>
            <IconButton label={follow ? 'Stop following new messages' : 'Follow new messages'} className={follow ? 'text-accent-strong' : ''} onClick={() => setFollow((f) => !f)}>
              <ArrowDownToLine size={16} />
            </IconButton>
            <IconButton
              label="Copy shown messages"
              onClick={() =>
                void navigator.clipboard?.writeText(text()).then(
                  () => toast('ok', 'Log copied'),
                  () => toast('bad', 'Couldn’t copy', 'Your browser blocked clipboard access. Use Download instead.'),
                )
              }
            >
              <Copy size={16} />
            </IconButton>
            <IconButton
              label="Download shown messages"
              onClick={() => {
                const url = URL.createObjectURL(new Blob([text()], { type: 'text/plain' }));
                const a = document.createElement('a');
                a.href = url;
                a.download = `overglow-log-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.txt`;
                a.click();
                setTimeout(() => URL.revokeObjectURL(url), 1000);
              }}
            >
              <Download size={16} />
            </IconButton>
            <IconButton label="Clear" onClick={clear}>
              <Eraser size={16} />
            </IconButton>
          </div>
        }
      >
        {error && <Callout tone="bad">Couldn’t start the log: {error}</Callout>}
        <div className="flex flex-wrap items-center gap-2">
          <div role="group" aria-label="Show levels" className="flex flex-wrap gap-1">
            {LOG_LEVELS.map((l) => (
              <button
                key={l}
                type="button"
                aria-pressed={levels.has(l)}
                onClick={() => toggle(l)}
                className={cx(
                  'h-8 rounded-lg border px-2.5 text-xs font-medium transition-colors',
                  levels.has(l) ? 'border-line-strong bg-surface-3' : 'border-line text-ink-faint line-through',
                  levels.has(l) && (l === 'DEBUG' ? 'text-ink' : LEVEL_STYLE[l]),
                )}
              >
                {LEVEL_LABEL[l]}
              </button>
            ))}
          </div>
          <label className="relative ml-auto block">
            <span className="sr-only">Filter messages</span>
            <Search size={15} className="pointer-events-none absolute left-2.5 top-2 text-ink-faint" />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Filter"
              className="h-8 w-52 rounded-lg border border-line bg-bg pl-8 pr-2 text-[13px] focus:border-accent"
            />
          </label>
        </div>
        <div
          ref={box}
          role="log"
          aria-live="off"
          aria-label="Log messages"
          tabIndex={0}
          onScroll={(e) => {
            const el = e.currentTarget;
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
            if (atBottom !== follow) setFollow(atBottom);
          }}
          className="scroll-thin h-[28rem] overflow-auto rounded-xl border border-line bg-bg p-3 font-mono text-xs leading-relaxed"
        >
          {shown.length === 0 ? (
            <p className="text-ink-faint">{logs.lines.length ? 'No messages match the filter.' : 'Waiting for messages…'}</p>
          ) : (
            shown.map((l) => (
              <div key={l.id} className="flex gap-2 whitespace-pre-wrap break-words">
                <span className="shrink-0 text-ink-faint">{formatTime(l.time)}</span>
                <span className={cx('w-14 shrink-0', LEVEL_STYLE[l.level])}>{l.level === 'WARNING' ? 'WARN' : l.level}</span>
                {l.source && <span className="shrink-0 text-accent-strong/80">{l.source}</span>}
                <span className={cx('min-w-0', l.level === 'ERROR' ? 'text-bad' : l.level === 'WARNING' ? 'text-warn' : 'text-ink')}>
                  {l.location && <span className="text-ink-faint">({l.location}) </span>}
                  {l.message}
                </span>
              </div>
            ))
          )}
        </div>
        <p className="text-xs text-ink-faint">
          {shown.length} of {logs.lines.length} messages{paused ? ' · paused' : ''}
        </p>
      </Section>
      <Section title="How much to log" icon={<ScrollText size={18} />}>
        <Select
          label="Log detail"
          hint="More detail helps troubleshooting but writes more. “Warnings and errors” is fine for everyday use."
          value={String(logger.level ?? 'warn')}
          options={LOGGER_LEVELS}
          onChange={(v) => ps.setPath('logger', ['level'], v)}
        />
      </Section>
    </>
  );
}
