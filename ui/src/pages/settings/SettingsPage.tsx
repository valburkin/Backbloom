import type { ReactNode } from 'react';
import { Info, Network, Radio, ScrollText, Shield, SlidersHorizontal } from 'lucide-react';
import { cx } from '../../components/ui';
import { PageFrame } from '../../components/PageFrame';
import { usePageSettings } from '../../app/config';
import { GeneralTab } from './GeneralTab';
import { SecurityTab } from './SecurityTab';
import { NetworkTab } from './NetworkTab';
import { MqttTab } from './MqttTab';
import { LogsTab } from './LogsTab';
import { AboutTab } from './AboutTab';

export const SETTINGS_LABELS: Record<string, string> = {
  'general.name': 'Name',
  'webConfig.port': 'Web port',
  'webConfig.sslPort': 'Secure web port',
  'mqtt.host': 'Broker address',
  'mqtt.port': 'Broker port',
  'logger.level': 'Log detail',
};

/** Stored as soon as they change; network and MQTT settings wait for Save (a half-typed port or address would apply). */
const AUTO_SAVED = ['general', 'logger'];

const TABS: Array<{ id: string; label: string; icon: ReactNode; config: boolean }> = [
  { id: 'general', label: 'General', icon: <SlidersHorizontal size={16} />, config: true },
  { id: 'security', label: 'Security', icon: <Shield size={16} />, config: false },
  { id: 'network', label: 'Network', icon: <Network size={16} />, config: true },
  { id: 'mqtt', label: 'MQTT', icon: <Radio size={16} />, config: true },
  { id: 'logs', label: 'Logs', icon: <ScrollText size={16} />, config: true },
  { id: 'about', label: 'About', icon: <Info size={16} />, config: false },
];

export function SettingsPage({ tab, onTab }: { tab: string | null; onTab: (t: string) => void }) {
  const ps = usePageSettings('settings', SETTINGS_LABELS, AUTO_SAVED);
  const active = TABS.find((t) => t.id === tab) ?? TABS[0];
  return (
    <PageFrame
      title="Settings"
      subtitle="Security, network, integrations and information about Overglow."
      settings={active.config || ps.dirty ? ps : undefined}
      toolbar={
        <div role="tablist" aria-label="Settings sections" className="scroll-thin -mx-1 flex gap-1 overflow-x-auto px-1 pb-1">
          {TABS.map((t) => {
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
                  const i = TABS.indexOf(active);
                  const next = e.key === 'ArrowRight' ? TABS[(i + 1) % TABS.length] : e.key === 'ArrowLeft' ? TABS[(i - 1 + TABS.length) % TABS.length] : null;
                  if (next) {
                    e.preventDefault();
                    onTab(next.id);
                    requestAnimationFrame(() => document.getElementById(`tab-${next.id}`)?.focus());
                  }
                }}
                className={cx(
                  'flex h-9 shrink-0 items-center gap-2 rounded-lg px-3 text-[13px] font-medium transition-colors',
                  selected ? 'bg-surface-3 text-ink' : 'text-ink-muted hover:bg-surface-2 hover:text-ink',
                )}
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
        {active.id === 'security' && <SecurityTab />}
        {active.id === 'network' && <NetworkTab ps={ps} />}
        {active.id === 'mqtt' && <MqttTab ps={ps} />}
        {active.id === 'logs' && <LogsTab ps={ps} />}
        {active.id === 'about' && <AboutTab />}
      </div>
    </PageFrame>
  );
}
