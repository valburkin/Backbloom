import { useRef, useState, type ReactNode } from 'react';
import { CHOICE_ACTIVE, cx } from '../components/ui';
import { describePreset, type Preset } from '../settings/intensity';

/**
 * The Look on Home (and in the guided setup): one row of choices, then what the hovered or chosen Look sets. `current` is the preset the settings match ("custom" for none). One Tab stop; the arrow keys move
 * between the presets and show what each sets, Enter or Space picks one (each pick is saved, so arrows don't pick).
 */
export function PresetPicker<Id extends string>({
  label,
  presets,
  current,
  onPick,
  disabled,
  customHint,
}: {
  label: string;
  presets: ReadonlyArray<Preset<Id>>;
  current: Id | 'custom' | null;
  onPick: (id: Id) => void;
  disabled?: boolean;
  /** What to say while the settings match no preset. */
  customHint: ReactNode;
}) {
  const [preview, setPreview] = useState<Id | null>(null);
  const buttons = useRef<Array<HTMLButtonElement | null>>([]);
  const focusable = Math.max(0, presets.findIndex((p) => p.id === current));
  const shown = presets.find((p) => p.id === (preview ?? (current !== 'custom' ? current : null)));
  return (
    <div>
      <div role="radiogroup" aria-label={label} className="grid grid-cols-5 gap-1.5">
        {presets.map((p, i) => {
          const active = current === p.id;
          return (
            <button
              key={p.id}
              ref={(el) => {
                buttons.current[i] = el;
              }}
              type="button"
              role="radio"
              aria-checked={active}
              disabled={disabled}
              tabIndex={i === focusable ? 0 : -1}
              onKeyDown={(e) => {
                const step = e.key === 'ArrowRight' || e.key === 'ArrowDown' ? 1 : e.key === 'ArrowLeft' || e.key === 'ArrowUp' ? -1 : 0;
                if (!step) return;
                e.preventDefault();
                buttons.current[(i + step + presets.length) % presets.length]?.focus();
              }}
              onMouseEnter={() => setPreview(p.id)}
              onMouseLeave={() => setPreview(null)}
              onFocus={() => setPreview(p.id)}
              onBlur={() => setPreview(null)}
              onClick={() => onPick(p.id)}
              className={cx(
                'min-h-10 rounded-lg border px-1 text-[13px] font-medium transition-colors sm:text-sm disabled:cursor-not-allowed disabled:opacity-50',
                active ? cx(CHOICE_ACTIVE, 'border-transparent') : 'border-line-input text-ink-muted hover:border-line-strong hover:text-ink',
              )}
            >
              {p.label}
            </button>
          );
        })}
      </div>
      <div className="mt-3 min-h-[3rem] text-[13px]">
        {shown ? (
          <>
            <p className="text-ink-muted">{shown.description}</p>
            <dl className="mt-1.5 flex flex-wrap gap-x-5 gap-y-1 text-xs">
              {describePreset(shown).map(([k, v]) => (
                <div key={k} className="flex gap-1.5">
                  <dt className="text-ink-faint">{k}</dt>
                  <dd className="tabular-nums text-ink">{v}</dd>
                </div>
              ))}
            </dl>
          </>
        ) : (
          current === 'custom' && <p className="text-ink-muted">{customHint}</p>
        )}
      </div>
    </div>
  );
}
