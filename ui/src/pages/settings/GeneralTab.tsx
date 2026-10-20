import { useRef, useState, type ReactNode } from 'react';
import { Download, HardDrive, LogIn, LogOut, SlidersHorizontal, Upload, Wifi } from 'lucide-react';
import { Button, Dialog, StatusDot } from '../../components/ui';
import { Callout, Input, Section, Toggle } from '../../components/form';
import { useConfig, type PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import { useToast } from '../../app/toast';

type Obj = Record<string, unknown>;

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-line py-2.5 last:border-0">
      <span className="text-[13px] text-ink-muted">{label}</span>
      <span className="min-w-0 truncate text-right text-sm">{children}</span>
    </div>
  );
}

function download(name: string, text: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function GeneralTab({ ps }: { ps: PageSettings }) {
  const { status, backend, auth, sysInfo, logout, requestLogin, api } = useConnection();
  const { reload } = useConfig();
  const toast = useToast();
  const { run, busy } = useAction();
  const general = ps.value<Obj>('general');
  const err = (p: string) => ps.issues.find((i) => i.path === p)?.message;
  const fileInput = useRef<HTMLInputElement>(null);
  const [restore, setRestore] = useState<{ name: string; data: unknown } | null>(null);

  const exportSettings = () =>
    void run('export', 'Couldn’t export the settings', async () => {
      const data = await api.exportSettings();
      download(`overglow-settings-${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(data, null, 2));
    });

  // The backend applies a backup while running and lists what it adapted; sections the backup lacks (e.g. the
  // layout) aren't announced by events, so the settings are read again afterwards.
  const restoreSettings = (data: unknown) =>
    void run('restore', 'Couldn’t restore the settings', async () => {
      const changes = await api.importSettings(data);
      setRestore(null);
      reload();
      toast(
        'ok',
        'Settings restored',
        changes.length ? `Adapted to this version: ${changes.slice(0, 3).join('; ')}${changes.length > 3 ? ` (and ${changes.length - 3} more)` : ''}` : undefined,
      );
    });

  const pickFile = async (file: File | undefined) => {
    if (!file) return;
    try {
      const data = JSON.parse(await file.text()) as Obj;
      if (!data || typeof data.version !== 'string' || !Array.isArray(data.settings)) throw new Error('This isn’t a settings backup from Overglow.');
      setRestore({ name: file.name, data });
    } catch (e) {
      toast('bad', 'Can’t use that file', e instanceof SyntaxError ? 'The file isn’t valid JSON.' : e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <>
      <Section title="General" icon={<SlidersHorizontal size={18} />}>
        <Input
          label="Name"
          hint="Shown in other apps and on your network (4–20 characters)."
          value={String(general.name ?? '')}
          error={err('general.name')}
          onChange={(v) => ps.setPath('general', ['name'], v)}
        />
        <Toggle
          label="Pause while the PC is locked or the screen is off"
          hint="Screen capture stops while Windows is locked or the display is off."
          checked={general.disableOnLocked === true}
          onChange={(v) => ps.setPath('general', ['disableOnLocked'], v)}
        />
        <Toggle
          label="Start with the lights off"
          hint="Overglow starts paused; turn the lights on from Home."
          checked={general.disableLedsStartup === true}
          onChange={(v) => ps.setPath('general', ['disableLedsStartup'], v)}
        />
      </Section>

      <Section title="Backup" icon={<HardDrive size={18} />} description="Save all settings to a file, or restore them from one.">
        <div className="flex flex-wrap gap-2">
          <Button icon={<Download size={15} />} disabled={busy === 'export'} onClick={exportSettings}>
            Export settings
          </Button>
          <Button icon={<Upload size={15} />} onClick={() => fileInput.current?.click()}>
            Restore from file…
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept=".json,application/json"
            className="hidden"
            onChange={(e) => {
              void pickFile(e.target.files?.[0]);
              e.target.value = '';
            }}
          />
        </div>
      </Section>

      <Section title="This connection" icon={<Wifi size={18} />}>
        <div>
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
          <Row label="Computer">{sysInfo?.system?.hostName ?? '—'}</Row>
        </div>
        <div className="flex justify-end">
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
      </Section>

      <Dialog
        open={!!restore}
        title="Restore settings?"
        onClose={() => setRestore(null)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setRestore(null)}>
              Cancel
            </Button>
            <Button variant="primary" disabled={busy === 'restore'} onClick={() => restore && restoreSettings(restore.data)}>
              Restore
            </Button>
          </>
        }
      >
        <div className="space-y-3 text-[13px]">
          <p>
            All current settings will be replaced with the ones in <span className="font-medium">{restore?.name}</span>.
          </p>
          <Callout tone="info">The lights switch to the restored settings right away. Your current settings are kept in the backups folder of Overglow’s data folder.</Callout>
        </div>
      </Dialog>
    </>
  );
}
