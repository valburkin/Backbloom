import type { ReactNode } from 'react';
import { LogIn, LogOut } from 'lucide-react';
import { Button, Card, StatusDot } from '../components/ui';
import { PageHeader } from '../components/PageHeader';
import { useConnection } from '../app/connection';

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-line py-3 last:border-0">
      <span className="text-[13px] text-ink-muted">{label}</span>
      <span className="text-right text-sm">{children}</span>
    </div>
  );
}

export function SettingsPage() {
  const { status, backend, auth, sysInfo, serverInfo, logout, requestLogin } = useConnection();
  const instances = serverInfo?.instance ?? [];
  return (
    <div className="scroll-thin h-full overflow-y-auto">
      <PageHeader title="Settings" subtitle="More settings are coming soon." />
      <div className="mx-auto max-w-3xl space-y-6 p-6">
        <Card className="p-5">
          <h2 className="mb-2 text-sm font-semibold">Connection</h2>
          <Row label="Status">
            <span className="inline-flex items-center gap-2">
              <StatusDot tone={status === 'open' ? 'ok' : 'warn'} />
              {status === 'open' ? 'Connected' : status}
            </span>
          </Row>
          <Row label="Address">
            <code className="font-mono text-[13px] text-ink-muted">{backend.url}</code>
          </Row>
          <Row label="Access">{auth === 'admin' ? 'Signed in' : auth === 'readOnly' ? 'View only' : '—'}</Row>
          <Row label="Service version">{sysInfo?.hyperhdr?.version ?? '—'}</Row>
          <Row label="Computer">{sysInfo?.system?.prettyName ?? sysInfo?.system?.hostName ?? '—'}</Row>
          <Row label="Light groups">{instances.map((i) => `${i.friendly_name}${i.running ? '' : ' (stopped)'}`).join(', ') || '—'}</Row>
          <div className="mt-4 flex justify-end">
            {auth === 'admin' ? (
              <Button size="sm" icon={<LogOut size={15} />} onClick={() => void logout()}>
                Sign out
              </Button>
            ) : (
              <Button size="sm" variant="primary" icon={<LogIn size={15} />} onClick={requestLogin}>
                Sign in
              </Button>
            )}
          </div>
        </Card>
      </div>
    </div>
  );
}
