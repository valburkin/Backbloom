import type { ReactNode } from 'react';
import { Check, Lightbulb, MonitorPlay } from 'lucide-react';
import { Button, cx } from '../../components/ui';

/** The steps of placing strips for the first time (the roadmap's first-run layout wizard, without a dialog). */
export const GUIDE_STEPS = ['screen', 'place', 'match', 'test'] as const;
export type GuideStep = (typeof GUIDE_STEPS)[number];

export interface GuideState {
  /** The step shown open; the ones before it count as done. */
  step: number;
  /** The test pattern was shown, so the last step can be finished. */
  tested: boolean;
}

/**
 * A short guided sequence at the top of Place while there's no layout yet, or after an import that only had LED
 * positions: the screen stands on → place strips → match with the lights → test pattern. Each step can be reopened;
 * the steps can be hidden at any time.
 */
export function PlaceGuide({
  imported,
  note,
  state,
  onState,
  onHide,
  furniture,
  furnitureLabel,
  matchLabel,
  onMatch,
  onTest,
}: {
  /** The strips came from LED positions alone (a HyperHDR import). */
  imported: boolean;
  /** More about how the layout was loaded, if anything. */
  note?: string;
  state: GuideState;
  onState(next: GuideState): void;
  onHide(): void;
  /** The "Your screen is on a" choice, and what is chosen (for the step once it is done). */
  furniture: ReactNode;
  furnitureLabel?: string;
  /** Name of the strip "Match" works on, or null when there is none. */
  matchLabel: string | null;
  onMatch(): void;
  onTest(): void;
}) {
  const go = (step: number) => onState({ ...state, step });
  const next = () => go(state.step + 1);
  const finished = state.step >= GUIDE_STEPS.length;

  const steps: Array<{ title: string; body: ReactNode }> = [
    {
      title: state.step > 0 && furnitureLabel ? `Your screen is on a ${furnitureLabel}` : 'Your screen is on a…',
      body: (
        <>
          {furniture}
          <Button size="sm" onClick={next}>
            Next
          </Button>
        </>
      ),
    },
    {
      title: 'Place your strips',
      body: (
        <>
          <p className="text-xs text-ink-faint">
            Select a strip and drag its round handles to where it really runs, or pick a ready-made shape for it. Add strips with Add strip.
          </p>
          <Button size="sm" onClick={next}>
            Next
          </Button>
        </>
      ),
    },
    {
      title: 'Match with your lights',
      body: (
        <>
          <p className="text-xs text-ink-faint">Light up LEDs one by one and mark the corners, so every LED sits where it really is.</p>
          <div className="flex flex-wrap gap-1.5">
            {matchLabel && (
              <Button size="sm" icon={<Lightbulb size={14} />} onClick={onMatch}>
                Match {matchLabel}
              </Button>
            )}
            <Button size="sm" variant="ghost" onClick={next}>
              {matchLabel ? 'Next' : 'Skip'}
            </Button>
          </div>
        </>
      ),
    },
    {
      title: 'Check with the test pattern',
      body: (
        <>
          <p className="text-xs text-ink-faint">Colors run around your screen; your lights should follow them the same way. Press Esc to come back.</p>
          <div className="flex flex-wrap gap-1.5">
            <Button size="sm" icon={<MonitorPlay size={14} />} onClick={onTest}>
              Show test pattern
            </Button>
            <Button size="sm" variant="ghost" onClick={next}>
              {state.tested ? 'Done' : 'Skip'}
            </Button>
          </div>
        </>
      ),
    },
  ];

  return (
    <section aria-labelledby="guide-title" className="space-y-3">
      <div>
        <h2 id="guide-title" className="text-base font-semibold">
          {imported ? 'Your strips need placing' : 'Set up your layout'}
        </h2>
        <p className="mt-0.5 text-xs text-ink-faint">{note ?? 'Four short steps, so every LED shows the part of the screen next to it.'}</p>
      </div>
      {finished ? (
        <p className="flex items-center gap-2 text-[13px] text-ink-muted">
          <Check size={15} className="shrink-0 text-ok" /> All set. Save the layout to use it.
        </p>
      ) : (
        <ol className="space-y-1">
          {steps.map((s, i) => {
            const done = i < state.step;
            const open = i === state.step;
            return (
              <li key={i} className={cx('rounded-xl', open && 'bg-surface-2 p-3')}>
                <button
                  type="button"
                  aria-current={open ? 'step' : undefined}
                  disabled={open}
                  onClick={() => go(i)}
                  className={cx('flex w-full items-center gap-2.5 text-left text-sm', !open && 'rounded-lg px-3 py-1.5 hover:bg-white/[0.04]')}
                >
                  <span
                    className={cx(
                      'flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-[11px] font-semibold',
                      done ? 'bg-ok/15 text-ok' : open ? 'bg-accent text-accent-ink' : 'border border-line-input text-ink-faint',
                    )}
                  >
                    {done ? <Check size={12} /> : i + 1}
                  </span>
                  <span className={cx('font-medium', !open && !done && 'text-ink-muted')}>{s.title}</span>
                </button>
                {open && <div className="mt-2.5 space-y-2.5 pl-7">{s.body}</div>}
              </li>
            );
          })}
        </ol>
      )}
      <button type="button" onClick={onHide} className="text-xs text-ink-faint hover:text-ink hover:underline">
        {finished ? 'Close' : 'Hide these steps'}
      </button>
    </section>
  );
}
