import { useState, type ReactNode } from 'react';
import { CHOICE_ACTIVE, cx } from '../components/ui';
import { describeIntensity, INTENSITY_PRESETS, type IntensityId, type IntensityPreset } from '../settings/intensity';

/**
 * The Intensity presets (Calm, Balanced, Lively, Intense), shared by Home and the Colors page: one row of choices, then
 * what the hovered or chosen preset sets. `current` is the preset the settings match ("custom" for none).
 */
export function IntensityPicker({
  current,
  onPick,
  disabled,
  customHint,
}: {
  current: IntensityId | null;
  onPick: (id: IntensityPreset['id']) => void;
  disabled?: boolean;
  /** What to say while the settings match no preset. */
  customHint: ReactNode;
}) {
  const [preview, setPreview] = useState<IntensityPreset['id'] | null>(null);
  const shown = INTENSITY_PRESETS.find((p) => p.id === (preview ?? (current !== 'custom' ? current : null)));
  return (
    <div className="@container">
      <div role="radiogroup" aria-label="Intensity" className="grid grid-cols-2 gap-1.5 @xs:grid-cols-4">
        {INTENSITY_PRESETS.map((p) => {
          const active = current === p.id;
          return (
            <button
              key={p.id}
              type="button"
              role="radio"
              aria-checked={active}
              disabled={disabled}
              onMouseEnter={() => setPreview(p.id)}
              onMouseLeave={() => setPreview(null)}
              onFocus={() => setPreview(p.id)}
              onBlur={() => setPreview(null)}
              onClick={() => onPick(p.id)}
              className={cx(
                'min-h-10 rounded-lg border text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50',
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
              {describeIntensity(shown).map(([k, v]) => (
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
