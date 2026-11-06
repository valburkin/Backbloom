import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type CSSProperties,
  type ReactNode,
} from 'react';
import { X } from 'lucide-react';

export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(' ');
}

/**
 * Text inputs and selects: a filled field whose edge keeps 3:1 (`line-input`); focus turns the edge violet with a soft
 * ring. Add the border color (`border-line-input`, or `border-bad/60` for an error).
 */
export const INPUT_BASE =
  'h-10 w-full rounded-xl border bg-white/[0.03] px-3.5 text-sm text-ink placeholder:text-ink-faint transition-[border-color,box-shadow,background-color] duration-200 hover:bg-white/[0.05] focus:border-accent focus:bg-white/[0.05] focus:shadow-[0_0_0_4px_rgba(139,92,246,0.18)] focus-visible:outline-hidden disabled:opacity-50';

/**
 * A small input in a toolbar or a row (search boxes, names, a number next to a slider): INPUT_BASE's edge, fill and
 * focus ring, with its size and corners left to the caller. `outline-hidden` keeps an outline in Windows High
 * Contrast, where the ring doesn't show.
 */
export const INPUT_SMALL =
  'border border-line-input bg-white/[0.03] transition-[border-color,box-shadow] focus:border-accent focus:shadow-[0_0_0_3px_rgba(139,92,246,0.2)] focus-visible:outline-hidden';

/** Something that floats over the page (menus, toasts, the unsaved-changes bar): frosted, with a shadow to lift it. */
export const FLOATING =
  'border border-white/[0.1] bg-[#141823]/90 shadow-[inset_0_1px_0_rgba(255,255,255,0.06),0_20px_50px_-12px_rgba(0,0,0,0.85)] backdrop-blur-xl';

// ---- Buttons ----

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';
/** xs only inside compact toolbars (the drawing bar on the Layout canvas). */
type ButtonSize = 'xs' | 'sm' | 'md';

const VARIANTS: Record<Variant, string> = {
  // the landing page's button: gradient, sheen and halo (.btn-brand in index.css)
  primary: 'btn-brand font-semibold',
  // frosted glass, like the landing page's second button
  secondary:
    'border border-white/[0.11] bg-white/[0.06] text-ink shadow-[inset_0_1px_0_rgba(255,255,255,0.06)] backdrop-blur-md hover:border-white/[0.18] hover:bg-white/[0.1]',
  ghost: 'text-ink-muted hover:bg-white/[0.06] hover:text-ink',
  danger: 'border border-bad/25 bg-bad/[0.07] text-bad hover:border-bad/40 hover:bg-bad/[0.14]',
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: ButtonSize;
  icon?: ReactNode;
}

/** A button's classes, for elements that must look like one without being a Button (a link, a menu trigger). */
export function buttonClass(variant: Variant = 'secondary', size: ButtonSize = 'md'): string {
  return cx(
    'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-full font-medium select-none',
    'transition-[background-color,border-color,box-shadow,color,scale] duration-200 ease-out-soft motion-safe:enabled:active:scale-[0.97]',
    'disabled:cursor-not-allowed disabled:opacity-45',
    { xs: 'h-7 px-3 text-[13px]', sm: 'h-8 px-3.5 text-[13px]', md: 'h-10 px-5 text-sm' }[size],
    VARIANTS[variant],
  );
}

export function Button({ variant = 'secondary', size = 'md', icon, className, children, type = 'button', ...rest }: ButtonProps) {
  return (
    <button type={type} className={cx(buttonClass(variant, size), className)} {...rest}>
      {icon}
      {children}
    </button>
  );
}

export function IconButton({
  label,
  className,
  children,
  type = 'button',
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return (
    <button
      type={type}
      aria-label={label}
      title={label}
      className={cx(
        'inline-flex h-8 w-8 items-center justify-center rounded-full text-ink-muted transition-colors',
        'hover:bg-white/[0.07] hover:text-ink disabled:opacity-30 disabled:cursor-not-allowed disabled:hover:bg-transparent',
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

// ---- Surfaces ----

/**
 * The one card look (`.card` in index.css): a lighter, slightly translucent surface with a faint edge and a light top
 * edge, so the page glow shows through a little. No drop shadow: on the dark page it wouldn't show.
 */
export const CARD = 'card rounded-3xl';

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cx(CARD, className)}>{children}</div>;
}

export function Badge({ tone = 'neutral', children, className }: { tone?: 'neutral' | 'ok' | 'warn' | 'bad' | 'accent'; children: ReactNode; className?: string }) {
  const tones = {
    neutral: 'bg-surface-3 text-ink-muted',
    ok: 'bg-ok/12 text-ok',
    warn: 'bg-warn/12 text-warn',
    bad: 'bg-bad/12 text-bad',
    accent: 'bg-accent/15 text-accent-strong',
  } as const;
  return (
    <span className={cx('inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold tracking-wide', tones[tone], className)}>
      {children}
    </span>
  );
}

/** Colored status dot; `live` pulses it (only for something that is happening right now, e.g. a running effect). */
export function StatusDot({ tone, live }: { tone: 'ok' | 'warn' | 'bad' | 'neutral'; live?: boolean }) {
  const c = { ok: 'bg-ok', warn: 'bg-warn', bad: 'bg-bad', neutral: 'bg-ink-faint' }[tone];
  return (
    <span className="relative inline-flex h-2.5 w-2.5" aria-hidden>
      {tone === 'ok' && live && <span className={cx('absolute inline-flex h-full w-full rounded-full opacity-40 motion-safe:animate-ping', c)} />}
      <span className={cx('relative inline-flex h-2.5 w-2.5 rounded-full', c)} />
    </span>
  );
}

// ---- Form controls ----

/** An option tile that isn't chosen (ChoiceCard, OptionButton and the like): a faint fill and edge, brighter on hover. */
export const TILE = 'border border-white/[0.08] bg-white/[0.03] transition-[background-color,border-color,color] duration-200 enabled:hover:border-white/[0.15] enabled:hover:bg-white/[0.06]';

/** The one look of a chosen option (`.choice-on` in index.css), shared by the "pick one" rows and ChoiceCard. */
export const CHOICE_ACTIVE = 'choice-on';

/** The track of a "pick one" row (Segmented, the Look, tabs): a filled pill the chosen option's thumb slides along. */
export const CHOICE_TRACK = 'relative inline-flex flex-wrap gap-0.5 rounded-[20px] border border-white/[0.08] bg-white/[0.035] p-1';

/** An option in a CHOICE_TRACK: sits above the thumb; the chosen one is ink, the others muted. */
export function choiceOption(active: boolean): string {
  return cx(
    'relative z-[1] inline-flex items-center justify-center gap-1.5 rounded-full font-medium transition-colors duration-200 disabled:cursor-not-allowed disabled:opacity-45',
    active ? 'text-ink' : 'text-ink-muted hover:text-ink',
  );
}

/**
 * The chosen option's thumb in a CHOICE_TRACK: slides to the option marked chosen (aria-checked, aria-selected or
 * aria-current) when `key` changes; `dimmed` while the row is disabled. Put the returned ref on the track and render
 * `thumb` as its first child.
 */
export function useChoiceThumb<T extends HTMLElement = HTMLDivElement>(key: unknown, dimmed = false) {
  const ref = useRef<T>(null);
  const [box, setBox] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const placed = useRef(false);
  useLayoutEffect(() => {
    const track = ref.current;
    if (!track) return;
    const measure = () => {
      const el = track.querySelector<HTMLElement>(
        ':scope > [aria-checked="true"], :scope > [aria-selected="true"], :scope > [aria-current="page"]',
      );
      setBox((prev) => {
        const next = el ? { x: el.offsetLeft, y: el.offsetTop, w: el.offsetWidth, h: el.offsetHeight } : null;
        return prev && next && prev.x === next.x && prev.y === next.y && prev.w === next.w && prev.h === next.h ? prev : next;
      });
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(track);
    return () => ro.disconnect();
  }, [key]);
  useEffect(() => {
    // slide only once the thumb has a place: the first placement appears without moving
    if (box) placed.current = true;
  }, [box]);
  const thumb = box && box.w > 0 && (
    <span
      aria-hidden
      className={cx(
        CHOICE_ACTIVE,
        'pointer-events-none absolute left-0 top-0 rounded-full',
        dimmed && 'opacity-45',
        placed.current && 'transition-[transform,width,height] duration-300 ease-out-soft motion-reduce:transition-none',
      )}
      style={{ width: box.w, height: box.h, transform: `translate(${box.x}px, ${box.y}px)` }}
    />
  );
  return { ref, thumb };
}

/** Style for a range input: how far its track is filled (see the slider rules in index.css). */
export function rangeFill(value: number, min: number, max: number): CSSProperties {
  const f = max > min ? ((Math.min(max, Math.max(min, value)) - min) / (max - min)) * 100 : 0;
  return { '--fill': `${f}%` } as CSSProperties;
}

/**
 * Larger "pick one" option with a title and an optional line of explanation (modes, presets). Use inside an element
 * with role="radiogroup"; for short labels use Segmented instead.
 */
export function ChoiceCard({
  active,
  title,
  description,
  icon,
  onClick,
  disabled,
  className,
}: {
  active: boolean;
  title: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      disabled={disabled}
      onClick={onClick}
      className={cx(
        'flex min-h-11 items-start gap-2.5 rounded-2xl border px-3.5 py-3 text-left transition-[background-color,border-color,color] duration-200 disabled:cursor-not-allowed disabled:opacity-40',
        active
          ? cx(CHOICE_ACTIVE, 'border-transparent')
          : cx(TILE, 'text-ink-muted enabled:hover:text-ink'),
        className,
      )}
    >
      {icon && <span className={cx('mt-0.5 shrink-0', active && 'text-accent-strong')}>{icon}</span>}
      <span className="min-w-0">
        <span className="block text-sm font-medium">{title}</span>
        {description && <span className="mt-0.5 block text-xs text-ink-faint">{description}</span>}
      </span>
    </button>
  );
}

export function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
  size = 'md',
}: {
  label: string;
  value: T;
  options: ReadonlyArray<{ value: T; label: ReactNode; title?: string }>;
  onChange: (v: T) => void;
  size?: 'sm' | 'md';
}) {
  const { ref, thumb } = useChoiceThumb(`${value}|${options.map((o) => o.value).join(',')}`);
  return (
    <div ref={ref} role="radiogroup" aria-label={label} className={CHOICE_TRACK}>
      {thumb}
      {options.map((o, index) => {
        const active = o.value === value;
        // nothing chosen yet: the first option is the Tab stop
        const tabStop = active || (index === 0 && !options.some((x) => x.value === value));
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={o.title}
            onClick={() => onChange(o.value)}
            onKeyDown={(e) => {
              const i = options.findIndex((x) => x.value === value);
              if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                e.preventDefault();
                onChange(options[(i + 1) % options.length].value);
              } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                e.preventDefault();
                onChange(options[(i - 1 + options.length) % options.length].value);
              }
            }}
            tabIndex={tabStop ? 0 : -1}
            className={cx(choiceOption(active), size === 'sm' ? 'h-7 px-3 text-xs' : 'h-8 px-3.5 text-[13px]')}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

/**
 * Slider with a linked numeric input. Values are in model units; `scale` converts for display
 * (e.g. scale=100 shows 0..1 as 0..100 %).
 */
export function SliderField({
  label,
  hint,
  value,
  min,
  max,
  step = 1,
  scale = 1,
  unit,
  minLabel,
  maxLabel,
  onChange,
  disabled,
}: {
  label: string;
  hint?: ReactNode;
  value: number;
  min: number;
  max: number;
  step?: number;
  scale?: number;
  unit?: string;
  minLabel?: string;
  maxLabel?: string;
  onChange: (v: number) => void;
  disabled?: boolean;
}) {
  const id = useId();
  const decimals = Math.max(0, -Math.floor(Math.log10(step * scale) + 1e-9));
  const shown = Number((value * scale).toFixed(decimals));
  const [draft, setDraft] = useState<string | null>(null);
  // Only a typed value is committed: leaving the box untouched must not write the shown value back.
  const commitDraft = (s: string) => {
    const n = Number(s);
    if (draft !== null && s.trim() !== '' && Number.isFinite(n)) onChange(Math.min(max, Math.max(min, n / scale)));
    setDraft(null);
  };
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between gap-3">
        <label htmlFor={id} className="text-sm font-medium text-ink">
          {label}
        </label>
        <div className="flex items-center gap-1">
          <input
            type="number"
            aria-label={`${label} value`}
            className={cx(INPUT_SMALL, 'h-7 w-16 rounded-lg px-2 text-right text-[13px] tabular-nums text-ink')}
            value={draft ?? String(shown)}
            min={min * scale}
            max={max * scale}
            step={step * scale}
            disabled={disabled}
            onChange={(e) => {
              setDraft(e.target.value);
              const n = Number(e.target.value);
              if (e.target.value.trim() !== '' && Number.isFinite(n) && n >= min * scale && n <= max * scale) onChange(n / scale);
            }}
            onBlur={(e) => commitDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitDraft((e.target as HTMLInputElement).value);
            }}
          />
          {unit && <span className="w-4 text-xs text-ink-faint">{unit}</span>}
        </div>
      </div>
      <input
        id={id}
        type="range"
        className="w-full"
        min={min}
        max={max}
        step={step}
        value={value}
        style={rangeFill(value, min, max)}
        disabled={disabled}
        onChange={(e) => onChange(Number(e.target.value))}
      />
      {(minLabel || maxLabel) && (
        <div className="flex justify-between text-[11px] text-ink-faint" aria-hidden>
          <span>{minLabel}</span>
          <span>{maxLabel}</span>
        </div>
      )}
      {hint && <p className="text-xs text-ink-faint">{hint}</p>}
    </div>
  );
}

export function Stepper({
  label,
  value,
  min = 0,
  max = 9999,
  onChange,
  className,
}: {
  label: string;
  value: number;
  min?: number;
  max?: number;
  onChange: (v: number) => void;
  className?: string;
}) {
  const id = useId();
  const [draft, setDraft] = useState<string | null>(null);
  const clamp = (n: number) => Math.min(max, Math.max(min, Math.round(n)));
  return (
    <div className={cx('flex items-center justify-between gap-3', className)}>
      <label htmlFor={id} className="text-sm font-medium text-ink">
        {label}
      </label>
      <div className="inline-flex items-center rounded-full border border-line-input bg-white/[0.03] p-0.5 transition-[border-color,box-shadow] focus-within:border-accent focus-within:shadow-[0_0_0_3px_rgba(139,92,246,0.2)]">
        <button
          type="button"
          aria-label={`Decrease ${label}`}
          className="h-7 w-7 rounded-full text-ink-muted transition-colors hover:bg-white/[0.08] hover:text-ink disabled:opacity-30 disabled:hover:bg-transparent"
          disabled={value <= min}
          onClick={() => onChange(clamp(value - 1))}
        >
          −
        </button>
        <input
          id={id}
          type="number"
          inputMode="numeric"
          className="h-7 w-12 bg-transparent text-center text-sm tabular-nums text-ink focus-visible:outline-hidden"
          value={draft ?? String(value)}
          min={min}
          max={max}
          onChange={(e) => {
            setDraft(e.target.value);
            const n = Number(e.target.value);
            if (e.target.value.trim() !== '' && Number.isFinite(n) && n >= min && n <= max) onChange(clamp(n));
          }}
          onBlur={(e) => {
            const n = Number(e.target.value);
            if (e.target.value.trim() !== '' && Number.isFinite(n)) onChange(clamp(n));
            setDraft(null);
          }}
        />
        <button
          type="button"
          aria-label={`Increase ${label}`}
          className="h-7 w-7 rounded-full text-ink-muted transition-colors hover:bg-white/[0.08] hover:text-ink disabled:opacity-30 disabled:hover:bg-transparent"
          disabled={value >= max}
          onClick={() => onChange(clamp(value + 1))}
        >
          +
        </button>
      </div>
    </div>
  );
}

export function TextField({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
  autoFocus,
  className,
  hideLabel,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  autoFocus?: boolean;
  className?: string;
  hideLabel?: boolean;
}) {
  const id = useId();
  return (
    <div className={cx('space-y-1.5', className)}>
      <label htmlFor={id} className={cx('block text-sm font-medium text-ink', hideLabel && 'sr-only')}>
        {label}
      </label>
      <input
        id={id}
        type={type}
        value={value}
        autoFocus={autoFocus}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className={cx(INPUT_BASE, 'border-line-input')}
      />
    </div>
  );
}

// ---- Dialog ----

export function Dialog({
  open,
  title,
  onClose,
  children,
  footer,
  wide,
  closeOnBackdrop = true,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
  /** A click beside the dialog closes it (false for a wizard: a stray click mustn't lose its steps; X and Esc still call onClose). */
  closeOnBackdrop?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;
  useEffect(() => {
    if (!open) return;
    const prev = document.activeElement as HTMLElement | null;
    const el = ref.current;
    const focusables = () =>
      el ? [...el.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')].filter((x) => !x.hasAttribute('disabled')) : [];
    (focusables()[1] ?? focusables()[0])?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onCloseRef.current();
      } else if (e.key === 'Tab') {
        const f = focusables();
        if (!f.length) return;
        const first = f[0];
        const last = f[f.length - 1];
        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }
    };
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('keydown', onKey, true);
      prev?.focus?.();
    };
  }, [open]);

  if (!open) return null;
  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/65 p-4 backdrop-blur-md" onMouseDown={(e) => closeOnBackdrop && e.target === e.currentTarget && onClose()}>
      <div
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className={cx(
          'flex max-h-[90vh] w-full flex-col rounded-3xl border border-white/[0.1] bg-[#10131b] shadow-[inset_0_1px_0_rgba(255,255,255,0.06),0_30px_80px_-20px_rgba(0,0,0,0.9)]',
          wide ? 'max-w-3xl' : 'max-w-lg',
        )}
      >
        <div className="flex items-center justify-between border-b border-line px-6 py-4">
          <h2 id={titleId} className="text-base font-semibold">
            {title}
          </h2>
          <IconButton label="Close" onClick={onClose}>
            <X size={18} />
          </IconButton>
        </div>
        <div className="scroll-thin min-h-0 flex-1 overflow-y-auto px-6 py-5">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-line px-6 py-3.5">{footer}</div>}
      </div>
    </div>
  );
}

export function EmptyState({ icon, title, children }: { icon?: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-10 text-center">
      {icon && <div className="mb-1 text-ink-faint">{icon}</div>}
      <p className="text-sm font-medium text-ink">{title}</p>
      {children && <div className="max-w-sm text-[13px] text-ink-muted">{children}</div>}
    </div>
  );
}
