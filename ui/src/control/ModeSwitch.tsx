import { useRef, type ReactNode } from 'react';
import { Gamepad2, Sofa } from 'lucide-react';
import { CHOICE_ACTIVE, cx } from '../components/ui';
import { useConnection } from '../app/connection';
import { useAction } from '../app/useAction';
import type { LightMode, ModeState } from '../api/types';

const MODES: Array<{ id: LightMode; label: string; hint: string; icon: ReactNode }> = [
  { id: 'ambient', label: 'Ambient', hint: 'Ambient mode: movies, music and everyday use', icon: <Sofa size={16} /> },
  { id: 'game', label: 'Game', hint: 'Game mode: the lights follow your game, quicker and richer. Your games turn it on while they run.', icon: <Gamepad2 size={16} /> },
];

/** What the switch says under it: which game decides the mode now, if one does. */
export function modeNote(state: ModeState | undefined): string | null {
  if (state?.gameProfile) return state.saved === 'game' ? `Playing “${state.gameProfile}”` : `While “${state.gameProfile}” runs`;
  if (state?.dismissedGame) return `Off for “${state.dismissedGame}”`;
  return null;
}

function hintFor(id: LightMode, state: ModeState | undefined, fallback: string): string {
  if (id === 'game' && state?.gameProfile)
    return state.saved === 'game'
      ? `Game mode is on, and “${state.gameProfile}” runs. Choose Ambient to turn it off.`
      : `Game mode is on while “${state.gameProfile}” runs. Ambient turns it off until the game quits.`;
  if (id === 'game' && state?.dismissedGame) return `Turn Game mode back on for “${state.dismissedGame}”. Ambient comes back when it quits.`;
  return fallback;
}

/**
 * Ambient or Game mode, at the top of the sidebar on every page (in Home's header on a phone): chosen here, or Game
 * while a game's profile runs. Labels show beside the icons in a row, except in the narrow icon-only sidebar. Arrow
 * keys move between the two like a radio group; while a switch is under way the buttons keep their focus.
 */
export function ModeSwitch({ className }: { className?: string }) {
  const { serverInfo, api } = useConnection();
  const { run, busy } = useAction();
  const buttons = useRef<Array<HTMLButtonElement | null>>([]);
  const state = serverInfo?.mode;
  const current = state?.mode ?? 'ambient';
  const disabled = !state || busy === 'lightMode';
  const choose = (mode: LightMode) => {
    if (mode !== current && !disabled) void run('lightMode', 'Couldn’t switch the mode', () => api.setMode(mode));
  };
  const note = modeNote(state);

  return (
    <div className={className}>
      <div role="radiogroup" aria-label="Mode" className="flex gap-1 rounded-xl border border-line-input bg-bg/60 p-1 sm:flex-col lg:flex-row">
        {MODES.map((m, i) => {
          const active = m.id === current;
          return (
            <button
              key={m.id}
              ref={(el) => {
                buttons.current[i] = el;
              }}
              type="button"
              role="radio"
              aria-checked={active}
              aria-label={m.label}
              aria-disabled={disabled || undefined}
              title={hintFor(m.id, state, m.hint)}
              tabIndex={active ? 0 : -1}
              onClick={() => choose(m.id)}
              onKeyDown={(e) => {
                if (!['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'].includes(e.key)) return;
                e.preventDefault();
                const next = (i + 1) % MODES.length;
                buttons.current[next]?.focus();
                choose(MODES[next].id);
              }}
              className={cx(
                'flex h-8 flex-1 items-center justify-center gap-1.5 rounded-lg px-2.5 text-[13px] font-medium transition-colors sm:flex-none lg:flex-1',
                'aria-disabled:cursor-not-allowed',
                active ? CHOICE_ACTIVE : 'text-ink-muted hover:text-ink aria-disabled:opacity-60',
              )}
            >
              <span className={active ? 'text-accent-strong' : ''}>{m.icon}</span>
              <span className="sm:sr-only lg:not-sr-only">{m.label}</span>
            </button>
          );
        })}
      </div>
      {note && (
        <p className="mt-1.5 hidden truncate px-1 text-[11px] text-ink-faint lg:block" title={hintFor('game', state, '')}>
          {note}
        </p>
      )}
    </div>
  );
}
