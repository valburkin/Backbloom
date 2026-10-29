import type { CSSProperties } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { stripLedCount, type LayoutIssue, type LayoutSpec, type Section, type ShapeId, type Strip } from '../../layout';
import { roleLabel, stripShows } from '../../editor/mapping';
import { Badge, Button, cx, IconButton } from '../../components/ui';
import { stripColor } from '../../editor/colors';
import type { EditorAction } from '../../editor/state';
import { ShapeMenu } from './ShapeMenu';

export function outputOf(spec: LayoutSpec, strip: Strip) {
  return spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output);
}

/** The strip's LED count, as a badge only when it needs attention (not connected, unused or too many LEDs). */
export function CountBadge({ count, length }: { count: number; length: number | undefined }) {
  if (length === undefined) return <Badge tone="bad">Not connected</Badge>;
  if (count === length) return <span className="text-xs text-ink-faint">{count} LEDs</span>;
  const diff = Math.abs(count - length);
  return (
    <Badge tone="warn" className="whitespace-nowrap">
      {count} / {length} · {diff} {count < length ? 'unused' : 'too many'}
    </Badge>
  );
}

/** The strips as a list to pick one from; in Place also adding and removing them. */
export function StripPanel({
  spec,
  selectedId,
  issues,
  dispatch,
  onAdd,
  onAddDrawn,
  customize,
  rolesOnly,
}: {
  spec: LayoutSpec;
  selectedId: string | null;
  issues: LayoutIssue[];
  dispatch: (a: EditorAction) => void;
  /** Add a strip of a ready-made shape. */
  onAdd(shape: ShapeId): void;
  /** Add a strip and draw its path. */
  onAddDrawn(): void;
  /** Customize mode: what each strip shows is listed; strips aren't added or removed here. */
  customize?: boolean;
  /** A profile is edited: only what strips show can change. */
  rolesOnly?: boolean;
}) {
  const editable = !customize && !rolesOnly;

  return (
    <section aria-labelledby="strips-title" className="space-y-2">
      <div className="flex min-h-8 items-center justify-between">
        <h2 id="strips-title" className="text-sm font-semibold">
          Strips
        </h2>
        {editable && spec.controllers.length > 0 && <ShapeMenu room={spec.room} label="Add strip" icon={<Plus size={15} />} onShape={onAdd} onDraw={onAddDrawn} />}
      </div>
      {spec.controllers.length === 0 ? (
        <div className="space-y-2 rounded-xl border border-dashed border-line p-4">
          <p className="text-[13px] text-ink-muted">Add a device first.</p>
          <Button size="sm" onClick={() => (window.location.hash = '#/devices')}>
            Open Devices
          </Button>
        </div>
      ) : (
        spec.strips.length === 0 && (
          <p className="rounded-xl border border-dashed border-line p-4 text-[13px] text-ink-muted">
            No strips yet. Add one from a shape, or draw it in the room.
          </p>
        )
      )}
      <ul className="space-y-1.5">
        {spec.strips.map((strip, i) => {
          const selected = strip.id === selectedId;
          const count = stripLedCount(strip);
          const errors = issues.filter((x) => x.stripId === strip.id && x.severity === 'error').length;
          const color = stripColor(i);
          const { shows, section } = stripShows(spec.room, strip);
          const role = shows === 'screen' ? null : shows === 'custom' ? 'Mixed' : roleLabel(section ?? ({ kind: shows } as Section));
          return (
            <li key={strip.id}>
              <div
                className={cx(
                  'flex items-center gap-3 rounded-xl border px-3 py-2 transition-colors',
                  selected ? 'border-line-strong bg-surface-2 ring-1 ring-inset' : 'border-line hover:border-line-strong',
                )}
                style={selected ? ({ '--tw-ring-color': `${color}66` } as CSSProperties) : undefined}
              >
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
                  {selected && editable ? (
                    <input
                      aria-label="Strip name"
                      value={strip.name}
                      onChange={(e) => dispatch({ type: 'updateStrip', stripId: strip.id, patch: { name: e.target.value }, coalesce: `strip-name-${strip.id}` })}
                      className="-mx-1 w-full rounded-md bg-transparent px-1 text-sm font-medium hover:bg-surface-3 focus:bg-surface-3"
                    />
                  ) : (
                    <p className="cursor-pointer truncate text-sm font-medium">{strip.name}</p>
                  )}
                  <div className="mt-0.5 flex flex-wrap items-center gap-1.5">
                    {customize ? (
                      <span className="text-xs text-ink-faint">{role ?? 'Screen edge'}</span>
                    ) : (
                      <>
                        <CountBadge count={count} length={outputOf(spec, strip)?.length} />
                        {spec.controllers.length > 1 && <span className="truncate text-xs text-ink-faint">· {spec.controllers.find((c) => c.id === strip.controllerId)?.name}</span>}
                      </>
                    )}
                    {customize && strip.brightness !== undefined && strip.brightness < 1 && <span className="text-xs text-ink-faint">· {Math.round(strip.brightness * 100)}%</span>}
                    {errors > 0 && <Badge tone="bad">{errors} problem{errors === 1 ? '' : 's'}</Badge>}
                  </div>
                </div>
                {selected && editable && (
                  <IconButton label={`Remove ${strip.name}`} onClick={() => dispatch({ type: 'removeStrip', stripId: strip.id })} className="shrink-0 hover:text-bad">
                    <Trash2 size={15} />
                  </IconButton>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
