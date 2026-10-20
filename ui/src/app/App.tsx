import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Cpu, House, Loader2, MonitorSmartphone, Palette, Power, ScreenShare, Settings as SettingsIcon, Sparkles, WifiOff } from 'lucide-react';
import { Button, cx, StatusDot } from '../components/ui';
import { Logo } from '../components/Logo';
import { isPageDirty } from '../settings/drafts';
import { useConnection } from './connection';
import { useConfig } from './config';
import { useEditor } from './editor';
import { hrefFor, useRoute, type Route } from './router';
import { LoginScreen } from '../pages/LoginScreen';
import { HomePage } from '../pages/HomePage';
import { LayoutPage } from '../pages/layout/LayoutPage';
import { DevicesPage } from '../pages/DevicesPage';
import { CapturePage } from '../pages/CapturePage';
import { ColorsPage } from '../pages/ColorsPage';
import { EffectsPage } from '../pages/EffectsPage';
import { SettingsPage } from '../pages/settings/SettingsPage';
import { WelcomeScreen } from '../pages/welcome/WelcomeScreen';
import { OtherAppNotice } from '../pages/welcome/OtherAppNotice';
import { useAction } from './useAction';

const NAV: Array<{ route: Route; label: string; icon: ReactNode }> = [
  { route: 'home', label: 'Home', icon: <House size={20} /> },
  { route: 'layout', label: 'Layout', icon: <MonitorSmartphone size={20} /> },
  { route: 'devices', label: 'Devices', icon: <Cpu size={20} /> },
  { route: 'capture', label: 'Screen capture', icon: <ScreenShare size={20} /> },
  { route: 'colors', label: 'Colors', icon: <Palette size={20} /> },
  { route: 'effects', label: 'Effects & Music', icon: <Sparkles size={20} /> },
  { route: 'settings', label: 'Settings', icon: <SettingsIcon size={20} /> },
];

function ConnectionPill() {
  const { status, backend, auth } = useConnection();
  const tone = status === 'open' ? 'ok' : status === 'reconnecting' || status === 'connecting' ? 'warn' : 'bad';
  const label =
    status === 'open'
      ? backend.mode === 'mock'
        ? 'Demo mode'
        : auth === 'readOnly'
          ? 'Connected · view only'
          : 'Connected'
      : status === 'reconnecting'
        ? 'Reconnecting…'
        : status === 'connecting'
          ? 'Connecting…'
          : 'Offline';
  return (
    <div className="flex items-center gap-2.5 rounded-xl border border-line bg-surface-2 px-3 py-2 xl:px-3" title={backend.url}>
      <StatusDot tone={tone} />
      <span className="hidden text-xs text-ink-muted xl:inline">{label}</span>
      <span className="sr-only xl:hidden">{label}</span>
    </div>
  );
}

function Shell() {
  const [loc, go] = useRoute();
  const { dirty: layoutDirty } = useEditor();
  const { drafts } = useConfig();
  const main = useRef<HTMLElement>(null);
  useEffect(() => {
    main.current?.focus({ preventScroll: true });
  }, [loc.route]);
  const unsaved = (r: Route) => isPageDirty(drafts[r]) || (layoutDirty && (r === 'layout' || r === 'devices'));

  return (
    <div className="flex h-full min-h-0 flex-col sm:flex-row">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface-3 focus:px-3 focus:py-2">
        Skip to content
      </a>
      <nav
        aria-label="Main"
        className={cx(
          'order-last flex shrink-0 border-line bg-surface/95 backdrop-blur',
          'border-t px-1 pb-[env(safe-area-inset-bottom)] sm:order-first sm:w-[72px] sm:flex-col sm:border-r sm:border-t-0 sm:bg-surface/60 sm:px-3 sm:py-4 xl:w-56',
        )}
      >
        <div className="mb-6 hidden items-center gap-2.5 px-1.5 sm:flex">
          <Logo />
          <span className="hidden text-[15px] font-semibold tracking-tight xl:inline">Overglow</span>
        </div>
        <ul className="flex w-full justify-around gap-1 sm:flex-col sm:justify-start">
          {NAV.map((n) => {
            const active = n.route === loc.route;
            const showDot = unsaved(n.route);
            return (
              <li key={n.route}>
                <a
                  href={hrefFor(n.route)}
                  aria-current={active ? 'page' : undefined}
                  onClick={(e) => {
                    e.preventDefault();
                    go(n.route);
                  }}
                  className={cx(
                    'relative flex h-12 items-center justify-center gap-3 rounded-xl px-2.5 text-sm font-medium transition-colors sm:h-11 sm:justify-start sm:px-3',
                    active ? 'bg-surface-3 text-ink' : 'text-ink-muted hover:bg-surface-2 hover:text-ink',
                  )}
                  title={n.label}
                >
                  {active && <span className="absolute left-0 top-2.5 hidden h-6 w-[3px] rounded-r bg-accent sm:block" aria-hidden />}
                  <span className={active ? 'text-accent-strong' : ''}>{n.icon}</span>
                  <span className="sr-only xl:not-sr-only xl:whitespace-nowrap">{n.label}</span>
                  {showDot && (
                    <span className="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-warn xl:static xl:ml-auto" aria-label="Unsaved changes" />
                  )}
                </a>
              </li>
            );
          })}
        </ul>
        <div className="mt-auto hidden sm:block">
          <ConnectionPill />
        </div>
      </nav>
      <main id="main" ref={main} tabIndex={-1} className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden outline-none">
        <ReconnectBanner />
        <SetupBanner />
        <div className="min-h-0 flex-1">
          {loc.route === 'home' && <HomePage />}
          {loc.route === 'layout' && <LayoutPage />}
          {loc.route === 'devices' && <DevicesPage />}
          {loc.route === 'capture' && <CapturePage />}
          {loc.route === 'colors' && <ColorsPage />}
          {loc.route === 'effects' && <EffectsPage />}
          {loc.route === 'settings' && <SettingsPage tab={loc.sub} onTab={(t) => go('settings', t)} />}
        </div>
      </main>
    </div>
  );
}

function ReconnectBanner() {
  const { status } = useConnection();
  if (status === 'open' || status === 'idle') return null;
  return (
    <div role="status" className="flex items-center gap-2 border-b border-warn/30 bg-warn/10 px-6 py-2 text-[13px] text-warn">
      <WifiOff size={15} />
      Lost connection to the lighting service. Reconnecting… your edits are kept.
    </div>
  );
}

/** Why the LEDs are off although everything is switched on: HyperHDR runs, or the lighting wasn't started yet. */
function SetupBanner() {
  const { setup, api, applySetup, auth } = useConnection();
  const { run, busy } = useAction();
  if (!setup?.outputHeld) return null;
  if (setup.otherApp)
    return (
      <div className="border-b border-line px-4 py-3 sm:px-6">
        <OtherAppNotice app={setup.otherApp} compact />
      </div>
    );
  if (setup.firstRunCompleted) return null;
  return (
    <div role="status" className="flex flex-wrap items-center gap-3 border-b border-accent/30 bg-accent/10 px-6 py-2 text-[13px] text-ink">
      <Power size={15} className="text-accent-strong" aria-hidden />
      <span className="flex-1">Overglow hasn’t started your lights yet: nothing is sent to the LEDs until you start the lighting.</span>
      <Button
        size="sm"
        variant="primary"
        disabled={auth !== 'admin' || busy === 'start-lighting'}
        onClick={() => void run('start-lighting', 'Couldn’t start the lighting', async () => applySetup(await api.startLighting()))}
      >
        Start lighting
      </Button>
    </div>
  );
}

function Splash({ children }: { children: ReactNode }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4 p-6 text-center">
      <Logo size={44} />
      {children}
    </div>
  );
}

export function App() {
  const { status, auth, sessionError, backend, serverInfo, setup } = useConnection();
  const everReady = useRef(false);
  const [lookingAround, setLookingAround] = useState(false);
  if (serverInfo) everReady.current = true;

  if (auth === 'login' && status === 'open') return <LoginScreen />;

  if (!everReady.current) {
    if (sessionError)
      return (
        <Splash>
          <p className="text-base font-medium">The lighting service isn’t ready</p>
          <p className="max-w-md text-sm text-ink-muted">{sessionError}</p>
          <button type="button" className="text-sm text-accent-strong underline" onClick={() => window.location.reload()}>
            Try again
          </button>
        </Splash>
      );
    return (
      <Splash>
        <p className="flex items-center gap-2 text-sm text-ink-muted">
          <Loader2 size={16} className="animate-spin" />
          {status === 'reconnecting' ? 'Waiting for the lighting service…' : 'Connecting to the lighting service…'}
        </p>
        <p className="text-xs text-ink-faint">{backend.url}</p>
      </Splash>
    );
  }
  if (setup && !setup.firstRunCompleted && !lookingAround) return <WelcomeScreen onLookAround={() => setLookingAround(true)} />;
  return <Shell />;
}
