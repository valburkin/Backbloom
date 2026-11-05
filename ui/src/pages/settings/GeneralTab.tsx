import { useRef, useState, type ReactNode } from 'react';
import { Clapperboard, Download, HardDrive, LogOut, SlidersHorizontal, Upload, Wifi } from 'lucide-react';
import { Button, Dialog, StatusDot } from '../../components/ui';
import { Advanced, Callout, Expert, Input, Section, Toggle } from '../../components/form';
import { useConfig, type PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import { setExpertTools, useExpertTools } from '../../app/expert';
import { useToast } from '../../app/toast';
import { differsFromDefault, propertyAt } from '../../schema/schema';
import { splitEffects } from '../../control/modes';
import { StartupAndIdle } from './StartupIdle';

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
  const { status, backend, remote, sysInfo, logout, api, setup, applySetup, serverInfo } = useConnection();
  const { reload } = useConfig();
  const toast = useToast();
  const { run, busy } = useAction();
  const general = ps.value<Obj>('general');
  const err = (p: string) => ps.issues.find((i) => i.path === p)?.message;
  const fileInput = useRef<HTMLInputElement>(null);
  const expertTools = useExpertTools();
  const [restore, setRestore] = useState<{ name: string; data: unknown } | null>(null);

  const generalSchema = ps.schemaOf('general');
  const moreChanged = ['name', 'disableLedsStartup'].filter((k) => {
    const s = propertyAt(generalSchema, k);
    return !!s && differsFromDefault(s, general[k]);
  });

  const exportSettings = () =>
    void run('export', 'Couldn’t save a backup', async () => {
      const data = await api.exportSettings();
      download(`backbloom-settings-${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(data, null, 2));
    });

  // The backend applies a backup while running and lists what it adapted; sections the backup lacks (e.g. the
  // layout) aren't announced by events, so the settings are read again afterwards.
  const restoreSettings = (data: unknown) =>
    void run('restore', 'Couldn’t restore the backup', async () => {
      const changes = await api.importSettings(data);
      setRestore(null);
      reload();
      toast(
        'ok',
        'Backup restored',
        changes.length ? `Adapted to this version: ${changes.slice(0, 3).join('; ')}${changes.length > 3 ? ` (and ${changes.length - 3} more)` : ''}` : undefined,
      );
    });

  const pickFile = async (file: File | undefined) => {
    if (!file) return;
    try {
      const data = JSON.parse(await file.text()) as Obj;
      if (!data || typeof data.version !== 'string' || !Array.isArray(data.settings)) throw new Error('It isn’t a Backbloom backup.');
      setRestore({ name: file.name, data });
    } catch (e) {
      toast('bad', 'Can’t use that file', e instanceof SyntaxError ? 'It isn’t a Backbloom backup.' : e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <>
      <Section title="General" icon={<SlidersHorizontal size={18} />}>
        {setup?.autostart.available && (
          <Toggle
            label="Start with Windows"
            hint="Backbloom starts in the tray when you sign in to Windows."
            checked={setup.autostart.enabled}
            disabled={!ps.canEdit || busy === 'autostart'}
            onChange={(on) => void run('autostart', 'Couldn’t change starting with Windows', async () => applySetup(await api.setAutostart(on)))}
          />
        )}
        <Toggle
          label="Pause while the PC is locked or the screen is off"
          hint="Screen sync stops while Windows is locked or the screen is off."
          checked={general.disableOnLocked === true}
          onChange={(v) => ps.setPath('general', ['disableOnLocked'], v)}
        />
        <Toggle
          label="Show expert and diagnostic tools"
          hint="Adds performance numbers, logs, test tools and rarely needed technical settings. Only on this device."
          checked={expertTools}
          onChange={setExpertTools}
        />
        <Advanced
          changed={moreChanged.length}
          invalid={!!err('general.name')}
          onReset={ps.canEdit ? () => ps.set('general', { ...general, ...Object.fromEntries(moreChanged.map((k) => [k, propertyAt(generalSchema, k)?.default])) }) : undefined}
        >
          <Input
            label="Name"
            hint="Shown in other apps and on your network (4–20 characters)."
            value={String(general.name ?? '')}
            error={err('general.name')}
            onChange={(v) => ps.setPath('general', ['name'], v)}
          />
          <Toggle
            label="Start with the lights off"
            hint="Turn them on from Home or the tray icon."
            checked={general.disableLedsStartup === true}
            onChange={(v) => ps.setPath('general', ['disableLedsStartup'], v)}
          />
        </Advanced>
      </Section>

      {ps.canEdit && (
        <Section title="Startup and idle" icon={<Clapperboard size={18} />} description="What plays when Backbloom starts, and when nothing else shows.">
          <StartupAndIdle ps={ps} effects={[...splitEffects(serverInfo?.effects).regular, ...splitEffects(serverInfo?.effects).music]} />
        </Section>
      )}

      <Section title="Backup" icon={<HardDrive size={18} />} description="Save all settings to a file, or bring them back from one.">
        <div className="flex flex-wrap gap-2">
          <Button icon={<Download size={15} />} disabled={busy === 'export'} onClick={exportSettings}>
            Save a backup
          </Button>
          <Button icon={<Upload size={15} />} onClick={() => fileInput.current?.click()}>
            Restore a backup…
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

      {(expertTools || remote) && (
        <Section title="This connection" icon={<Wifi size={18} />}>
          <Expert>
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
              <Row label="Access">{remote ? 'Signed in from another device' : 'This PC: full access, no password needed'}</Row>
              <Row label="PC">{sysInfo?.system?.hostName ?? '—'}</Row>
            </div>
          </Expert>
          {remote && (
            <div className="flex items-center justify-between gap-3">
              <p className="text-[13px] text-ink-muted">You’re signed in from another device.</p>
              <Button size="sm" icon={<LogOut size={15} />} onClick={() => void logout()}>
                Sign out
              </Button>
            </div>
          )}
        </Section>
      )}

      <Dialog
        open={!!restore}
        title="Restore a backup?"
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
          <Callout tone="info">The lights switch to the restored settings right away. Backbloom keeps a copy of your current settings, just in case.</Callout>
        </div>
      </Dialog>
    </>
  );
}
