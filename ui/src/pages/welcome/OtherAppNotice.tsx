import { Loader2 } from 'lucide-react';
import type { SetupState } from '../../api/types';

/** HyperHDR (or Hyperion) drives the LEDs right now: Backbloom waits and takes over when it quits. */
export function OtherAppNotice({ app }: { app: NonNullable<SetupState['otherApp']> }) {
  return (
    <p role="status" className="flex gap-3 rounded-xl border border-warn/30 bg-warn/10 px-4 py-3 text-[13px] text-ink">
      <Loader2 size={16} className="mt-0.5 shrink-0 text-warn motion-safe:animate-spin" aria-hidden />
      <span className="min-w-0">
        <span className="font-medium text-warn">{app.name} is controlling your lights right now.</span> Backbloom waits and takes over by itself when
        you quit {app.name} (its tray icon → Quit).
      </span>
    </p>
  );
}
