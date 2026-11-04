import { ArrowDown, ArrowLeftRight, ArrowUp, CircleAlert, Lightbulb, PenLine, Plus, Shapes, Trash2, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { DEFAULT_BLEND, DEFAULT_DEPTH, litLength, shapePath, stripLedCount, type LayoutIssue, type LayoutSpec, type Section, type Strip } from '../../layout';
import { Button, cx, IconButton, SliderField, Stepper } from '../../components/ui';
import { Advanced, Select } from '../../components/form';
import type { EditorAction } from '../../editor/state';
import { DENSITIES, ledsForLength, pinUnevenness, unpinned } from '../../editor/strips';
import { hasAdjustedMapping, setStripShows, stripShows } from '../../editor/mapping';
import { RoleChooser } from './RoleChooser';
import type { Mode } from '../../control/modes';
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

function Group({ title, children }: { title?: string; children: ReactNode }) {
  return (
    <div className="space-y-2.5 py-5 first:pt-0 last:pb-0">
      {title && <h3 className="text-sm font-semibold">{title}</h3>}
      {children}
    </div>
  );
}

/** What the strip is plugged into: a choice of outputs when there are several, else the device's name. */
function PluggedInto({ spec, strip, dispatch }: { spec: LayoutSpec; strip: Strip; dispatch: (a: EditorAction) => void }) {
  const device = spec.controllers.find((c) => c.id === strip.controllerId);
  const output = device?.outputs.find((o) => o.index === strip.output);
  const several = spec.controllers.length > 1 || spec.controllers.some((c) => c.outputs.length > 1);
  if (!several) {
    return (
      <p className="text-[13px] text-ink-muted">
        Plugged into <span className="font-medium text-ink">{device?.name ?? 'Missing device'}</span>
        {device && !output && <span className="text-bad"> (missing output)</span>}
      </p>
    );
  }
  return (
    <Select
      label="Plugged into"
      className="max-w-xs"
      value={output ? `${strip.controllerId}::${strip.output}` : undefined}
      placeholder={device ? 'Missing output' : 'Missing device'}
      options={spec.controllers.flatMap((c) =>
        c.outputs.map((o) => ({
          value: `${c.id}::${o.index}`,
          label: `${spec.controllers.length > 1 ? `${c.name} · ` : ''}Output ${o.index + 1} (${o.length} LEDs)`,
        })),
      )}
      onChange={(v) => {
        const [controllerId, out] = v.split('::');
        dispatch({
          type: 'updateStrip',
          stripId: strip.id,
          patch: { controllerId, output: Number(out) },
        });
      }}
    />
  );
}

/** Place: where the selected strip runs, what it's plugged into, its LEDs and matching them with the real lights. */
export function PlaceEditor({
  spec,
  strip,
  issues,
  dispatch,
  onDraw,
  onMatch,
}: {
  spec: LayoutSpec;
  strip: Strip;
  issues: LayoutIssue[];
  dispatch: (a: EditorAction) => void;
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
  const index = spec.strips.findIndex((s) => s.id === strip.id);
  const forget = () => update({ path: unpinned(strip.path) });

  return (
    <section aria-label={`Settings of ${strip.name}`}>
      <div className="divide-y divide-line">
        <Group>
          {stripIssues.length > 0 && (
            <div className="space-y-1.5">
              {stripIssues.map((i, k) => (
                <IssueLine key={k} issue={i} />
              ))}
            </div>
          )}
          <PluggedInto spec={spec} strip={strip} dispatch={dispatch} />
          <div className="flex flex-wrap gap-1.5">
            <Button size="sm" icon={<PenLine size={14} />} onClick={onDraw}>
              Redraw
            </Button>
            <Button
              size="sm"
              icon={<ArrowLeftRight size={14} />}
              onClick={() => dispatch({ type: 'reverseStrip', stripId: strip.id })}
              title="The LEDs run the other way: the end becomes the start"
            >
              Reverse direction
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
        </Group>

        <Group title="LEDs">
          <Stepper
            label="Number of LEDs"
            value={count}
            min={1}
            max={Math.max(2000, output?.length ?? 0)}
            onChange={(n) => update({ ledCount: n }, `count-${strip.id}`)}
          />
          <p className="text-xs text-ink-faint">
            {length.toFixed(2)} m drawn
            {density > 0 ? ` · ${Math.round(density)} LEDs per meter` : ''}
            {output && output.length !== count && (
              <>
                {' · '}
                <button type="button" className="text-accent-strong hover:underline" onClick={() => update({ ledCount: output.length })}>
                  use all {output.length} LEDs of Output {output.index + 1}
                </button>
              </>
            )}
          </p>
        </Group>

        <Group title="Match with your lights">
          <p className="text-xs text-ink-faint">Light up LEDs one by one and mark the corners, so every LED sits where it really is.</p>
          <Button size="sm" icon={<Lightbulb size={14} />} onClick={onMatch}>
            Match with my lights{matched > 0 ? ` (${matched} matched)` : ''}
          </Button>
          {matched > 0 &&
            (pinUnevenness(room, strip) > 1.6 ? (
              <div className="space-y-2">
                <p className="text-xs text-warn">
                  The LEDs are spaced unevenly: {matched} matched point
                  {matched === 1 ? '' : 's'} no longer fit
                  {matched === 1 ? 's' : ''} the shape. Forget them to spread the LEDs evenly, then match with your lights again if you like.
                </p>
                <Button size="sm" variant="ghost" onClick={forget}>
                  Forget matched points (spread LEDs evenly)
                </Button>
              </div>
            ) : (
              <div>
                <Button size="sm" variant="ghost" className="-ml-3" onClick={forget}>
                  Forget matched points (spread LEDs evenly)
                </Button>
              </div>
            ))}
        </Group>
      </div>
      <div className="mt-5">
        <Advanced>
          <div className="space-y-2">
            <p className="text-sm font-medium">Count per meter</p>
            <p className="text-xs text-ink-faint">Set from LEDs per meter on your strip:</p>
            <div className="flex flex-wrap gap-1.5">
              {DENSITIES.map((d) => (
                <button
                  key={d}
                  type="button"
                  className="h-8 rounded-md border border-line-input px-2.5 text-xs text-ink-muted hover:border-line-strong hover:text-ink"
                  onClick={() => update({ ledCount: ledsForLength(room, strip.path, d) })}
                  title={`${ledsForLength(room, strip.path, d)} LEDs for ${length.toFixed(2)} m`}
                >
                  {d}/m
                </button>
              ))}
            </div>
          </div>
          {spec.strips.length > 1 && (
            <div className="space-y-2">
              <p className="text-sm font-medium">Order in the list</p>
              <div className="flex gap-1.5">
                <Button
                  size="sm"
                  variant="ghost"
                  icon={<ArrowUp size={14} />}
                  disabled={index <= 0}
                  onClick={() =>
                    dispatch({
                      type: 'moveStrip',
                      stripId: strip.id,
                      to: index - 1,
                    })
                  }
                >
                  Move up
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  icon={<ArrowDown size={14} />}
                  disabled={index >= spec.strips.length - 1}
                  onClick={() =>
                    dispatch({
                      type: 'moveStrip',
                      stripId: strip.id,
                      to: index + 1,
                    })
                  }
                >
                  Move down
                </Button>
              </div>
            </div>
          )}
        </Advanced>
      </div>
    </section>
  );
}

/** Customize: what the selected strip shows, its brightness, and (More options) turned-off LEDs, depth and softness. */
export function CustomizeEditor({
  spec,
  strip,
  issues,
  dispatch,
  effects,
  profileName,
  showing,
  onRoles,
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
  /** What the lights show now (null: unknown). */
  showing?: Mode | null;
  /** What the strip's sections do changed (`path` carries them in its points' `next`). */
  onRoles(path: Strip['path']): void;
}) {
  const { room } = spec;
  const count = stripLedCount(strip);
  const update = (patch: Partial<Strip>, coalesce?: string) => dispatch({ type: 'updateStrip', stripId: strip.id, patch, coalesce });
  const shows = stripShows(room, strip);
  const roleIssues = issues.filter((i) => i.stripId === strip.id && i.pointIndex !== undefined && /^Part /.test(i.message));
  const dark = strip.dark ?? [];
  const changed = (dark.length ? 1 : 0) + (strip.depth !== undefined ? 1 : 0) + (strip.blend !== undefined ? 1 : 0);
  const invalid = issues.some((i) => i.stripId === strip.id && /turns off LEDs|looks .* into the picture|softness of/.test(i.message));

  return (
    <section aria-label={`What ${strip.name} shows`}>
      <div className="divide-y divide-line">
        <Group title={profileName ? `What it shows in ${profileName}` : 'What it shows'}>
          {roleIssues.map((i, k) => (
            <IssueLine key={k} issue={i} />
          ))}
          {shows.shows === 'custom' && <p className="text-xs text-ink-faint">Its parts show different things. Pick one to use it for the whole strip.</p>}
          <RoleChooser
            value={shows.shows === 'screen' ? undefined : (shows.section ?? (shows.shows === 'custom' ? undefined : ({ kind: shows.shows } as Section)))}
            custom={shows.shows === 'custom'}
            effects={effects}
            showing={showing}
            onChange={(section) => onRoles(setStripShows(strip.path, section))}
          />
          {hasAdjustedMapping(strip) && (
            <Button size="sm" variant="ghost" className="-ml-3" onClick={() => onRoles(setStripShows(strip.path, undefined))}>
              Reset to automatic
            </Button>
          )}
        </Group>

        {profileName ? (
          <Group>
            <p className="text-xs text-ink-faint">Position, LEDs and turned-off LEDs are shared by all profiles. Switch to Everyday to change them.</p>
          </Group>
        ) : (
          <Group>
            <SliderField
              label="Brightness"
              value={strip.brightness ?? 1}
              min={0}
              max={1}
              step={0.01}
              scale={100}
              unit="%"
              onChange={(v) => update({ brightness: v >= 1 ? undefined : v }, `strip-brightness-${strip.id}`)}
            />
          </Group>
        )}
      </div>
      {!profileName && (
        <div className="mt-5">
          <Advanced changed={changed} invalid={invalid} onReset={() => update({ dark: undefined, depth: undefined, blend: undefined })}>
            <div className="space-y-2">
              <p className="text-sm font-medium">Turned-off LEDs</p>
              <p className="text-xs text-ink-faint">For example where the strip runs behind a stand.</p>
              {dark.map((r, i) => {
                const set = (start: number, end: number) => {
                  const next = dark.map((x, k) =>
                    k === i
                      ? {
                          start: start - 1,
                          count: Math.max(1, end - start + 1),
                        }
                      : x,
                  );
                  update({ dark: next }, `dark-${strip.id}-${i}`);
                };
                return (
                  <div key={i} className="flex items-center gap-2">
                    <Stepper
                      label="LEDs"
                      value={r.start + 1}
                      min={1}
                      max={count}
                      onChange={(v) => set(v, Math.max(v, r.start + r.count))}
                      className="flex-1"
                    />
                    <Stepper label="to" value={r.start + r.count} min={r.start + 1} max={count} onChange={(v) => set(r.start + 1, v)} />
                    <IconButton
                      label="Turn these LEDs on again"
                      onClick={() => {
                        const next = dark.filter((_, k) => k !== i);
                        update({ dark: next.length ? next : undefined });
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
                className="-ml-3"
                icon={<Plus size={14} />}
                onClick={() =>
                  update({
                    dark: [...dark, { start: 0, count: Math.min(5, count) }],
                  })
                }
              >
                Turn off a range of LEDs
              </Button>
            </div>
            <SliderField
              label="Depth"
              hint="How far into the screen each LED looks from its edge."
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
              hint="How wide each LED looks along the edge: softer blends neighbors for calmer light."
              value={strip.blend ?? DEFAULT_BLEND}
              min={0.5}
              max={8}
              step={0.1}
              minLabel="Sharp"
              maxLabel="Soft"
              onChange={(v) => update({ blend: v === DEFAULT_BLEND ? undefined : v }, `blend-${strip.id}`)}
            />
          </Advanced>
        </div>
      )}
    </section>
  );
}
