import type { ReactNode } from 'react';
import { Bookmark, House, MonitorSmartphone, Palette, Settings as SettingsIcon, Sparkles } from 'lucide-react';
import type { Route } from './router';

export interface NavItem {
  label: string;
  /** Label in the phone's bottom bar, when `label` is too long for it. */
  short?: string;
  icon: ReactNode;
  /** Pages of the section; more than one shows tabs above them. */
  pages: ReadonlyArray<{ route: Route; label: string }>;
}

// What the lights show and how they look (docs/ui-guidelines.md, Structure).
export const MAIN_NAV: readonly NavItem[] = [
  { label: 'Home', icon: <House size={20} />, pages: [{ route: 'home', label: 'Home' }] },
  { label: 'Effects', icon: <Sparkles size={20} />, pages: [{ route: 'effects', label: 'Effects' }] },
  { label: 'Presets', icon: <Bookmark size={20} />, pages: [{ route: 'presets', label: 'Presets' }] },
  { label: 'Colors', icon: <Palette size={20} />, pages: [{ route: 'colors', label: 'Colors' }] },
];

// At the bottom of the sidebar: the hardware and the app itself.
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

export const NAV: readonly NavItem[] = [...MAIN_NAV, ...SHARED_NAV];

/** The sidebar section that holds the page. */
export const sectionOf = (nav: readonly NavItem[], route: Route) => nav.find((n) => n.pages.some((p) => p.route === route));
