import { useEffect, useId, useState, type ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { useExpertTools } from '../app/expert';
import { CARD, INPUT_BASE, cx } from './ui';

/** Label + control + hint/error, with ids wired for screen readers. */
export function Field({
  label,
  hint,
  error,
  required,
  children,
  className,
  hideLabel,
  inline,
}: {
  label: ReactNode;
  hint?: ReactNode;
  error?: string;
  required?: boolean;
  /** Receives the control's id and the id of its description. */
  children: (id: string, describedBy: string | undefined) => ReactNode;
  className?: string;
  hideLabel?: boolean;
  /** Label and control on one row (switches). */
  inline?: boolean;
}) {
  const id = useId();
  const descId = hint || error ? `${id}-desc` : undefined;
  const desc = (hint || error) && (
    <p id={descId} className={cx('text-xs', inline && 'mt-0.5', error ? 'text-bad' : 'text-ink-faint')}>
      {error ?? hint}
    </p>
  );
  return (
    <div className={cx(inline ? 'flex items-start justify-between gap-4' : 'space-y-1.5', className)}>
      <div className={cx(inline && 'min-w-0 flex-1')}>
        <label htmlFor={id} className={cx('block text-sm font-medium text-ink', hideLabel && 'sr-only')}>
          {label}
          {required && (
            <span className="ml-0.5 text-ink-faint" aria-hidden>
              *
            </span>
          )}
        </label>
        {inline && desc}
      </div>
      <div className={cx(inline && 'shrink-0')}>{children(id, descId)}</div>
      {!inline && desc}
    </div>
  );
}

export const INPUT_CLASS = INPUT_BASE;

/** A select drawn like the inputs, with its own arrow (add the border color, like INPUT_CLASS). */
export const SELECT_CLASS = cx(INPUT_BASE.replace('px-3.5', 'pl-3.5 pr-9'), 'select-arrow cursor-pointer');

/** Border of an input that has no error. */
export const INPUT_BORDER = 'border-line-input';

export function Input({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
  hint,
  error,
  required,
  disabled,
  autoComplete,
  className,
  trailing,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  hint?: ReactNode;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  autoComplete?: string;
  className?: string;
  /** Extra control shown right of the input (e.g. a "Find" button). */
  trailing?: ReactNode;
}) {
  return (
    <Field label={label} hint={hint} error={error} required={required} className={className}>
      {(id, describedBy) => (
        <div className="flex items-center gap-2">
          <input
            id={id}
            type={type}
            value={value}
            placeholder={placeholder}
            disabled={disabled}
            autoComplete={autoComplete}
            spellCheck={false}
            aria-describedby={describedBy}
            aria-invalid={error ? true : undefined}
            onChange={(e) => onChange(e.target.value)}
            className={cx(INPUT_CLASS, error ? 'border-bad/60' : INPUT_BORDER)}
          />
          {trailing}
        </div>
      )}
    </Field>
  );
}

/** Number input with unit; keeps a text draft so partially typed values ("1.", "-") aren't fought. */
export function NumberField({
  label,
  value,
  onChange,
  min,
  max,
  step,
  unit,
  hint,
  error,
  required,
  disabled,
  integer,
  className,
}: {
  label: string;
  value: number | undefined;
  onChange: (v: number) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  hint?: ReactNode;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  integer?: boolean;
  className?: string;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const parse = (s: string): number | null => {
    const n = Number(s);
    return s.trim() !== '' && Number.isFinite(n) ? (integer ? Math.round(n) : n) : null;
  };
  return (
    <Field label={label} hint={hint} error={error} required={required} className={className}>
      {(id, describedBy) => (
        <div className="flex items-center gap-2">
          <input
            id={id}
            type="number"
            inputMode={integer ? 'numeric' : 'decimal'}
            value={draft ?? (value === undefined || Number.isNaN(value) ? '' : String(value))}
            min={min}
            max={max}
            step={step ?? (integer ? 1 : 'any')}
            disabled={disabled}
            aria-describedby={describedBy}
            aria-invalid={error ? true : undefined}
            onChange={(e) => {
              setDraft(e.target.value);
              const n = parse(e.target.value);
              if (n !== null) onChange(n);
            }}
            onBlur={(e) => {
              const n = parse(e.target.value);
              if (n !== null) onChange(n);
              setDraft(null);
            }}
            className={cx(INPUT_CLASS, 'max-w-[11rem] tabular-nums', error ? 'border-bad/60' : INPUT_BORDER)}
          />
          {unit && <span className="text-xs text-ink-faint">{unit}</span>}
        </div>
      )}
    </Field>
  );
}

export interface SelectOption<T extends string | number = string> {
  value: T;
  label: string;
  group?: string;
  disabled?: boolean;
}

export function Select<T extends string | number = string>({
  label,
  value,
  options,
  onChange,
  hint,
  error,
  disabled,
  className,
  hideLabel,
  placeholder = 'Choose…',
}: {
  label: string;
  value: T | undefined;
  options: ReadonlyArray<SelectOption<T>>;
  onChange: (v: T) => void;
  hint?: ReactNode;
  error?: string;
  disabled?: boolean;
  className?: string;
  hideLabel?: boolean;
  placeholder?: string;
}) {
  const groups = [...new Set(options.map((o) => o.group ?? ''))];
  const known = options.some((o) => o.value === value);
  const render = (o: SelectOption<T>) => (
    <option key={String(o.value)} value={String(o.value)} disabled={o.disabled}>
      {o.label}
    </option>
  );
  return (
    <Field label={label} hint={hint} error={error} className={className} hideLabel={hideLabel}>
      {(id, describedBy) => (
        <select
          id={id}
          value={value === undefined ? '' : String(value)}
          disabled={disabled}
          aria-describedby={describedBy}
          onChange={(e) => {
            const o = options.find((x) => String(x.value) === e.target.value);
            if (o) onChange(o.value);
          }}
          className={cx(SELECT_CLASS, error ? 'border-bad/60' : INPUT_BORDER)}
        >
          {!known && (
            <option value={value === undefined ? '' : String(value)} disabled>
              {value === undefined || value === '' ? placeholder : String(value)}
            </option>
          )}
          {groups.length > 1
            ? groups.map((g) => (
                <optgroup key={g} label={g || 'Other'}>
                  {options.filter((o) => (o.group ?? '') === g).map(render)}
                </optgroup>
              ))
            : options.map(render)}
        </select>
      )}
    </Field>
  );
}

/** On/off switch with a label (and optional explanation) on the left. */
export function Toggle({
  label,
  checked,
  onChange,
  hint,
  disabled,
  className,
}: {
  label: ReactNode;
  checked: boolean;
  onChange: (v: boolean) => void;
  hint?: ReactNode;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <Field label={label} hint={hint} inline className={className}>
      {(id, describedBy) => <Switch id={id} describedBy={describedBy} checked={checked} onChange={onChange} disabled={disabled} />}
    </Field>
  );
}

export function Switch({
  checked,
  onChange,
  disabled,
  id,
  describedBy,
  label,
  size = 'md',
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  id?: string;
  describedBy?: string;
  /** Accessible name when there's no visible <label>. */
  label?: string;
  size?: 'md' | 'lg';
}) {
  const lg = size === 'lg';
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked}
      aria-describedby={describedBy}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cx(
        'relative inline-flex shrink-0 items-center rounded-full border transition-colors disabled:cursor-not-allowed disabled:opacity-40',
        lg ? 'h-8 w-14' : 'mt-0.5 h-6 w-11',
        checked ? 'bg-brand border-transparent shadow-[0_0_18px_-4px_rgba(139,92,246,0.75)]' : 'border-white/[0.14] bg-white/[0.08] hover:bg-white/[0.12]',
      )}
    >
      <span
        aria-hidden
        className={cx(
          'inline-block rounded-full bg-white shadow-[0_1px_4px_rgba(0,0,0,0.45)] transition-transform duration-200 ease-[cubic-bezier(0.22,1,0.36,1)]',
          lg ? 'h-6 w-6' : 'h-[18px] w-[18px]',
          checked ? (lg ? 'translate-x-[26px]' : 'translate-x-[21px]') : 'translate-x-[3px]',
        )}
      />
    </button>
  );
}

/** Titled group of settings. */
export function Section({
  title,
  description,
  icon,
  actions,
  children,
  className,
}: {
  title: string;
  description?: ReactNode;
  icon?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  const id = useId();
  return (
    <section aria-labelledby={id} className={cx(CARD, 'p-5', className)}>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          {icon && <div className="mt-0.5 text-ink-faint">{icon}</div>}
          <div className="min-w-0">
            <h2 id={id} className="text-base font-semibold">
              {title}
            </h2>
            {description && <p className="mt-0.5 text-sm text-ink-muted">{description}</p>}
          </div>
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
      <div className="space-y-5">{children}</div>
    </section>
  );
}

/**
 * "More options": the collapsed end of a section, for settings most people never need (the "advanced" tier in
 * docs/ui-guidelines.md). Says how many values inside differ from their defaults, opens by itself while one of them
 * has a problem, and can put them all back.
 */
export function Advanced({
  children,
  label = 'More options',
  defaultOpen,
  changed = 0,
  invalid,
  onReset,
}: {
  children: ReactNode;
  label?: string;
  defaultOpen?: boolean;
  /** Number of values inside that differ from their defaults. */
  changed?: number;
  /** A value inside has a problem: open, so it can be seen. */
  invalid?: boolean;
  /** Puts the values inside back to their defaults (offered while `changed` > 0). */
  onReset?: () => void;
}) {
  const [open, setOpen] = useState(!!defaultOpen || !!invalid);
  useEffect(() => {
    if (invalid) setOpen(true);
  }, [invalid]);
  return (
    <details className="group border-t border-line pt-1" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="-mx-2 flex cursor-pointer select-none items-center gap-1.5 rounded-lg px-2 py-2 text-sm font-medium text-ink-muted hover:text-ink [&::-webkit-details-marker]:hidden">
        <ChevronRight size={15} aria-hidden className="transition-transform group-open:rotate-90" />
        {label}
        {changed > 0 && <span className="ml-1 text-xs font-normal text-ink-faint">({changed} changed)</span>}
      </summary>
      <div className="space-y-5 pb-1 pt-3">
        {children}
        {onReset && changed > 0 && (
          <button type="button" className="text-[13px] text-accent-strong hover:underline" onClick={onReset}>
            Reset these to defaults
          </button>
        )}
      </div>
    </details>
  );
}

/** Content shown only while "Show expert and diagnostic tools" is on (Settings → General). */
export function Expert({ children, always }: { children: ReactNode; always?: boolean }) {
  return useExpertTools() || always ? <>{children}</> : null;
}

export function Callout({
  tone = 'info',
  icon,
  children,
  className,
}: {
  tone?: 'info' | 'warn' | 'bad' | 'ok';
  icon?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  const tones = {
    info: 'border-line bg-white/[0.03] text-ink-muted',
    warn: 'border-warn/30 bg-warn/10 text-warn',
    bad: 'border-bad/30 bg-bad/10 text-bad',
    ok: 'border-ok/30 bg-ok/10 text-ok',
  } as const;
  return (
    <div role={tone === 'bad' ? 'alert' : undefined} className={cx('flex gap-2.5 rounded-2xl border px-3.5 py-3 text-[13px]', tones[tone], className)}>
      {icon && <span className="mt-px shrink-0">{icon}</span>}
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  );
}
