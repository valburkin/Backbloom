import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Check, Loader2 } from 'lucide-react';
import { cx } from '../components/ui';
import { saveErrorText } from '../app/config';

/** A backend error in words for the wizard (the reply's message, or its rejected settings). */
export function errorMessage(e: unknown): string {
  const text = saveErrorText(e);
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * A step's lifetime: `alive()` is false once the step closed or was left (Back, the dialog's X), and `signal` aborts
 * then, so a write or a 30 s wait never answers into another step.
 */
export function useStepLife(): { alive(): boolean; signal: AbortSignal } {
  const ref = useRef<AbortController | null>(null);
  if (!ref.current) ref.current = new AbortController();
  useEffect(() => {
    // a remount (React's strict mode) starts a new life
    if (ref.current!.signal.aborted) ref.current = new AbortController();
    const c = ref.current!;
    return () => c.abort();
  }, []);
  return {
    alive: () => !ref.current!.signal.aborted,
    get signal() {
      return ref.current!.signal;
    },
  };
}

/** Renders a wizard step in the wizard's dialog: its body and its buttons (`back` replaces the wizard's Back). */
export type StepFrame = (p: { actions: ReactNode; children: ReactNode; back?: () => void; backDisabled?: boolean }) => ReactNode;

/** The wizard's steps, as dots with labels (the current one highlighted, the done ones checked). */
export function StepDots({ steps, current }: { steps: readonly string[]; current: number }) {
  return (
    <ol className="flex flex-wrap items-center gap-2 text-xs text-ink-faint" aria-label={`Step ${current + 1} of ${steps.length}: ${steps[current]}`}>
      {steps.map((label, i) => (
        <li key={label} className="flex items-center gap-2" aria-current={i === current ? 'step' : undefined}>
          <span
            className={cx(
              'flex h-5 w-5 items-center justify-center rounded-full text-[11px] font-semibold',
              i < current ? 'bg-accent/20 text-accent-strong' : i === current ? 'bg-accent text-accent-ink' : 'bg-surface-3 text-ink-muted',
            )}
            aria-hidden
          >
            {i < current ? <Check size={12} /> : i + 1}
          </span>
          <span className={cx(i === current && 'font-medium text-ink')}>{label}</span>
          {i < steps.length - 1 && <span className="h-px w-4 bg-line-strong" aria-hidden />}
        </li>
      ))}
    </ol>
  );
}

/** A big button for one way to go on: an icon, a title and a line of text. */
export function OptionButton({ icon, title, children, onClick, disabled }: { icon?: ReactNode; title: string; children?: ReactNode; onClick(): void; disabled?: boolean }) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="flex w-full items-start gap-3 rounded-xl border border-line-input px-4 py-3 text-left transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-line-input pointer-coarse:min-h-11"
    >
      {icon && <span className="mt-0.5 shrink-0 text-ink-faint">{icon}</span>}
      <span className="min-w-0">
        <span className="block text-sm font-medium text-ink">{title}</span>
        {children && <span className="mt-0.5 block text-xs text-ink-faint">{children}</span>}
      </span>
    </button>
  );
}

/** A step's heading and one line of what to do. */
export function StepTitle({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div>
      <h3 className="text-base font-semibold">{title}</h3>
      {children && <p className="mt-1 text-sm text-ink-muted">{children}</p>}
    </div>
  );
}

export function Busy({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="flex items-center gap-2 text-sm text-ink-muted">
      <Loader2 size={15} className="animate-spin" aria-hidden /> {children}
    </p>
  );
}

export function ErrorText({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="text-sm text-bad">
      {children}
    </p>
  );
}

/** Numbered instructions. */
export function Steps({ items }: { items: ReactNode[] }) {
  return (
    <ol className="space-y-2 text-sm">
      {items.map((item, i) => (
        <li key={i} className="flex gap-3">
          <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-surface-3 text-[11px] font-semibold text-ink-muted" aria-hidden>
            {i + 1}
          </span>
          <span className="min-w-0">{item}</span>
        </li>
      ))}
    </ol>
  );
}

/** Things to check, as a bulleted list. */
export function CheckList({ items }: { items: ReactNode[] }) {
  return (
    <ul className="list-disc space-y-1 pl-5 text-sm text-ink-muted">
      {items.map((item, i) => (
        <li key={i}>{item}</li>
      ))}
    </ul>
  );
}

/**
 * Prints `children` alone: they are rendered into a node outside the app that only the printer shows (see
 * index.css, `#bb-print`). Render it while printing is wanted: once they are in the page it opens the print dialog,
 * and calls `onDone` when that closes.
 */
export function PrintArea({ children, onDone }: { children: ReactNode; onDone(): void }) {
  const [node, setNode] = useState<HTMLElement | null>(null);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    const el = document.createElement('div');
    el.id = 'bb-print';
    document.body.appendChild(el);
    setNode(el);
    return () => el.remove();
  }, []);
  // runs after the render that put the children into the node, so the printout isn't blank
  useEffect(() => {
    if (!node) return;
    const finish = () => done.current();
    window.addEventListener('afterprint', finish, { once: true });
    window.print();
    // a browser without \`afterprint\`: done a moment after print() returned (it blocks while the dialog is open in
    // Chromium and WebView2), so Print works again
    const fallback = setTimeout(finish, 1000);
    return () => {
      clearTimeout(fallback);
      window.removeEventListener('afterprint', finish);
    };
  }, [node]);
  return node ? createPortal(children, node) : null;
}
