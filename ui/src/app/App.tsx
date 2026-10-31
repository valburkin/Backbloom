import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from 'react';
import { Gamepad2, House, Loader2, MonitorSmartphone, Palette, Power, Settings as SettingsIcon, Sparkles, WifiOff } from 'lucide-react';
import { Button, cx, StatusDot } from '../components/ui';
import { Logo } from '../components/Logo';
import { isPageDirty } from '../settings/drafts';
import { useConnection } from './connection';
import { useConfig } from './config';
import { useEditor } from './editor';
import { hrefFor, useFocusTarget, useRoute, type Route } from './router';
import { LoginScreen } from '../pages/LoginScreen';
import { HomePage } from '../pages/HomePage';
import { DevicesPage } from '../pages/DevicesPage';
import { CapturePage } from '../pages/CapturePage';
import { ColorsPage } from '../pages/ColorsPage';
import { CalibrationPage } from '../pages/CalibrationPage';
import { EffectsPage } from '../pages/EffectsPage';
import { GamesPage } from '../pages/GamesPage';
import { SettingsPage } from '../pages/settings/SettingsPage';
import { WelcomeScreen } from '../pages/welcome/WelcomeScreen';
import { OtherAppNotice } from '../pages/welcome/OtherAppNotice';
import { useAction } from './useAction';
import { ModeSwitch } from '../control/ModeSwitch';
import type { LightMode } from '../api/types';

// The layout editor is the biggest page; it loads when first opened.
const LayoutPage = lazy(() => import('../pages/layout/LayoutPage').then((m) => ({ default: m.LayoutPage })));

interface NavItem {
  label: string;
  /** Label in the phone's bottom bar, when `label` is too long for it. */
  short?: string;
  icon: ReactNode;
  /** Pages of the section; more than one shows tabs above them. */
  pages: ReadonlyArray<{ route: Route; label: string }>;
}

const HOME: NavItem = { label: 'Home', icon: <House size={20} />, pages: [{ route: 'home', label: 'Home' }] };
const COLORS: NavItem = { label: 'Colors', icon: <Palette size={20} />, pages: [{ route: 'colors', label: 'Colors' }] };

// Each mode is its own app (docs/ui-guidelines.md): its pages come first in the sidebar, under the Mode switch.
const MODE_NAV: Record<LightMode, readonly NavItem[]> = {
  ambient: [
    HOME,
    COLORS,
    { label: 'Effects & Music', short: 'Effects', icon: <Sparkles size={20} />, pages: [{ route: 'effects', label: 'Effects & Music' }] },
  ],
  game: [HOME, COLORS, { label: 'Games', icon: <Gamepad2 size={20} />, pages: [{ route: 'games', label: 'Games' }] }],
};

// What both modes share, at the bottom of the sidebar: the hardware and the app itself.
const SHARED_NAV: readonly NavItem[] = [
  {
    label: 'Setup',
    icon: <MonitorSmartphone size={20} />,
    pages: [
      { route: 'layout', label: 'Layout' },
      { route: 'devices', label: 'Devices' },
      { route: 'capture', label: 'Screen' },
      { route: 'calibration', label: 'Calibration' },
    ],
  },
  { label: 'Settings', icon: <SettingsIcon size={20} />, pages: [{ route: 'settings', label: 'Settings' }] },
];

const NAV: Record<LightMode, readonly NavItem[]> = {
  ambient: [...MODE_NAV.ambient, ...SHARED_NAV],
  game: [...MODE_NAV.game, ...SHARED_NAV],
};

/** The profile a `#/layout/profile:<id>` link opens (the id URL-encoded, see hrefForProfile), or null. */
function profileLink(sub: string | null): string | null {
  if (!sub?.startsWith('profile:')) return null;
  try {
    return decodeURIComponent(sub.slice('profile:'.length));
  } catch {
    return null;
  }
}

const sectionOf = (nav: readonly NavItem[], route: Route) => nav.find((n) => n.pages.some((p) => p.route === route)) ?? nav[0];

/** Tabs between the pages of a section (Setup: Layout / Devices / Screen / Calibration). */
function SectionTabs({ nav, route, go, unsaved }: { nav: readonly NavItem[]; route: Route; go: (r: Route) => void; unsaved: (r: Route) => boolean }) {
  const section = sectionOf(nav, route);
  if (section.pages.length < 2) return null;
  return (
    <nav aria-label={section.label} className="flex gap-1 border-b border-line px-4 pt-2 sm:px-6">
      {section.pages.map((p) => {
        const active = p.route === route;
        return (
          <a
            key={p.route}
            href={hrefFor(p.route)}
            aria-current={active ? 'page' : undefined}
            onClick={(e) => {
              e.preventDefault();
              go(p.route);
            }}
            className={cx(
              '-mb-px inline-flex h-10 items-center gap-2 border-b-2 px-3 text-sm font-medium transition-colors',
              active ? 'border-accent text-ink' : 'border-transparent text-ink-muted hover:text-ink',
            )}
          >
            {p.label}
            {unsaved(p.route) && <span className="h-1.5 w-1.5 rounded-full bg-warn" aria-label="Unsaved changes" />}
          </a>
        );
      })}
    </nav>
  );
}

function ConnectionPill() {
  const { status, backend } = useConnection();
  const tone = status === 'open' ? 'ok' : status === 'reconnecting' || status === 'connecting' ? 'warn' : 'bad';
  const label =
    status === 'open'
      ? backend.mode === 'mock'
        ? 'Demo mode'
        : 'Connected'
      : status === 'reconnecting'
        ? 'Reconnecting…'
        : status === 'connecting'
          ? 'Connecting…'
          : 'Offline';
  return (
    <div className="flex items-center gap-2.5 rounded-xl border border-line bg-surface-2 px-3 py-2" title={backend.url}>
      <StatusDot tone={tone} />
      <span className="hidden text-xs text-ink-muted lg:inline">{label}</span>
      <span className="sr-only lg:hidden">{label}</span>
    </div>
  );
}

function Shell() {
  const [loc, go] = useRoute();
  const { dirty: layoutDirty } = useEditor();
  const { drafts } = useConfig();
  const main = useRef<HTMLElement>(null);
  useFocusTarget(loc);
  useEffect(() => {
    main.current?.focus({ preventScroll: true });
  }, [loc.route]);
  const unsaved = (r: Route) => isPageDirty(drafts[r]) || (layoutDirty && (r === 'layout' || r === 'devices'));
  const mode = useConnection().serverInfo?.mode?.mode;
  const nav = NAV[mode ?? 'ambient'];
  // Effects & Music leads Home once Game mode is known (it shows only the screen). Games stays reachable in Ambient:
  // Ambient's Home links to it, so games can be added without switching the lights to Game mode.
  const offered = loc.route === 'games' || nav.some((n) => n.pages.some((p) => p.route === loc.route));
  useEffect(() => {
    // replace, so that Back doesn't return to the page that isn't there
    if (mode && !offered) window.location.replace(hrefFor('home'));
  }, [mode, offered]);

  return (
    <div className="flex h-full min-h-0 flex-col sm:flex-row">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface-3 focus:px-3 focus:py-2">
        Skip to content
      </a>
      <nav
        aria-label="Main"
        className={cx(
          'order-last flex shrink-0 border-line bg-surface/95 backdrop-blur',
          'border-t px-1 pb-[env(safe-area-inset-bottom)] sm:order-first sm:w-[72px] sm:flex-col sm:border-r sm:border-t-0 sm:bg-surface/60 sm:px-3 sm:py-4 lg:w-56',
        )}
      >
        <div className="mb-6 hidden items-center gap-2.5 px-1.5 sm:flex">
          <Logo />
          <span className="hidden text-[15px] font-semibold tracking-tight lg:inline">Backbloom</span>
        </div>
        <ModeSwitch className="mb-5 hidden sm:block" />
        <ul className="flex w-full justify-around gap-1 sm:flex-1 sm:flex-col sm:justify-start">
          {nav.map((n) => {
            const shared = n === SHARED_NAV[0];
            const active = n === sectionOf(nav, loc.route);
            const showDot = n.pages.some((p) => unsaved(p.route));
            const target = n.pages[0].route;
            return (
              <li key={n.label} className={cx('min-w-0 flex-1 sm:flex-none', shared && 'sm:mt-auto sm:border-t sm:border-line sm:pt-3')}>
                <a
                  href={hrefFor(target)}
                  aria-current={active ? 'page' : undefined}
                  onClick={(e) => {
                    e.preventDefault();
                    if (!active) go(target);
                  }}
                  className={cx(
                    'relative flex h-14 flex-col items-center justify-center gap-0.5 rounded-xl px-1 text-[11px] font-medium transition-colors',
                    'sm:h-11 sm:flex-row sm:justify-start sm:gap-3 sm:px-3 sm:text-sm',
                    active ? 'bg-surface-3 text-ink' : 'text-ink-muted hover:bg-surface-2 hover:text-ink',
                  )}
                  title={n.label}
                >
                  {active && <span className="absolute left-0 top-2.5 hidden h-6 w-[3px] rounded-r bg-accent sm:block" aria-hidden />}
                  <span className={active ? 'text-accent-strong' : ''}>{n.icon}</span>
                  <span className="max-w-full truncate sm:sr-only lg:not-sr-only lg:whitespace-nowrap">
                    <span className="sm:hidden">{n.short ?? n.label}</span>
                    <span className="hidden sm:inline">{n.label}</span>
                  </span>
                  {showDot && (
                    <span className="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-warn lg:static lg:ml-auto" aria-label="Unsaved changes" />
                  )}
                </a>
              </li>
            );
          })}
        </ul>
        <div className="mt-3 hidden sm:block">
          <ConnectionPill />
        </div>
      </nav>
      <main id="main" ref={main} tabIndex={-1} className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden outline-none">
        <ReconnectBanner />
        <SetupBanner />
        <SectionTabs nav={nav} route={loc.route} go={go} unsaved={unsaved} />
        <div className="min-h-0 flex-1">
          {loc.route === 'home' && <HomePage />}
          {loc.route === 'games' && offered && <GamesPage />}
          {loc.route === 'layout' && (
            <Suspense fallback={null}>
              <LayoutPage openProfile={profileLink(loc.sub)} />
            </Suspense>
          )}
          {loc.route === 'devices' && <DevicesPage />}
          {loc.route === 'capture' && <CapturePage />}
          {loc.route === 'colors' && <ColorsPage />}
          {loc.route === 'calibration' && <CalibrationPage />}
          {loc.route === 'effects' && offered && <EffectsPage />}
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
      Lost connection to Backbloom. Reconnecting… Your changes are kept.
    </div>
  );
}

/** Why the LEDs are dark although everything is switched on: HyperHDR runs, or the lights weren't turned on yet. */
function SetupBanner() {
  const { setup, api, applySetup, auth } = useConnection();
  const { run, busy } = useAction();
  if (!setup?.outputHeld) return null;
  if (setup.otherApp)
    return (
      <div className="border-b border-line px-4 py-3 sm:px-6">
        <OtherAppNotice app={setup.otherApp} />
      </div>
    );
  if (setup.firstRunCompleted) return null;
  return (
    <div role="status" className="flex flex-wrap items-center gap-3 border-b border-accent/30 bg-accent/10 px-6 py-2 text-[13px] text-ink">
      <Power size={15} className="text-accent-strong" aria-hidden />
      <span className="flex-1">Your lights are waiting: Backbloom sends nothing to them until you turn them on.</span>
      <Button
        size="sm"
        variant="primary"
        disabled={auth !== 'admin' || busy === 'start-lighting'}
        onClick={() => void run('start-lighting', 'Couldn’t turn on the lights', async () => applySetup(await api.startLighting()))}
      >
        Turn on lights
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
          <p className="text-base font-medium">Backbloom isn’t responding</p>
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
          {status === 'reconnecting' ? 'Waiting for Backbloom…' : 'Connecting to Backbloom…'}
        </p>
        <p className="text-xs text-ink-faint">{backend.url}</p>
      </Splash>
    );
  }
  if (setup && !setup.firstRunCompleted && !lookingAround) return <WelcomeScreen onLookAround={() => setLookingAround(true)} />;
  return <Shell />;
}
