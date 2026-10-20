import type { CSSProperties } from 'react';
import { ChevronDown, ChevronUp, Plus, Trash2 } from 'lucide-react';
import { stripLedCount, type LayoutIssue, type LayoutSpec, type Strip } from '../../layout';
import { Badge, Button, cx, IconButton } from '../../components/ui';
import { stripColor } from '../../editor/colors';
import type { EditorAction } from '../../editor/state';

function outputOf(spec: LayoutSpec, strip: Strip) {
  return spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output);
}

export function CountBadge({ count, length }: { count: number; length: number | undefined }) {
  if (length === undefined) return <Badge tone="bad">No output</Badge>;
  if (count === length) return <Badge tone="ok">{count} / {length} LEDs</Badge>;
  const diff = Math.abs(count - length);
  return (
    <Badge tone="warn" className="whitespace-nowrap">
      {count} / {length} · {diff} {count < length ? 'missing' : 'too many'}
    </Badge>
  );
}

export function StripPanel({
  spec,
  selectedId,
  issues,
  dispatch,
}: {
  spec: LayoutSpec;
  selectedId: string | null;
  issues: LayoutIssue[];
  dispatch: (a: EditorAction) => void;
}) {
  const multiTarget = spec.controllers.length > 1 || spec.controllers.some((c) => c.outputs.length > 1);

  return (
    <section aria-labelledby="strips-title" className="space-y-2">
      <div className="flex items-center justify-between">
        <h2 id="strips-title" className="text-xs font-semibold uppercase tracking-wider text-ink-faint">
          Strips
        </h2>
        <Button size="sm" variant="ghost" icon={<Plus size={15} />} disabled={!spec.controllers.length} onClick={() => dispatch({ type: 'addStrip' })}>
          Add strip
        </Button>
      </div>
      {spec.strips.length === 0 && (
        <p className="rounded-xl border border-dashed border-line p-4 text-[13px] text-ink-muted">
          {spec.controllers.length ? 'No strips yet. Add one, or start from a template.' : 'Add a controller on the Devices page first.'}
        </p>
      )}
      <ul className="space-y-2">
        {spec.strips.map((strip, i) => {
          const selected = strip.id === selectedId;
          const out = outputOf(spec, strip);
          const count = stripLedCount(strip);
          const stripIssues = issues.filter((x) => x.stripId === strip.id);
          const errors = stripIssues.filter((x) => x.severity === 'error').length;
          const color = stripColor(i);
          return (
            <li key={strip.id}>
              <div
                className={cx(
                  'group rounded-xl border bg-surface-2 transition-colors',
                  selected ? 'border-line-strong ring-1 ring-inset' : 'border-line hover:border-line-strong',
                )}
                style={selected ? ({ '--tw-ring-color': `${color}66` } as CSSProperties) : undefined}
              >
                <div className="flex items-center gap-3 p-3">
                  <button
                    type="button"
                    aria-pressed={selected}
                    aria-label={`Select ${strip.name}`}
                    onClick={() => dispatch({ type: 'selectStrip', stripId: strip.id })}
                    className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-bold text-[#0b0d12]"
                    style={{ background: color }}
                  >
                    {i + 1}
                  </button>
                  <div className="min-w-0 flex-1" onClick={() => !selected && dispatch({ type: 'selectStrip', stripId: strip.id })}>
                    {selected ? (
                      <input
                        aria-label="Strip name"
                        value={strip.name}
                        onChange={(e) => dispatch({ type: 'updateStrip', stripId: strip.id, patch: { name: e.target.value }, coalesce: `strip-name-${strip.id}` })}
                        className="w-full rounded-md bg-transparent px-1 -mx-1 text-sm font-medium hover:bg-surface-3 focus:bg-surface-3"
                      />
                    ) : (
                      <p className="cursor-pointer truncate text-sm font-medium">{strip.name}</p>
                    )}
                    <div className="mt-1 flex flex-wrap items-center gap-1.5">
                      <CountBadge count={count} length={out?.length} />
                      {spec.controllers.length > 1 && !selected && (
                        <span className="truncate text-xs text-ink-faint">{spec.controllers.find((c) => c.id === strip.controllerId)?.name}</span>
                      )}
                      {strip.brightness !== undefined && strip.brightness < 1 && <Badge>{Math.round(strip.brightness * 100)} %</Badge>}
                      {errors > 0 && <Badge tone="bad">{errors} problem{errors === 1 ? '' : 's'}</Badge>}
                    </div>
                  </div>
                  {selected && (
                    <div className="flex shrink-0 flex-col">
                      <IconButton label="Move strip up" className="h-6 w-6" disabled={i === 0} onClick={() => dispatch({ type: 'moveStrip', stripId: strip.id, to: i - 1 })}>
                        <ChevronUp size={15} />
                      </IconButton>
                      <IconButton
                        label="Move strip down"
                        className="h-6 w-6"
                        disabled={i === spec.strips.length - 1}
                        onClick={() => dispatch({ type: 'moveStrip', stripId: strip.id, to: i + 1 })}
                      >
                        <ChevronDown size={15} />
                      </IconButton>
                    </div>
                  )}
                </div>
                {selected && (
                  <div className="flex items-center gap-2 border-t border-line px-3 py-2">
                    {multiTarget ? (
                      <label className="flex min-w-0 flex-1 items-center gap-2 text-xs text-ink-muted">
                        Wired to
                        <select
                          className="h-7 min-w-0 flex-1 rounded-md border border-line bg-bg px-2 text-xs text-ink"
                          value={`${strip.controllerId}::${strip.output}`}
                          onChange={(e) => {
                            const [controllerId, output] = e.target.value.split('::');
                            dispatch({ type: 'updateStrip', stripId: strip.id, patch: { controllerId, output: Number(output) } });
                          }}
                        >
                          {!out && <option value={`${strip.controllerId}::${strip.output}`}>Unknown output</option>}
                          {spec.controllers.flatMap((c) =>
                            c.outputs.map((o) => (
                              <option key={`${c.id}::${o.index}`} value={`${c.id}::${o.index}`}>
                                {spec.controllers.length > 1 ? `${c.name} · ` : ''}Output {o.index + 1} ({o.length} LEDs)
                              </option>
                            )),
                          )}
                        </select>
                      </label>
                    ) : (
                      <span className="flex-1 truncate text-xs text-ink-muted">
                        {spec.controllers.find((c) => c.id === strip.controllerId)?.name ?? 'Unknown controller'}
                      </span>
                    )}
                    <IconButton
                      label={`Remove ${strip.name}`}
                      onClick={() => dispatch({ type: 'removeStrip', stripId: strip.id })}
                      className="hover:text-bad"
                    >
                      <Trash2 size={15} />
                    </IconButton>
                  </div>
                )}
                {selected && (
                  <label className="flex items-center gap-2 border-t border-line px-3 py-2 text-xs text-ink-muted">
                    Brightness
                    <input
                      type="range"
                      aria-label={`Brightness of ${strip.name}`}
                      className="min-w-0 flex-1"
                      min={0}
                      max={100}
                      value={Math.round((strip.brightness ?? 1) * 100)}
                      onChange={(e) => {
                        const v = Number(e.target.value) / 100;
                        dispatch({ type: 'updateStrip', stripId: strip.id, patch: { brightness: v >= 1 ? undefined : v }, coalesce: `strip-brightness-${strip.id}` });
                      }}
                    />
                    <span className="w-10 text-right tabular-nums text-ink">{Math.round((strip.brightness ?? 1) * 100)} %</span>
                  </label>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
