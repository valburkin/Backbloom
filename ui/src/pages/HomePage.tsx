import { ArrowRight, Lightbulb, MonitorSmartphone, Radio, Server } from 'lucide-react';
import type { ReactNode } from 'react';
import { Badge, Card, StatusDot } from '../components/ui';
import { PageHeader } from '../components/PageHeader';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { stripLedCount } from '../layout';

function Stat({ icon, label, value, tone }: { icon: ReactNode; label: string; value: ReactNode; tone?: 'ok' | 'warn' | 'bad' | 'neutral' }) {
  return (
    <Card className="p-5">
      <div className="flex items-center gap-2 text-[13px] text-ink-muted">
        {icon}
        {label}
      </div>
      <div className="mt-3 flex items-center gap-2 text-lg font-semibold">
        {tone && <StatusDot tone={tone} />}
        {value}
      </div>
    </Card>
  );
}

export function HomePage() {
  const { status, serverInfo, sysInfo, backend, auth } = useConnection();
  const { state } = useEditor();
  const comp = (name: string) => serverInfo?.components?.find((c) => c.name === name)?.enabled;
  const lightsOn = comp('ALL') !== false && comp('LEDDEVICE') !== false;
  const source = serverInfo?.priorities?.find((p) => p.visible);
  const ledTotal = state.spec.strips.reduce((n, s) => n + stripLedCount(s), 0);
  const host = sysInfo?.system?.hostName ?? serverInfo?.hostname;

  return (
    <div className="scroll-thin h-full overflow-y-auto">
      <PageHeader
        title="Home"
        subtitle={host ? `Running on ${host}` : undefined}
        actions={backend.mode === 'mock' ? <Badge tone="accent">Demo data</Badge> : auth === 'readOnly' ? <Badge>View only</Badge> : null}
      />
      <div className="mx-auto max-w-5xl space-y-6 p-6">
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <Stat
            icon={<Server size={15} />}
            label="Lighting service"
            value={status === 'open' ? 'Connected' : 'Reconnecting…'}
            tone={status === 'open' ? 'ok' : 'warn'}
          />
          <Stat icon={<Lightbulb size={15} />} label="Lights" value={lightsOn ? 'On' : 'Off'} tone={lightsOn ? 'ok' : 'neutral'} />
          <Stat
            icon={<Radio size={15} />}
            label="Showing"
            value={source ? (source.componentId === 'SYSTEMGRABBER' ? 'Your screen' : source.origin) : 'Nothing'}
          />
        </div>

        <a href="#/layout" className="group block">
          <Card className="flex items-center gap-5 overflow-hidden p-6 transition-colors group-hover:border-line-strong">
            <div className="flex h-14 w-14 shrink-0 items-center justify-center rounded-2xl bg-gradient-to-br from-cyan-400/20 via-violet-500/20 to-pink-500/20 text-accent-strong">
              <MonitorSmartphone size={26} />
            </div>
            <div className="min-w-0 flex-1">
              <p className="font-semibold">LED layout</p>
              <p className="text-[13px] text-ink-muted">
                {state.spec.strips.length
                  ? `${state.spec.strips.length} strip${state.spec.strips.length === 1 ? '' : 's'} · ${ledTotal} LEDs around your screen`
                  : 'Tell us where your LED strips are so the colors line up with the picture.'}
              </p>
            </div>
            <ArrowRight size={18} className="text-ink-faint transition-transform group-hover:translate-x-0.5 group-hover:text-ink" />
          </Card>
        </a>

        <p className="text-xs text-ink-faint">
          {sysInfo?.hyperhdr?.version ? `Service version ${sysInfo.hyperhdr.version} · ` : ''}
          {backend.url}
        </p>
      </div>
    </div>
  );
}
