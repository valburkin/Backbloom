import { ArrowLeftRight, ChevronDown, ChevronUp, CircleAlert, Plus, Trash2, TriangleAlert } from 'lucide-react';
import type { Edge, LayoutIssue, Segment, Strip } from '../../layout';
import { Button, cx, IconButton, Segmented, SliderField, Stepper } from '../../components/ui';
import {
  convertSegment,
  DEFAULT_BLEND,
  describeSegment,
  EDGE_LABELS,
  KIND_LABELS,
  newEdgeSegment,
  type SegmentKind,
} from '../../editor/segments';
import type { EditorAction } from '../../editor/state';

const KIND_OPTIONS = (Object.keys(KIND_LABELS) as SegmentKind[]).map((k) => ({ value: k, label: KIND_LABELS[k] }));
const EDGE_OPTIONS = (['top', 'right', 'bottom', 'left'] as Edge[]).map((e) => ({ value: e, label: EDGE_LABELS[e] }));

function IssueLine({ issue }: { issue: LayoutIssue }) {
  const Icon = issue.severity === 'error' ? CircleAlert : TriangleAlert;
  return (
    <p className={cx('flex gap-1.5 text-xs', issue.severity === 'error' ? 'text-bad' : 'text-warn')}>
      <Icon size={14} className="mt-px shrink-0" />
      {issue.message}
    </p>
  );
}

function SegmentEditor({
  segment,
  onChange,
}: {
  segment: Segment;
  /** `field` drives undo coalescing so a slider drag is a single undo step. */
  onChange: (next: Segment, field: string) => void;
}) {
  return (
    <div className="space-y-4">
      <div className="space-y-1.5">
        <span className="text-[13px] font-medium">What these LEDs show</span>
        <div>
          <Segmented label="Segment type" value={segment.kind} options={KIND_OPTIONS} size="sm" onChange={(k) => onChange(convertSegment(segment, k), 'kind')} />
        </div>
      </div>

      <Stepper label="Number of LEDs" value={segment.count} min={1} max={2000} onChange={(count) => onChange({ ...segment, count }, 'count')} />

      {segment.kind === 'edge' && <EdgeFields segment={segment} onChange={onChange} />}

      {segment.kind === 'fixed' && (
        <div className="space-y-3">
          <p className="text-xs text-ink-faint">All LEDs in this part show the average color of one area of the screen.</p>
          {(
            [
              ['hmin', 'Area left side'],
              ['hmax', 'Area right side'],
              ['vmin', 'Area top side'],
              ['vmax', 'Area bottom side'],
            ] as const
          ).map(([k, label]) => (
            <SliderField
              key={k}
              label={label}
              value={segment.area[k]}
              min={0}
              max={1}
              step={0.005}
              scale={100}
              unit="%"
              onChange={(v) => onChange({ ...segment, area: { ...segment.area, [k]: v } }, `area-${k}`)}
            />
          ))}
        </div>
      )}

      {segment.kind === 'off' && (
        <p className="text-xs text-ink-faint">
          These LEDs stay dark — handy where the strip goes around a corner, behind a stand, or out of sight.
        </p>
      )}
    </div>
  );
}

function EdgeFields({ segment, onChange }: { segment: Extract<Segment, { kind: 'edge' }>; onChange: (s: Segment, f: string) => void }) {
  const horizontal = segment.edge === 'top' || segment.edge === 'bottom';
  const [lo, hi] = horizontal ? ['Left end', 'Right end'] : ['Top end', 'Bottom end'];
  return (
    <>
      <div className="space-y-1.5">
        <span className="text-[13px] font-medium">Screen edge</span>
        <div>
          <Segmented label="Screen edge" value={segment.edge} options={EDGE_OPTIONS} size="sm" onChange={(edge) => onChange({ ...segment, edge }, 'edge')} />
        </div>
      </div>
      <SliderField
        label="Starts at"
        hint="Where the first LED of this part sits along the edge."
        value={segment.from}
        min={-0.1}
        max={1.1}
        step={0.005}
        scale={100}
        unit="%"
        minLabel={lo}
        maxLabel={hi}
        onChange={(from) => onChange({ ...segment, from }, 'from')}
      />
      <SliderField
        label="Ends at"
        value={segment.to}
        min={-0.1}
        max={1.1}
        step={0.005}
        scale={100}
        unit="%"
        minLabel={lo}
        maxLabel={hi}
        onChange={(to) => onChange({ ...segment, to }, 'to')}
      />
      <Button size="sm" icon={<ArrowLeftRight size={15} />} onClick={() => onChange({ ...segment, from: segment.to, to: segment.from }, 'reverse')}>
        Reverse direction
      </Button>
      <SliderField
        label="How far into the screen"
        hint="How much of the picture next to the edge each LED looks at."
        value={segment.depth}
        min={0.01}
        max={0.5}
        step={0.005}
        scale={100}
        unit="%"
        minLabel="Just the edge"
        maxLabel="Deep"
        onChange={(depth) => onChange({ ...segment, depth }, 'depth')}
      />
      <SliderField
        label="Smoothness"
        hint="Higher values blend neighboring LEDs for softer transitions."
        value={segment.blend ?? DEFAULT_BLEND}
        min={0.5}
        max={3}
        step={0.05}
        minLabel="Sharp"
        maxLabel="Smooth"
        onChange={(blend) => onChange({ ...segment, blend }, 'blend')}
      />
    </>
  );
}

export function SegmentPanel({
  strip,
  selectedIndex,
  issues,
  dispatch,
}: {
  strip: Strip;
  selectedIndex: number | null;
  issues: LayoutIssue[];
  dispatch: (a: EditorAction) => void;
}) {
  const stripIssues = issues.filter((i) => i.stripId === strip.id && i.segmentIndex === undefined);
  return (
    <section aria-labelledby="segments-title" className="space-y-2">
      <div className="flex items-center justify-between">
        <h2 id="segments-title" className="text-xs font-semibold uppercase tracking-wider text-ink-faint">
          Parts of “{strip.name}” <span className="font-normal normal-case tracking-normal">· in wiring order</span>
        </h2>
      </div>
      {stripIssues.map((i, k) => (
        <IssueLine key={k} issue={i} />
      ))}
      {strip.segments.length === 0 && (
        <p className="rounded-xl border border-dashed border-line p-4 text-[13px] text-ink-muted">
          This strip has no parts yet. Add one below, or use “Start from template”.
        </p>
      )}
      <ol className="space-y-2">
        {strip.segments.map((seg, i) => {
          const open = selectedIndex === i;
          const segIssues = issues.filter((x) => x.stripId === strip.id && x.segmentIndex === i);
          const hasError = segIssues.some((x) => x.severity === 'error');
          return (
            <li key={i} className={cx('rounded-xl border bg-surface-2', open ? 'border-line-strong' : 'border-line')}>
              <div className="flex items-center gap-2 p-2 pl-3">
                <button
                  type="button"
                  aria-expanded={open}
                  onClick={() => dispatch({ type: 'selectSegment', index: open ? null : i })}
                  className="flex min-w-0 flex-1 items-center gap-2.5 text-left"
                >
                  <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md bg-surface-3 text-xs font-semibold tabular-nums text-ink-muted">
                    {i + 1}
                  </span>
                  <span className="min-w-0 truncate text-[13px]">{describeSegment(seg)}</span>
                  {segIssues.length > 0 && (
                    <CircleAlert size={15} className={cx('shrink-0', hasError ? 'text-bad' : 'text-warn')} aria-label={`${segIssues.length} issue(s)`} />
                  )}
                </button>
                <IconButton label="Move part up" className="h-7 w-7" disabled={i === 0} onClick={() => dispatch({ type: 'moveSegment', stripId: strip.id, from: i, to: i - 1 })}>
                  <ChevronUp size={15} />
                </IconButton>
                <IconButton
                  label="Move part down"
                  className="h-7 w-7"
                  disabled={i === strip.segments.length - 1}
                  onClick={() => dispatch({ type: 'moveSegment', stripId: strip.id, from: i, to: i + 1 })}
                >
                  <ChevronDown size={15} />
                </IconButton>
                <IconButton label="Remove part" className="h-7 w-7 hover:text-bad" onClick={() => dispatch({ type: 'removeSegment', stripId: strip.id, index: i })}>
                  <Trash2 size={14} />
                </IconButton>
              </div>
              {open && (
                <div className="space-y-3 border-t border-line p-4">
                  {segIssues.map((x, k) => (
                    <IssueLine key={k} issue={x} />
                  ))}
                  <SegmentEditor
                    segment={seg}
                    onChange={(next, field) =>
                      dispatch({ type: 'updateSegment', stripId: strip.id, index: i, segment: next, coalesce: `seg-${strip.id}-${i}-${field}` })
                    }
                  />
                </div>
              )}
            </li>
          );
        })}
      </ol>
      <Button
        size="sm"
        variant="ghost"
        icon={<Plus size={15} />}
        onClick={() => {
          const last = strip.segments[strip.segments.length - 1];
          dispatch({ type: 'addSegment', stripId: strip.id, segment: newEdgeSegment(10, last?.kind === 'edge' ? last.edge : 'bottom') });
        }}
      >
        Add part
      </Button>
    </section>
  );
}
