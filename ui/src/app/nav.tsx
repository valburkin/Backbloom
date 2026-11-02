import type { ReactNode } from 'react';
import { Gamepad2, House, MonitorSmartphone, Palette, Settings as SettingsIcon, Sparkles } from 'lucide-react';
import type { LightMode } from '../api/types';
import type { Route } from './router';

export interface NavItem {
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
export const SHARED_NAV: readonly NavItem[] = [
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

export const NAV: Record<LightMode, readonly NavItem[]> = {
  ambient: [...MODE_NAV.ambient, ...SHARED_NAV],
  game: [...MODE_NAV.game, ...SHARED_NAV],
};

/** The sidebar section that holds the page, or none (a page the mode doesn't offer marks no section as current). */
export const sectionOf = (nav: readonly NavItem[], route: Route) => nav.find((n) => n.pages.some((p) => p.route === route));

/**
 * Whether Games stays open: it is open and the mode was Game when it opened. The mode can change under it (the running
 * game quits or is removed, or the user chooses Ambient, as the page suggests), and the page stays until the user
 * leaves it. Held only in memory: a reload or a new sign-in in Ambient leads Home.
 */
export const gamesStayOpen = (route: Route, mode: LightMode | undefined, wasOpen: boolean) => route === 'games' && (mode === 'game' || wasOpen);

/**
 * Whether the page shows. A page the mode doesn't offer (Effects & Music in Game mode, Games in Ambient) leads Home,
 * but only once the mode is known, and not Games while it stays open (gamesStayOpen).
 */
export const pageShown = (route: Route, mode: LightMode | undefined, gamesOpen: boolean) =>
  mode === undefined || (route === 'games' && gamesOpen) || NAV[mode].some((n) => n.pages.some((p) => p.route === route));
