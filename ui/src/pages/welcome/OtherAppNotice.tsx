import { Loader2, TriangleAlert } from 'lucide-react';
import type { SetupState } from '../../api/types';

/** HyperHDR (or Hyperion) drives the LEDs right now: why Backbloom waits and how to let it take over. */
export function OtherAppNotice({ app, compact }: { app: NonNullable<SetupState['otherApp']>; compact?: boolean }) {
  return (
    <div role="status" className="flex gap-3 rounded-xl border border-warn/30 bg-warn/10 p-4 text-[13px] text-ink">
      <TriangleAlert size={18} className="mt-0.5 shrink-0 text-warn" aria-hidden />
      <div className="min-w-0 space-y-2">
        <p className="font-medium text-warn">
          {app.name} is controlling your lights right now. Quit {app.name} (tray icon → Quit) to let Backbloom take over.
        </p>
        <p className="flex items-center gap-2 text-ink-muted">
          <Loader2 size={14} className="animate-spin" aria-hidden />
          Backbloom sends nothing to your LEDs while {app.name} runs, so they don’t flicker. It notices when {app.name} quits.
        </p>
        {!compact && (
          <p className="text-ink-muted">
            <span className="font-medium text-ink">Don’t start {app.name} with Windows:</span> open Windows Settings → Apps → Startup (or Task
            Manager → Startup apps) and switch {app.name} off. Backbloom has its own copy of your settings, so you can uninstall {app.name} once you’re happy.
          </p>
        )}
      </div>
    </div>
  );
}
