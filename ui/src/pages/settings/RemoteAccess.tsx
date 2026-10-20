import { useEffect, useState } from 'react';
import { KeyRound, Loader2, ShieldCheck, Smartphone } from 'lucide-react';
import { Button, Dialog } from '../../components/ui';
import { Callout, Section } from '../../components/form';
import { NewPasswordFields } from '../../components/PasswordFields';
import { QrCode } from '../../components/QrCode';
import { newPasswordProblem } from '../../settings/password';
import type { PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import type { SetupState } from '../../api/types';

type Obj = Record<string, unknown>;

/**
 * What turning remote access on does, said before it happens (Windows asks for network access right after). Other
 * devices always sign in, so without a password yet the dialog asks for one first: `onConfirm` receives it then.
 */
export function RemoteAccessDialog({
  open,
  busy,
  passwordSet,
  onCancel,
  onConfirm,
}: {
  open: boolean;
  busy: boolean;
  passwordSet: boolean;
  onCancel(): void;
  onConfirm(password?: string): void;
}) {
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  useEffect(() => {
    if (!open) {
      setPassword('');
      setConfirm('');
    }
  }, [open]);
  const problem = passwordSet ? null : newPasswordProblem(password, confirm);
  return (
    <Dialog
      open={open}
      title="Let your phone and other devices connect?"
      onClose={onCancel}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            Not now
          </Button>
          <Button
            variant="primary"
            disabled={busy || problem !== null}
            title={problem ?? undefined}
            icon={busy ? <Loader2 size={15} className="animate-spin" /> : undefined}
            onClick={() => onConfirm(passwordSet ? undefined : password)}
          >
            {passwordSet ? 'Turn on remote access' : 'Set password and turn on'}
          </Button>
        </>
      }
    >
      <div className="space-y-4 text-[13px] text-ink-muted">
        <p>
          <span className="font-medium text-ink">What it does:</span> Overglow starts accepting connections from your local network, so you can open this
          page on your phone, a tablet or another computer, and it announces itself on the network so apps can find it. Until now it only accepted
          connections from this computer.
        </p>
        <div className="rounded-xl border border-accent/30 bg-accent/10 p-3 text-ink">
          <p className="font-medium">Windows will ask next</p>
          <p className="mt-1 text-ink-muted">
            Right after you turn this on, Windows Security asks whether Overglow may communicate on networks. Allow <span className="font-medium text-ink">Private
            networks</span> only (your home network), not Public networks such as café, hotel or airport Wi-Fi.
          </p>
        </div>
        {passwordSet ? (
          <p>
            <span className="font-medium text-ink">Who can change settings:</span> other devices sign in with your Overglow password (you can change it
            in Settings → Security). This computer never needs it.
          </p>
        ) : (
          <div className="space-y-3 rounded-xl border border-line bg-surface-2 p-3">
            <p className="flex items-start gap-2 text-ink">
              <KeyRound size={15} className="mt-0.5 shrink-0 text-accent-strong" />
              <span>
                <span className="font-medium">Choose a password for other devices.</span>{' '}
                <span className="text-ink-muted">They sign in with it; this computer never needs it. You can change it later in Settings → Security.</span>
              </span>
            </p>
            <NewPasswordFields password={password} confirm={confirm} onPassword={setPassword} onConfirm={setConfirm} label="Password" />
          </div>
        )}
        <p>
          <span className="font-medium text-ink">Turning it off:</span> come back here and choose Turn off. Overglow then listens on this computer only
          again. To remove the Windows permission as well, open Windows Security → Firewall &amp; network protection → Allow an app through firewall.
        </p>
      </div>
    </Dialog>
  );
}

/** The addresses other devices open, with a QR code for the first. */
export function RemoteAccessAddresses({ status }: { status: SetupState['remoteAccess'] }) {
  const first = status.urls[0];
  if (!first)
    return <Callout tone="warn">Remote access is on, but this computer has no network address right now. Check that it is connected to your network.</Callout>;
  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-center">
      <QrCode text={first} label={`QR code for ${first}`} />
      <div className="min-w-0 space-y-2 text-[13px]">
        <p className="text-ink-muted">Scan the code with your phone’s camera, or open:</p>
        <ul className="space-y-1">
          {status.urls.map((u) => (
            <li key={u}>
              <a className="break-all font-mono text-accent-strong hover:underline" href={u} target="_blank" rel="noreferrer">
                {u}
              </a>
            </li>
          ))}
        </ul>
        <p className="text-xs text-ink-faint">Your phone must be on the same network as this computer.</p>
      </div>
    </div>
  );
}

/** Settings → Network: "Control from your phone / other devices" (the `network.remoteAccess` setting, off by default). */
export function RemoteAccessSection({ ps }: { ps: PageSettings }) {
  const { setup, api } = useConnection();
  const { run, busy } = useAction();
  const [asking, setAsking] = useState(false);
  const status = setup?.remoteAccess;

  const apply = (on: boolean, password?: string) =>
    run('remote-access', on ? 'Couldn’t turn on remote access' : 'Couldn’t turn off remote access', async () => {
      if (password !== undefined) await api.setPassword(password);
      // stored at once (not with the page's Save), so the explanation and the change go together
      const network = { ...ps.value<Obj>('network'), remoteAccess: on };
      await api.setConfig({ network });
      if (ps.dirtySections.includes('network')) ps.setPath('network', ['remoteAccess'], on);
      setAsking(false);
    }, on ? 'Remote access is on' : 'Remote access is off');

  return (
    <Section
      title="Control from your phone / other devices"
      icon={<Smartphone size={18} />}
      description={
        status?.enabled
          ? 'Other devices on your network can open Overglow.'
          : 'Off: Overglow only accepts connections from this computer, so Windows never asks for network access.'
      }
      actions={
        status && !status.fixedByCommandLine ? (
          status.enabled ? (
            <Button variant="danger" disabled={!ps.canEdit || busy === 'remote-access'} onClick={() => void apply(false)}>
              Turn off
            </Button>
          ) : (
            <Button variant="primary" disabled={!ps.canEdit} onClick={() => setAsking(true)}>
              Turn on…
            </Button>
          )
        ) : undefined
      }
    >
      {!status ? (
        <p className="text-[13px] text-ink-muted">Loading…</p>
      ) : status.fixedByCommandLine ? (
        <Callout icon={<ShieldCheck size={15} />}>
          Overglow was started with <span className="font-mono">--bind</span>, which decides where it listens{status.enabled ? '' : ' (this computer only)'}; this
          setting has no effect.
        </Callout>
      ) : null}
      {status?.enabled && <RemoteAccessAddresses status={status} />}
      <RemoteAccessDialog
        open={asking}
        busy={busy === 'remote-access'}
        passwordSet={setup?.passwordSet ?? false}
        onCancel={() => setAsking(false)}
        onConfirm={(password) => void apply(true, password)}
      />
    </Section>
  );
}
