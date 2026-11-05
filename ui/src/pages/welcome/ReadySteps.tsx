import { useEffect, useMemo, useRef } from 'react';
import { Gamepad2, Sliders } from 'lucide-react';
import type { LayoutSpec, Profile } from '../../layout';
import { Button, Segmented, cx } from '../../components/ui';
import { LayoutLights, type LayoutLightsHandle } from '../../components/LayoutLights';
import { PresetPicker } from '../../control/PresetPicker';
import { buildLayoutView } from '../../live/layoutView';
import { LOOKS, type LookId } from '../../settings/intensity';
import { SIMPLE_ROLES, type RoleRow, type SimpleRole } from '../../setup/roles';
import { ProgramPicker } from '../layout/ProfileBar';
import { highlightFrame } from './ready';

/**
 * The room as Layout draws it: each strip where the user said it runs, in its live colors (else its Layout color),
 * with the light being chosen picked out.
 */
export function RoomPreview({ spec, frame, highlight }: { spec: LayoutSpec; frame: readonly number[] | null; highlight: string | null }) {
  const view = useMemo(() => buildLayoutView(spec), [spec]);
  const colors = useMemo(() => highlightFrame(spec, frame, highlight), [spec, frame, highlight]);
  const lights = useRef<LayoutLightsHandle>(null);
  useEffect(() => lights.current?.paint(colors), [colors, view]);
  return <LayoutLights ref={lights} view={view} label="Your lights in the room" />;
}

export interface RolesProps {
  spec: LayoutSpec;
  rows: readonly RoleRow[];
  /** The live LED colors (the layout as saved: each choice is saved at once), null before the first. */
  frame: readonly number[] | null;
  /** The monitor in words, null while unknown. */
  monitor: string | null;
  /** The light picked out in the picture. */
  selected: string | null;
  onSelect(stripId: string): void;
  onRole(row: RoleRow, role: SimpleRole): void;
  /** Screen edge for a strip on a WLED board: trace it (again). */
  onTrace(row: RoleRow): void;
}

/** What each light does: the room picture, then one row per strip or light with three choices. */
export function RolesStepBody(p: RolesProps) {
  return (
    <div className="space-y-4">
      <div className="space-y-1.5">
        <RoomPreview spec={p.spec} frame={p.frame} highlight={p.selected} />
        <p className="text-xs text-ink-faint">{p.monitor ? `Your screen: ${p.monitor}.` : 'Your screen’s size isn’t known: set it on Layout.'}</p>
      </div>
      <ul className="divide-y divide-line border-y border-line">
        {p.rows.map((r) => {
          const shown = SIMPLE_ROLES.find((x) => x.id === r.role);
          const picked = r.stripId === p.selected;
          const leds = p.spec.strips.find((s) => s.id === r.stripId)?.ledCount ?? 1;
          const hint =
            r.role === 'screen' && !r.trace && leds > 1
              ? 'Follows the part of the screen next to it: draw where it runs on Layout, so it matches.'
              : (shown?.description ?? 'Something else, set on Layout: pick one here to replace it.');
          return (
            <li
              key={r.key}
              className={cx('-mx-2 space-y-1.5 rounded-lg px-2 py-3', picked && 'bg-surface-2')}
              onFocusCapture={() => p.onSelect(r.stripId)}
              onPointerEnter={() => p.onSelect(r.stripId)}
            >
              <p className="text-sm font-medium">
                {r.label}
                {r.description && <span className="font-normal text-ink-faint"> · {r.description}</span>}
              </p>
              <div className="flex flex-wrap items-center gap-2">
                <Segmented<SimpleRole | ''>
                  label={`What ${r.label} shows`}
                  value={r.role ?? ''}
                  options={SIMPLE_ROLES.map((x) => ({ value: x.id, label: x.label }))}
                  onChange={(v) => v && p.onRole(r, v)}
                />
                {r.role === 'screen' && r.trace && (
                  <Button size="sm" variant="ghost" onClick={() => p.onTrace(r)}>
                    Trace its route
                  </Button>
                )}
              </div>
              <p className="text-xs text-ink-faint">{hint}</p>
            </li>
          );
        })}
      </ul>
      <p className="text-xs text-ink-faint">Music, an effect or a color picked on Effects plays on all your lights.</p>
    </div>
  );
}

export interface FeelProps {
  look: LookId | 'custom' | null;
  disabled: boolean;
  onLook(id: LookId): void;
  /** The presets (the defaults, or ones there were): those used while a program runs are listed. */
  presets: readonly Profile[];
  loadPrograms(): Promise<Array<{ exe: string; title: string }>>;
  /** Adds the program to Gaming (made again when it was deleted). */
  onAddGame(exe: string, title: string): void;
}

/** The Look: Home's five (the same control), then the game for the Gaming preset, optional. */
export function FeelStepBody(p: FeelProps) {
  const custom = <>You’ve fine-tuned these on Colors. Pick one to start over from it.</>;
  const withPrograms = p.presets.filter((x) => x.apps.length > 0);
  return (
    <div className="space-y-5">
      <section aria-labelledby="feel-look" className="space-y-2">
        <h2 id="feel-look" className="flex items-center gap-2 text-sm font-semibold">
          <Sliders size={15} className="text-ink-faint" aria-hidden /> Look
        </h2>
        <PresetPicker label="Look" presets={LOOKS} current={p.look} disabled={p.disabled} onPick={p.onLook} customHint={custom} />
      </section>
      <section aria-labelledby="feel-game" className="space-y-2 border-t border-line pt-4">
        <h2 id="feel-game" className="flex items-center gap-2 text-sm font-semibold">
          <Gamepad2 size={15} className="text-ink-faint" aria-hidden /> Your game <span className="font-normal text-ink-faint">· optional</span>
        </h2>
        <p className="text-xs text-ink-faint">
          The Gaming preset (screen edges with the Intense Look) is used by itself while your game runs. Start your game, then pick it here, or add it later on Presets.
        </p>
        {withPrograms.length > 0 && (
          <ul className="space-y-1 text-sm">
            {withPrograms.map((g) => (
              <li key={g.id}>
                {g.name || 'Untitled'} <span className="text-xs text-ink-faint">used while {g.apps.join(', ')} runs</span>
              </li>
            ))}
          </ul>
        )}
        <ProgramPicker load={p.loadPrograms} taken={withPrograms.flatMap((g) => g.apps)} onPick={p.onAddGame} label="Add your game to Gaming" />
      </section>
    </div>
  );
}
