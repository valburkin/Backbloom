import { Ban, Palette, Scissors, Sparkles, SquareDashed, Tv } from 'lucide-react';
import type { ReactNode } from 'react';
import type { Rgb, Section } from '../../layout';
import { ChoiceCard, CHOICE_ACTIVE, cx } from '../../components/ui';
import { Select } from '../../components/form';
import { SWATCHES } from '../../components/ColorPicker';
import { effectLabel, isMusicEffect } from '../../control/modes';

/** The roles a strip or a part of it can have, as the editor offers them. */
export type RoleChoice = 'screen' | 'whole' | 'effect' | 'color' | 'none' | 'gap';

export const ROLE_CHOICES: ReadonlyArray<{ id: RoleChoice; label: string; description: string; icon: (size: number) => ReactNode }> = [
  { id: 'screen', label: 'Screen edge', description: 'Follows the part of the screen next to it', icon: (s) => <Tv size={s} /> },
  { id: 'whole', label: 'Screen average', description: 'The average color of the whole screen, for soft light', icon: (s) => <SquareDashed size={s} /> },
  { id: 'effect', label: 'Effect', description: 'Plays an effect or reacts to music on its own', icon: (s) => <Sparkles size={s} /> },
  { id: 'color', label: 'Color', description: 'Glows in one color of its own', icon: (s) => <Palette size={s} /> },
  { id: 'none', label: 'Off', description: 'These LEDs stay dark', icon: (s) => <Ban size={s} /> },
  { id: 'gap', label: 'Gap', description: 'No LEDs here: the strip is cut and joined with wire', icon: (s) => <Scissors size={s} /> },
];

const DEFAULT_COLOR: Rgb = [255, 140, 40];

/** The choice a section stands for. */
export function choiceOf(section: Section | undefined): RoleChoice {
  if (!section || section.kind === 'edge') return 'screen';
  return section.kind === 'area' ? 'whole' : section.kind;
}

/** The section a choice makes (undefined = the screen next to it), keeping the current effect or color. */
export function sectionFor(choice: RoleChoice, current: Section | undefined, effects: readonly string[]): Section | undefined {
  switch (choice) {
    case 'screen':
      return undefined;
    case 'effect':
      return current?.kind === 'effect' ? current : { kind: 'effect', effect: effects.find(isMusicEffect) ?? effects[0] ?? 'Candle' };
    case 'color':
      return current?.kind === 'color' ? current : { kind: 'color', color: DEFAULT_COLOR };
    default:
      return { kind: choice };
  }
}

/**
 * What a strip or a part shows, with the effect or color to pick under it when one of those is chosen. `value` is the
 * current section (undefined = the screen next to it); `custom` marks a strip whose parts differ. `compact` is the
 * one-row version for the popup on the canvas; otherwise each role is a card with a line of explanation.
 */
export function RoleChooser({
  value,
  custom,
  effects,
  withGap,
  compact,
  onChange,
}: {
  value: Section | undefined;
  custom?: boolean;
  /** Effect names the backend runs. */
  effects: readonly string[];
  withGap?: boolean;
  compact?: boolean;
  onChange(section: Section | undefined): void;
}) {
  const current = custom ? null : choiceOf(value);
  const music = effects.filter(isMusicEffect);
  const regular = effects.filter((e) => !isMusicEffect(e));
  const effectOptions = [
    ...music.map((n) => ({ value: n, label: effectLabel(n), group: 'Music (reacts to sound)' })),
    ...regular.map((n) => ({ value: n, label: effectLabel(n), group: 'Effects' })),
  ];
  if (value?.kind === 'effect' && !effects.includes(value.effect)) effectOptions.unshift({ value: value.effect, label: effectLabel(value.effect), group: 'Effects' });
  const choices = ROLE_CHOICES.filter((c) => withGap || c.id !== 'gap');
  return (
    <div className="space-y-2">
      <div role="radiogroup" aria-label="What it shows" className={compact ? 'flex flex-nowrap gap-1' : 'grid gap-1.5'}>
        {choices.map((c) =>
          compact ? (
            <button
              key={c.id}
              type="button"
              role="radio"
              aria-checked={current === c.id}
              title={c.description}
              onClick={() => onChange(sectionFor(c.id, value, effects))}
              className={cx(
                'inline-flex h-7 items-center gap-1 whitespace-nowrap rounded-md px-2 text-xs font-medium',
                current === c.id ? CHOICE_ACTIVE : 'text-ink-muted hover:bg-surface-3 hover:text-ink',
              )}
            >
              {c.icon(13)}
              {c.label}
            </button>
          ) : (
            <ChoiceCard
              key={c.id}
              active={current === c.id}
              icon={c.icon(15)}
              title={c.label}
              description={c.description}
              className="py-2"
              onClick={() => onChange(sectionFor(c.id, value, effects))}
            />
          ),
        )}
      </div>
      {value?.kind === 'effect' && (
        <Select label="Effect" hideLabel value={value.effect} options={effectOptions} onChange={(effect) => onChange({ kind: 'effect', effect })} />
      )}
      {value?.kind === 'color' && (
        <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label="Color">
          {SWATCHES.map((s) => {
            const on = s.rgb.every((v, i) => v === value.color[i]);
            return (
              <button
                key={s.name}
                type="button"
                role="radio"
                aria-checked={on}
                title={s.name}
                aria-label={s.name}
                onClick={() => onChange({ kind: 'color', color: [...s.rgb] as Rgb })}
                className={cx('h-6 w-6 rounded-full border-2', on ? 'border-white' : 'border-transparent')}
                style={{ background: `rgb(${s.rgb.join(',')})` }}
              />
            );
          })}
        </div>
      )}
    </div>
  );
}
