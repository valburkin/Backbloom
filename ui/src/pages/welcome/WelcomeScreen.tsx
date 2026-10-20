import { useState } from 'react';
import { CheckCircle2, Loader2, Power } from 'lucide-react';
import { Button } from '../../components/ui';
import { Logo } from '../../components/Logo';
import { useConnection } from '../../app/connection';
import { useConfig } from '../../app/config';
import type { BackendConfig, ServerInfo, SetupState } from '../../api/types';
import { OtherAppNotice } from './OtherAppNotice';
import { welcomeSummary } from './summary';

export interface WelcomeViewProps {
  setup: SetupState;
  config: BackendConfig | null;
  serverInfo: ServerInfo | null;
  starting: boolean;
  error: string | null;
  autostart: boolean;
  onAutostart(v: boolean): void;
  onStart(): void;
  onLookAround(): void;
  /** HyperHDR quit while the welcome was open. */
  otherAppQuit: boolean;
}

/** The first-run welcome as a pure view (see {@link WelcomeScreen}). */
export function WelcomeView(p: WelcomeViewProps) {
  const { setup } = p;
  const rows = welcomeSummary(p.config, p.serverInfo);
  const blocked = setup.otherApp !== null;
  return (
    <div className="flex h-full justify-center overflow-y-auto p-4 sm:p-8">
      <main aria-labelledby="welcome-title" className="my-auto w-full max-w-2xl space-y-5 rounded-2xl border border-line bg-surface p-6 shadow-2xl sm:p-8">
        <header className="flex items-center gap-3">
          <Logo size={40} />
          <div>
            <h1 id="welcome-title" className="text-xl font-semibold">
              Welcome to Overglow
            </h1>
            <p className="text-[13px] text-ink-muted">
              {setup.importedFrom
                ? 'Your HyperHDR settings were imported. Check what Overglow will drive, then start the lighting.'
                : 'Check what Overglow will drive, then start the lighting.'}
            </p>
          </div>
        </header>

        {setup.otherApp && <OtherAppNotice app={setup.otherApp} />}
        {!blocked && p.otherAppQuit && (
          <p role="status" className="flex items-center gap-2 rounded-xl border border-ok/30 bg-ok/10 p-3 text-[13px] text-ok">
            <CheckCircle2 size={16} aria-hidden /> HyperHDR has quit. Overglow can take over your lights now.
          </p>
        )}

        <section aria-labelledby="welcome-summary">
          <h2 id="welcome-summary" className="mb-2 text-[13px] font-semibold uppercase tracking-wide text-ink-faint">
            {setup.importedFrom ? 'Imported' : 'Your setup'}
          </h2>
          <dl className="divide-y divide-line rounded-xl border border-line bg-surface-2">
            {rows.map((r) => (
              <div key={r.label} className="grid gap-1 px-4 py-2.5 text-[13px] sm:grid-cols-[150px_1fr]">
                <dt className="text-ink-muted">{r.label}</dt>
                <dd className="min-w-0 break-words text-ink">
                  {r.value}
                  {r.details && (
                    <ul className="mt-1 space-y-0.5 text-ink-muted">
                      {r.details.map((d) => (
                        <li key={d}>{d}</li>
                      ))}
                    </ul>
                  )}
                </dd>
              </div>
            ))}
            {setup.importedFrom && (
              <div className="grid gap-1 px-4 py-2.5 text-[13px] sm:grid-cols-[150px_1fr]">
                <dt className="text-ink-muted">Imported from</dt>
                <dd className="min-w-0 break-all font-mono text-xs text-ink-muted">{setup.importedFrom}</dd>
              </div>
            )}
          </dl>
        </section>

        {setup.autostart.available && (
          <label className="flex cursor-pointer items-start gap-3 text-[13px]">
            <input
              type="checkbox"
              className="mt-0.5 h-4 w-4 accent-[var(--color-accent)]"
              checked={p.autostart}
              onChange={(e) => p.onAutostart(e.target.checked)}
            />
            <span>
              <span className="font-medium text-ink">Start Overglow when I sign in to Windows</span>
              <span className="block text-ink-muted">Off unless you tick it. You can change it later in the tray menu.</span>
            </span>
          </label>
        )}

        {p.error && (
          <p role="alert" className="text-[13px] text-bad">
            {p.error}
          </p>
        )}

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-5">
          <p className="max-w-sm text-xs text-ink-faint">Nothing is sent to your LEDs until you start the lighting.</p>
          <div className="flex flex-wrap gap-2">
            <Button variant="ghost" onClick={p.onLookAround}>
              Look around first
            </Button>
            <Button
              variant="primary"
              icon={p.starting ? <Loader2 size={16} className="animate-spin" /> : <Power size={16} />}
              disabled={blocked || p.starting}
              title={blocked ? `Quit ${setup.otherApp?.name} first` : undefined}
              onClick={p.onStart}
            >
              {blocked ? `Waiting for ${setup.otherApp?.name} to quit…` : 'Start lighting'}
            </Button>
          </div>
        </footer>
      </main>
    </div>
  );
}

/** Shown instead of the app until the lighting was started once (a fresh install or a HyperHDR import). */
export function WelcomeScreen({ onLookAround }: { onLookAround(): void }) {
  const { setup, serverInfo, api, applySetup } = useConnection();
  const { config } = useConfig();
  const [autostart, setAutostart] = useState(false);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sawOtherApp, setSawOtherApp] = useState(false);
  if (!setup) return null;
  if (setup.otherApp && !sawOtherApp) setSawOtherApp(true);

  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      applySetup(await api.startLighting(setup.autostart.available ? autostart : undefined));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  };

  return (
    <WelcomeView
      setup={setup}
      config={config}
      serverInfo={serverInfo}
      starting={starting}
      error={error}
      autostart={autostart}
      onAutostart={setAutostart}
      onStart={() => void start()}
      onLookAround={onLookAround}
      otherAppQuit={sawOtherApp && !setup.otherApp}
    />
  );
}
