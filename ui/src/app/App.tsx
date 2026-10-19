import { useEffect, useRef, type ReactNode } from 'react';
import { Cpu, House, Loader2, MonitorSmartphone, Settings as SettingsIcon, WifiOff } from 'lucide-react';
import { cx, StatusDot } from '../components/ui';
import { Logo } from '../components/Logo';
import { useConnection } from './connection';
import { useEditor } from './editor';
import { useRoute, type Route } from './router';
import { LoginScreen } from '../pages/LoginScreen';
import { HomePage } from '../pages/HomePage';
import { LayoutPage } from '../pages/layout/LayoutPage';
import { DevicesPage } from '../pages/DevicesPage';
import { SettingsPage } from '../pages/SettingsPage';

const NAV: Array<{ route: Route; label: string; icon: ReactNode }> = [
  { route: 'home', label: 'Home', icon: <House size={20} /> },
  { route: 'layout', label: 'Layout', icon: <MonitorSmartphone size={20} /> },
  { route: 'devices', label: 'Devices', icon: <Cpu size={20} /> },
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
  const [route, go] = useRoute();
  const { dirty } = useEditor();
  const main = useRef<HTMLElement>(null);
  useEffect(() => {
    main.current?.focus({ preventScroll: true });
  }, [route]);

  return (
    <div className="flex h-full min-h-0">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface-3 focus:px-3 focus:py-2">
        Skip to content
      </a>
      <nav aria-label="Main" className="flex w-[72px] shrink-0 flex-col border-r border-line bg-surface/60 px-3 py-4 xl:w-56">
        <div className="mb-6 flex items-center gap-2.5 px-1.5">
          <Logo />
          <span className="hidden text-[15px] font-semibold tracking-tight xl:inline">Ambient</span>
        </div>
        <ul className="flex flex-col gap-1">
          {NAV.map((n) => {
            const active = n.route === route;
            const showDot = dirty && (n.route === 'layout' || n.route === 'devices');
            return (
              <li key={n.route}>
                <a
                  href={`#/${n.route}`}
                  aria-current={active ? 'page' : undefined}
                  onClick={(e) => {
                    e.preventDefault();
                    go(n.route);
                  }}
                  className={cx(
                    'relative flex h-11 items-center gap-3 rounded-xl px-3 text-sm font-medium transition-colors',
                    active ? 'bg-surface-3 text-ink' : 'text-ink-muted hover:bg-surface-2 hover:text-ink',
                  )}
                  title={n.label}
                >
                  {active && <span className="absolute left-0 top-2.5 h-6 w-[3px] rounded-r bg-accent" aria-hidden />}
                  <span className={active ? 'text-accent-strong' : ''}>{n.icon}</span>
                  <span className="hidden xl:inline">{n.label}</span>
                  {showDot && (
                    <span className="absolute right-2 top-2 h-2 w-2 rounded-full bg-warn xl:static xl:ml-auto" aria-label="Unsaved changes" />
                  )}
                </a>
              </li>
            );
          })}
        </ul>
        <div className="mt-auto">
          <ConnectionPill />
        </div>
      </nav>
      <main id="main" ref={main} tabIndex={-1} className="min-w-0 flex-1 overflow-hidden outline-none">
        <ReconnectBanner />
        {route === 'home' && <HomePage />}
        {route === 'layout' && <LayoutPage />}
        {route === 'devices' && <DevicesPage />}
        {route === 'settings' && <SettingsPage />}
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

function Splash({ children }: { children: ReactNode }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4 p-6 text-center">
      <Logo size={44} />
      {children}
    </div>
  );
}

export function App() {
  const { status, auth, sessionError, backend, serverInfo } = useConnection();
  const everReady = useRef(false);
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
  return <Shell />;
}
