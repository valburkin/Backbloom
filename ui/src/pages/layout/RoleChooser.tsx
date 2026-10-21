import { Ban, Palette, Scissors, Sparkles, SquareDashed, Tv } from 'lucide-react';
import type { ReactNode } from 'react';
import type { Rgb, Section } from '../../layout';
import { cx } from '../../components/ui';
import { Select } from '../../components/form';
import { SWATCHES } from '../../components/ColorPicker';
import { effectLabel, isMusicEffect } from '../../control/modes';

/** The roles a strip or a part of it can have, as the editor offers them. */
export type RoleChoice = 'screen' | 'whole' | 'effect' | 'color' | 'none' | 'gap';

const CHOICES: Array<{ id: RoleChoice; label: string; title: string; icon: ReactNode }> = [
  { id: 'screen', label: 'Screen', title: 'Repeat the part of the screen next to it', icon: <Tv size={13} /> },
  { id: 'whole', label: 'Whole screen', title: 'The average color of the whole picture', icon: <SquareDashed size={13} /> },
  { id: 'effect', label: 'Effect', title: 'Play an effect or react to music on its own, whatever the rest shows', icon: <Sparkles size={13} /> },
  { id: 'color', label: 'Color', title: 'One color of its own', icon: <Palette size={13} /> },
  { id: 'none', label: 'Off', title: 'Stay dark', icon: <Ban size={13} /> },
  { id: 'gap', label: 'Gap', title: 'No LEDs here: the strip is cut and joined with wire, or goes around something', icon: <Scissors size={13} /> },
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
 * Buttons for what a strip or a part shows, with the effect or color to pick under them when one of those is chosen.
 * `value` is the current section (undefined = the screen next to it); `custom` marks a strip whose parts differ.
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
  return (
    <div className="space-y-2">
      <div role="radiogroup" aria-label="What it shows" className={cx('flex flex-wrap gap-1', compact && 'flex-nowrap')}>
        {CHOICES.filter((c) => withGap || c.id !== 'gap').map((c) => (
          <button
            key={c.id}
            type="button"
            role="radio"
            aria-checked={current === c.id}
            title={c.title}
            onClick={() => onChange(sectionFor(c.id, value, effects))}
            className={cx(
              'inline-flex h-7 items-center gap-1 whitespace-nowrap rounded-md px-2 text-xs font-medium',
              current === c.id ? 'bg-accent text-accent-ink' : compact ? 'text-ink-muted hover:bg-surface-3 hover:text-ink' : 'border border-line text-ink-muted hover:border-line-strong hover:text-ink',
            )}
          >
            {c.icon}
            {c.label}
          </button>
        ))}
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
