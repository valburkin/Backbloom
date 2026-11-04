import { Ban, MonitorPlay, Palette, Scissors, Sparkles } from 'lucide-react';
import { Fragment, type ReactNode } from 'react';
import type { Rgb, Section } from '../../layout';
import { ChoiceCard, CHOICE_ACTIVE, Segmented, cx } from '../../components/ui';
import { Select } from '../../components/form';
import { SWATCHES } from '../../components/ColorPicker';
import { effectLabel, isMusicEffect, type Mode } from '../../control/modes';
import { followsShow } from '../../editor/mapping';

/**
 * What a strip or a part of it does, as the editor offers it: follow what the lights show (`show`: the screen next to
 * it or the screen's average with Screen sync, else the music, effect or color shown), or something of its own with
 * the layout (an effect, a color: music, an effect or a color picked for every light plays on them too), off, or a gap
 * without LEDs.
 */
export type RoleChoice = 'show' | 'effect' | 'color' | 'none' | 'gap';

/** What a part that follows the Show takes from the screen with Screen sync: the part next to it or the average. */
export type ScreenRole = 'screen' | 'whole';

export const ROLE_CHOICES: ReadonlyArray<{ id: RoleChoice; label: string; short: string; description: string; icon: (size: number) => ReactNode }> = [
  { id: 'show', label: 'Follows the Show', short: 'Show', description: 'Plays what your lights show', icon: (s) => <MonitorPlay size={s} /> },
  { id: 'effect', label: 'Its own effect', short: 'Effect', description: 'Plays its own effect or music, until you pick one for all your lights', icon: (s) => <Sparkles size={s} /> },
  { id: 'color', label: 'Its own color', short: 'Color', description: 'Glows in its own color, until you pick something for all your lights', icon: (s) => <Palette size={s} /> },
  { id: 'none', label: 'Off', short: 'Off', description: 'These LEDs stay dark', icon: (s) => <Ban size={s} /> },
  { id: 'gap', label: 'Gap', short: 'Gap', description: 'No LEDs here: the strip is cut and joined with wire', icon: (s) => <Scissors size={s} /> },
];

export const SCREEN_ROLES: ReadonlyArray<{ value: ScreenRole; label: string; title: string }> = [
  { value: 'screen', label: 'Screen edge', title: 'Follows the part of the screen next to it' },
  { value: 'whole', label: 'Screen average', title: 'The average color of the whole screen, for soft light' },
];

/** The end of "Plays what your lights show", by what they show now. */
const SHOWING_NOW: Record<Mode, string> = {
  screen: 'the screen now',
  music: 'music now',
  effect: 'an effect now',
  color: 'a color now',
};

/** What plays on every strip while the lights don't show the layout. */
const PLAYING: Record<Exclude<Mode, 'screen'>, string> = { music: 'music', effect: 'an effect', color: 'a color' };

const DEFAULT_COLOR: Rgb = [255, 140, 40];

/** The choice a section stands for. */
export function choiceOf(section: Section | undefined): RoleChoice {
  return !section || followsShow(section) ? 'show' : (section.kind as RoleChoice);
}

/** What a section that follows the Show takes from the screen (one area counts as the average). */
export function screenRoleOf(section: Section | undefined): ScreenRole {
  return section?.kind === 'whole' || section?.kind === 'area' ? 'whole' : 'screen';
}

/**
 * The section a choice makes (undefined = the screen next to it), keeping the current effect or color, and for `show`
 * what the part took from the screen when it already followed the Show.
 */
export function sectionFor(choice: RoleChoice | ScreenRole, current: Section | undefined, effects: readonly string[]): Section | undefined {
  switch (choice) {
    case 'show':
      return followsShow(current) ? current : undefined;
    case 'screen':
      return undefined;
    case 'whole':
      return current?.kind === 'area' ? current : { kind: 'whole' };
    case 'effect':
      return current?.kind === 'effect' ? current : { kind: 'effect', effect: effects.find(isMusicEffect) ?? effects[0] ?? 'Candle' };
    case 'color':
      return current?.kind === 'color' ? current : { kind: 'color', color: DEFAULT_COLOR };
    default:
      return { kind: choice };
  }
}

/**
 * What a strip or a part shows, with what it takes from the screen, the effect or the color under it. `value` is the
 * current section (undefined = the screen next to it); `custom` marks a strip whose parts differ; `showing` is what the
 * lights show now (null: unknown). `compact` is the one-row version for the popup on the canvas; otherwise each role is
 * a card with a line of explanation.
 */
export function RoleChooser({
  value,
  custom,
  effects,
  withGap,
  compact,
  showing,
  onChange,
}: {
  value: Section | undefined;
  custom?: boolean;
  /** Effect names the backend runs. */
  effects: readonly string[];
  withGap?: boolean;
  compact?: boolean;
  showing?: Mode | null;
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
  const describe = (c: (typeof ROLE_CHOICES)[number]) => (c.id === 'show' && showing ? `${c.description} — ${SHOWING_NOW[showing]}` : c.description);
  // what a part that follows the Show takes from the screen: under its card (in the row of choices when compact)
  const screenRow = (
    <div className={compact ? '' : 'space-y-1 pb-1 pl-3'}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs text-ink-muted">With Screen sync</span>
        <Segmented<ScreenRole>
          label="With Screen sync"
          size="sm"
          value={screenRoleOf(value)}
          options={SCREEN_ROLES}
          onChange={(role) => onChange(sectionFor(role, value, effects))}
        />
      </div>
    </div>
  );
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
              aria-label={c.label}
              title={describe(c)}
              onClick={() => onChange(sectionFor(c.id, value, effects))}
              className={cx(
                'inline-flex h-7 items-center gap-1 whitespace-nowrap rounded-md px-2 text-xs font-medium',
                current === c.id ? CHOICE_ACTIVE : 'text-ink-muted hover:bg-surface-3 hover:text-ink',
              )}
            >
              {c.icon(13)}
              {c.short}
            </button>
          ) : (
            <Fragment key={c.id}>
              <ChoiceCard
                active={current === c.id}
                icon={c.icon(15)}
                title={c.label}
                description={describe(c)}
                className="py-2"
                onClick={() => onChange(sectionFor(c.id, value, effects))}
              />
              {c.id === 'show' && current === 'show' && screenRow}
            </Fragment>
          ),
        )}
      </div>
      {compact && current === 'show' && screenRow}
      {showing && showing !== 'screen' && !compact && (
        <p className="text-xs text-ink-faint">{`Your lights play ${PLAYING[showing]} on every strip now. Choosing here switches them back to your layout.`}</p>
      )}
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
