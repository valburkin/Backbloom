import { ArrowLeftRight, CircleAlert, Lightbulb, PenLine, Pin, Plus, Shapes, Trash2, TriangleAlert } from 'lucide-react';
import {
  DEFAULT_BLEND,
  DEFAULT_DEPTH,
  litLength,
  shapePath,
  stripLedCount,
  type LayoutIssue,
  type LayoutSpec,
  type Section,
  type Strip,
} from '../../layout';
import { Button, cx, IconButton, SliderField, Stepper } from '../../components/ui';
import { Advanced } from '../../components/form';
import type { EditorAction } from '../../editor/state';
import { DENSITIES, ledsForLength, pinUnevenness, unpinned } from '../../editor/strips';
import { hasAdjustedMapping, setStripShows, stripShows } from '../../editor/mapping';
import { RoleChooser } from './RoleChooser';
import { ShapeMenu } from './ShapeMenu';

function IssueLine({ issue }: { issue: LayoutIssue }) {
  const Icon = issue.severity === 'error' ? CircleAlert : TriangleAlert;
  return (
    <p className={cx('flex gap-1.5 text-xs', issue.severity === 'error' ? 'text-bad' : 'text-warn')}>
      <Icon size={14} className="mt-px shrink-0" />
      {issue.message}
    </p>
  );
}

function Heading({ children }: { children: string }) {
  return <h3 className="text-xs font-semibold uppercase tracking-wider text-ink-faint">{children}</h3>;
}

/** Everything about the selected strip: where it runs, its LEDs, what they show, dark parts, matching. */
export function StripEditor({
  spec,
  strip,
  issues,
  dispatch,
  effects,
  profileName,
  onRoles,
  onDraw,
  onMatch,
}: {
  spec: LayoutSpec;
  /** The strip as it is edited: with the profile's sections while a profile is edited. */
  strip: Strip;
  issues: LayoutIssue[];
  dispatch: (a: EditorAction) => void;
  /** Effect names the backend runs. */
  effects: readonly string[];
  /** Editing this profile: only what the strip shows can change (the rest is shared by every profile). */
  profileName?: string;
  /** What the strip's sections do changed (`path` carries them in its points' `next`). */
  onRoles(path: Strip['path']): void;
  onDraw(): void;
  onMatch(): void;
}) {
  const { room } = spec;
  const output = spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output);
  const count = stripLedCount(strip);
  const length = litLength(room, strip.path);
  const density = length > 0 ? count / length : 0;
  const update = (patch: Partial<Strip>, coalesce?: string) => dispatch({ type: 'updateStrip', stripId: strip.id, patch, coalesce });
  const stripIssues = issues.filter((i) => i.stripId === strip.id);
  const matched = strip.path.filter((p) => p.at !== undefined).length;
  const shows = stripShows(room, strip);


  return (
    <section aria-label={`Settings of ${strip.name}`} className="space-y-6">
      {stripIssues.length > 0 && (
        <div className="space-y-1.5 rounded-xl border border-line bg-bg/40 p-3">
          {stripIssues.map((i, k) => (
            <IssueLine key={k} issue={i} />
          ))}
        </div>
      )}

      <div className="space-y-2.5">
        <Heading>{profileName ? `What it shows in ${profileName}` : 'What it shows'}</Heading>
        <RoleChooser
          value={shows.shows === 'screen' ? undefined : shows.section ?? (shows.shows === 'custom' ? undefined : { kind: shows.shows } as Section)}
          custom={shows.shows === 'custom'}
          effects={effects}
          onChange={(section) => onRoles(setStripShows(strip.path, section))}
        />
        <p className="text-xs text-ink-faint">
          {shows.shows === 'screen'
            ? 'The colors show which LED repeats which part of the screen. Drag the dots on the screen’s edge to change it.'
            : shows.shows === 'whole'
              ? 'Every LED shows the average color of the whole picture, e.g. for soft light on the ceiling.'
              : shows.shows === 'effect'
                ? 'The strip plays this on its own, whatever the rest of your lights show. Strips with the same effect play it together.'
                : shows.shows === 'color'
                  ? 'The strip glows in this color, whatever the rest of your lights show.'
                  : shows.shows === 'none'
                    ? 'The strip stays dark.'
                    : 'Its parts do different things.'}
          {' '}Click a part of the strip to give just that part something else.
        </p>
        {hasAdjustedMapping(strip) && (
          <Button size="sm" variant="ghost" onClick={() => onRoles(setStripShows(strip.path, undefined))}>
            Back to automatic
          </Button>
        )}
      </div>

      {profileName ? (
        <p className="rounded-xl border border-line bg-bg/40 p-3 text-xs text-ink-muted">
          Where the strip runs, its LEDs and dark parts are the same in every profile. Switch to <b className="text-ink">Everyday</b> to change them.
        </p>
      ) : (
        <>
      <div className="space-y-2.5">
        <Heading>Where it runs</Heading>
        <p className="text-[13px] text-ink-muted">
          Drag the round handles in the room. Drag a <span className="font-semibold text-ink">+</span> to add a bend; right-click a handle to remove it.
        </p>
        <div className="flex flex-wrap gap-1.5">
          <Button size="sm" icon={<PenLine size={14} />} onClick={onDraw}>
            Draw again
          </Button>
          <Button size="sm" icon={<ArrowLeftRight size={14} />} onClick={() => dispatch({ type: 'reverseStrip', stripId: strip.id })} title="The LEDs run the other way: the end becomes the start">
            Reverse
          </Button>
          <ShapeMenu
            room={room}
            label="Shape"
            icon={<Shapes size={14} />}
            variant="secondary"
            onShape={(shape) => update({ path: shapePath(room, shape) })}
            onDraw={onDraw}
          />
        </div>
      </div>

      <div className="space-y-2.5">
        <Heading>LEDs</Heading>
        <Stepper label="Number of LEDs" value={count} min={1} max={Math.max(2000, output?.length ?? 0)} onChange={(n) => update({ ledCount: n }, `count-${strip.id}`)} />
        <p className="text-xs text-ink-faint">
          {length.toFixed(2)} m drawn{density > 0 ? ` · ${Math.round(density)} LEDs per meter` : ''}
          {output && output.length !== count && (
            <>
              {' · '}
              <button type="button" className="text-accent-strong hover:underline" onClick={() => update({ ledCount: output.length })}>
                use all {output.length} of the output
              </button>
            </>
          )}
        </p>
        <div className="flex flex-wrap items-center gap-1.5 text-xs text-ink-muted">
          <span>Count from the drawn length:</span>
          {DENSITIES.map((d) => (
            <button
              key={d}
              type="button"
              className="rounded-md border border-line px-2 py-0.5 hover:border-line-strong hover:text-ink"
              onClick={() => update({ ledCount: ledsForLength(room, strip.path, d) })}
              title={`${d} LEDs per meter`}
            >
              {d}/m
            </button>
          ))}
        </div>
        <Button size="sm" variant="primary" icon={<Lightbulb size={14} />} onClick={onMatch} className="w-full">
          Match with my lights{matched > 0 ? ` (${matched} matched)` : ''}
        </Button>
        <p className="text-xs text-ink-faint">Light up LEDs one at a time and show where the corners are. Then every LED lands where it really is.</p>
        {matched > 0 &&
          (pinUnevenness(room, strip) > 1.6 ? (
            <div className="space-y-2 rounded-xl border border-warn/30 bg-warn/10 p-3 text-xs text-warn">
              <p>
                The LEDs are spaced unevenly: {matched} point{matched === 1 ? ' is' : 's are'} pinned to LED numbers that no longer fit the shape. Unpin
                to spread them evenly, then match with your lights again if you like.
              </p>
              <Button size="sm" icon={<Pin size={14} />} onClick={() => update({ path: unpinned(strip.path) })}>
                Unpin all
              </Button>
            </div>
          ) : (
            <Button size="sm" variant="ghost" icon={<Pin size={14} />} onClick={() => update({ path: unpinned(strip.path) })}>
              Unpin all: spread the LEDs evenly again
            </Button>
          ))}
      </div>

      <div className="space-y-2.5">
        <Heading>Dark parts</Heading>
        <p className="text-xs text-ink-faint">LEDs that stay off, e.g. where the strip runs behind a stand.</p>
        {(strip.dark ?? []).map((r, i) => {
          const set = (start: number, end: number) => {
            const dark = (strip.dark ?? []).map((x, k) => (k === i ? { start: start - 1, count: Math.max(1, end - start + 1) } : x));
            update({ dark }, `dark-${strip.id}-${i}`);
          };
          return (
            <div key={i} className="flex items-center gap-2 rounded-lg border border-line bg-bg/40 p-2">
              <Stepper label="From LED" value={r.start + 1} min={1} max={count} onChange={(v) => set(v, Math.max(v, r.start + r.count))} className="flex-1" />
              <Stepper label="to" value={r.start + r.count} min={r.start + 1} max={count} onChange={(v) => set(r.start + 1, v)} />
              <IconButton
                label="Remove this dark part"
                onClick={() => {
                  const dark = (strip.dark ?? []).filter((_, k) => k !== i);
                  update({ dark: dark.length ? dark : undefined });
                }}
              >
                <Trash2 size={14} />
              </IconButton>
            </div>
          );
        })}
        <Button
          size="sm"
          variant="ghost"
          icon={<Plus size={14} />}
          onClick={() => update({ dark: [...(strip.dark ?? []), { start: 0, count: Math.min(5, count) }] })}
        >
          Add a dark part
        </Button>
      </div>

      <Advanced>
        <SliderField
          label="How far into the picture"
          hint="How deep each LED looks into the picture from its edge."
          value={strip.depth ?? DEFAULT_DEPTH}
          min={0.02}
          max={1}
          step={0.01}
          scale={100}
          unit="%"
          onChange={(v) => update({ depth: v === DEFAULT_DEPTH ? undefined : v }, `depth-${strip.id}`)}
        />
        <SliderField
          label="Softness"
          hint="How wide each LED looks along the edge: higher blends neighbors for calmer light."
          value={strip.blend ?? DEFAULT_BLEND}
          min={0.5}
          max={8}
          step={0.1}
          minLabel="Sharp"
          maxLabel="Soft"
          onChange={(v) => update({ blend: v === DEFAULT_BLEND ? undefined : v }, `blend-${strip.id}`)}
        />
      </Advanced>
        </>
      )}
    </section>
  );
}
