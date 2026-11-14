import type { ReactNode } from 'react';
import { Info, LifeBuoy, Plug, SlidersHorizontal, Smartphone } from 'lucide-react';
import { CHOICE_TRACK, choiceOption, cx, useChoiceThumb } from '../../components/ui';
import { PageFrame } from '../../components/PageFrame';
import { usePageSettings } from '../../app/config';
import { useExpertTools } from '../../app/expert';
import { GeneralTab } from './GeneralTab';
import { RemoteAccessTab } from './RemoteAccess';
import { IntegrationsTab } from './IntegrationsTab';
import { LogsTab } from './LogsTab';
import { AboutTab } from './AboutTab';
import { STARTUP_LABELS } from './StartupIdle';

export const SETTINGS_LABELS: Record<string, string> = {
  'general.name': 'Name',
  'mqtt.host': 'Broker address',
  'mqtt.port': 'Broker port',
  'logger.level': 'Log detail',
  ...STARTUP_LABELS,
};

/** Stored as soon as they change; network and MQTT settings wait for Save (a half-typed port or address would apply). */
const AUTO_SAVED = ['general', 'logger', 'foregroundEffect', 'backgroundEffect'];

interface Tab {
  id: string;
  label: string;
  icon: ReactNode;
  /** Shows the page's settings bar (Save / Discard). */
  config: boolean;
  /** Only with expert tools on (Settings → General). */
  expert?: boolean;
}

const TABS: Tab[] = [
  { id: 'general', label: 'General', icon: <SlidersHorizontal size={16} />, config: true },
  { id: 'remote-access', label: 'Remote access', icon: <Smartphone size={16} />, config: false },
  { id: 'integrations', label: 'Integrations', icon: <Plug size={16} />, config: true },
  { id: 'about', label: 'About', icon: <Info size={16} />, config: false },
  { id: 'support', label: 'Support', icon: <LifeBuoy size={16} />, config: true, expert: true },
];

/** Tabs of earlier builds, so their links (bookmarks, docs) still land on the same settings. */
const RENAMED: Record<string, string> = { security: 'remote-access', network: 'remote-access', mqtt: 'integrations', logs: 'support' };

export function SettingsPage({ tab, onTab }: { tab: string | null; onTab: (t: string) => void }) {
  const ps = usePageSettings('settings', SETTINGS_LABELS, AUTO_SAVED);
  const expert = useExpertTools();
  const tabs = TABS.filter((t) => !t.expert || expert);
  const id = tab && tab in RENAMED ? RENAMED[tab] : tab;
  const active = tabs.find((t) => t.id === id) ?? tabs[0];
  const { ref, thumb } = useChoiceThumb(`${active.id}|${tabs.length}`);
  return (
    <PageFrame
      title="Settings"
      subtitle="Everything about the app itself: startup, backups, remote access and integrations."
      settings={active.config || ps.dirty ? ps : undefined}
      toolbar={
        <div ref={ref} role="tablist" aria-label="Settings sections" className={CHOICE_TRACK}>
          {thumb}
          {tabs.map((t) => {
            const selected = t.id === active.id;
            return (
              <button
                key={t.id}
                type="button"
                role="tab"
                id={`tab-${t.id}`}
                aria-selected={selected}
                aria-controls={`panel-${t.id}`}
                tabIndex={selected ? 0 : -1}
                onClick={() => onTab(t.id)}
                onKeyDown={(e) => {
                  const i = tabs.indexOf(active);
                  const next = e.key === 'ArrowRight' ? tabs[(i + 1) % tabs.length] : e.key === 'ArrowLeft' ? tabs[(i - 1 + tabs.length) % tabs.length] : null;
                  if (next) {
                    e.preventDefault();
                    onTab(next.id);
                    requestAnimationFrame(() => document.getElementById(`tab-${next.id}`)?.focus());
                  }
                }}
                className={cx(choiceOption(selected), 'h-9 shrink-0 gap-2 px-4 text-[13px]')}
              >
                <span className={selected ? 'text-accent-strong' : ''}>{t.icon}</span>
                {t.label}
              </button>
            );
          })}
        </div>
      }
    >
      <div role="tabpanel" id={`panel-${active.id}`} aria-labelledby={`tab-${active.id}`} className="space-y-6">
        {active.id === 'general' && <GeneralTab ps={ps} />}
        {active.id === 'remote-access' && <RemoteAccessTab ps={ps} />}
        {active.id === 'integrations' && <IntegrationsTab ps={ps} />}
        {active.id === 'about' && <AboutTab />}
        {active.id === 'support' && <LogsTab ps={ps} />}
      </div>
    </PageFrame>
  );
}
