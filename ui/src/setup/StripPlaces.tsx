import { Frame, LampCeiling, LampDesk, Monitor, Shapes } from 'lucide-react';
import { CHOICE_ACTIVE, cx, TILE } from '../components/ui';
import { Advanced } from '../components/form';
import { STRIP_PLACES, type StripPlace } from './wled';

const PLACE_ICONS: Record<StripPlace, typeof Monitor> = { screen: Monitor, ceiling: LampCeiling, desk: LampDesk, wall: Frame, other: Shapes };

/**
 * Picking a place adds it, picking it again takes it out: the places are a set, kept in the cards' order (the way a
 * strip runs between them comes from tracing it, not from the order they were picked in).
 */
export function togglePlace(places: readonly StripPlace[], place: StripPlace): StripPlace[] {
  const next = places.includes(place) ? places.filter((p) => p !== place) : [...places, place];
  return STRIP_PLACES.map((p) => p.value).filter((v) => next.includes(v));
}

/**
 * "Where is this strip?": the places as cards with icons, any combination of them (every place the strip passes);
 * none chosen until the user picks. Then how to mount a strip at the places chosen, folded.
 */
export function PlaceCards({ label = 'Where is this strip?', places, onChange }: { label?: string; places: readonly StripPlace[]; onChange(places: StripPlace[]): void }) {
  const tips = (places.length ? STRIP_PLACES.filter((p) => places.includes(p.value)) : STRIP_PLACES.filter((p) => p.value !== 'other'));
  return (
    <div className="space-y-2">
      <div>
        <p className="text-sm font-medium">{label}</p>
        <p className="text-xs text-ink-faint">Pick every place it passes.</p>
      </div>
      <div role="group" aria-label={label} className="grid gap-2 sm:grid-cols-3">
        {STRIP_PLACES.map((p) => {
          const Icon = PLACE_ICONS[p.value];
          const on = places.includes(p.value);
          return (
            <button
              key={p.value}
              type="button"
              aria-pressed={on}
              onClick={() => onChange(togglePlace(places, p.value))}
              className={cx(
                'flex min-h-11 items-center gap-2.5 rounded-2xl border px-3.5 py-2.5 text-left text-sm font-medium transition-[background-color,border-color,color] duration-200',
                on
                  ? cx(CHOICE_ACTIVE, 'border-transparent')
                  : cx(TILE, 'text-ink-muted hover:text-ink'),
              )}
            >
              <Icon size={16} className={cx('shrink-0', on && 'text-accent-strong')} aria-hidden />
              <span className="min-w-0 flex-1">{p.label}</span>
            </button>
          );
        })}
      </div>
      <Advanced label="Not mounted yet? How to mount it">
        <div className="space-y-3">
          {tips.map((p) => (
            <div key={p.value}>
              <p className="text-sm font-medium">{p.label}</p>
              <ul className="mt-1 list-disc space-y-0.5 pl-5 text-sm text-ink-muted">
                {p.tips.map((t) => (
                  <li key={t}>{t}</li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </Advanced>
    </div>
  );
}
