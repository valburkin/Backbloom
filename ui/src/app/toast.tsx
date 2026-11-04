import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';
import { CheckCircle2, AlertTriangle, Info, X } from 'lucide-react';
import { cx } from '../components/ui';

type Tone = 'ok' | 'bad' | 'info';
/** A button on a toast (Undo): runs and closes it. */
export interface ToastAction {
  label: string;
  run(): void;
}

interface Toast {
  id: number;
  tone: Tone;
  title: string;
  body?: string;
  action?: ToastAction;
}

const Ctx = createContext<(tone: Tone, title: string, body?: string, action?: ToastAction) => void>(() => {});

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), []);
  const push = useCallback(
    (tone: Tone, title: string, body?: string, action?: ToastAction) => {
      const id = Date.now() + Math.random();
      setToasts((t) => [...t.slice(-3), { id, tone, title, body, action }]);
      setTimeout(() => dismiss(id), tone === 'bad' || action ? 9000 : 5000);
    },
    [dismiss],
  );
  return (
    <Ctx.Provider value={push}>
      {children}
      <div className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-[22rem] flex-col gap-2" role="status" aria-live="polite">
        {toasts.map((t) => {
          const Icon = t.tone === 'ok' ? CheckCircle2 : t.tone === 'bad' ? AlertTriangle : Info;
          return (
            <div key={t.id} className="pointer-events-auto flex gap-3 rounded-xl border border-line-strong bg-surface-2 p-3 shadow-2xl">
              <Icon size={18} className={cx('mt-0.5 shrink-0', t.tone === 'ok' ? 'text-ok' : t.tone === 'bad' ? 'text-bad' : 'text-accent-strong')} />
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium">{t.title}</p>
                {t.body && <p className="mt-0.5 text-[13px] text-ink-muted">{t.body}</p>}
                {t.action && (
                  <button
                    type="button"
                    className="mt-1.5 text-[13px] font-medium text-accent-strong underline-offset-2 hover:underline"
                    onClick={() => {
                      dismiss(t.id);
                      t.action?.run();
                    }}
                  >
                    {t.action.label}
                  </button>
                )}
              </div>
              <button type="button" aria-label="Dismiss" className="h-6 w-6 shrink-0 rounded text-ink-faint hover:text-ink" onClick={() => dismiss(t.id)}>
                <X size={14} className="mx-auto" />
              </button>
            </div>
          );
        })}
      </div>
    </Ctx.Provider>
  );
}

export function useToast() {
  return useContext(Ctx);
}
