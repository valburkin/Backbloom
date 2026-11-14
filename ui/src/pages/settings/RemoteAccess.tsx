import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Copy, KeyRound, Loader2, Lock, Plus, ShieldAlert, ShieldCheck, Smartphone, Trash2 } from 'lucide-react';
import { Button, Dialog, IconButton, StatusDot } from '../../components/ui';
import { Advanced, Callout, Input, Section } from '../../components/form';
import { NewPasswordFields } from '../../components/PasswordFields';
import { QrCode } from '../../components/QrCode';
import { newPasswordProblem } from '../../settings/password';
import type { PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import { useToast } from '../../app/toast';
import type { SetupState, TokenInfo } from '../../api/types';

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
          <span className="font-medium text-ink">What it does:</span> Backbloom starts accepting connections from your home network, so you can open it
          on your phone, a tablet or another PC, and it announces itself on the network so apps can find it. Until now only this PC could open it.
        </p>
        <p>
          <span className="font-medium text-ink">Not encrypted yet:</span> other devices connect over plain http, so someone else on the same network
          could read what they send, the password included. Turn this on only on a network you trust, such as your home Wi-Fi.
        </p>
        <div className="rounded-2xl border border-accent/30 bg-accent/10 p-3 text-ink">
          <p className="font-medium">Windows will ask next</p>
          <p className="mt-1 text-ink-muted">
            Right after you turn this on, Windows Security asks whether Backbloom may communicate on networks. Allow <span className="font-medium text-ink">Private
            networks</span> only (your home network), not Public networks such as café, hotel or airport Wi-Fi.
          </p>
        </div>
        {passwordSet ? (
          <p>
            <span className="font-medium text-ink">Who can change settings:</span> other devices sign in with your Backbloom password (you can change it
            in Settings → Remote access). This PC never needs it.
          </p>
        ) : (
          <div className="space-y-3 rounded-2xl border border-line bg-white/[0.03] p-3">
            <p className="flex items-start gap-2 text-ink">
              <KeyRound size={15} className="mt-0.5 shrink-0 text-accent-strong" />
              <span>
                <span className="font-medium">Choose a password for other devices.</span>{' '}
                <span className="text-ink-muted">They sign in with it; this PC never needs it. You can change it later in Settings → Remote access.</span>
              </span>
            </p>
            <NewPasswordFields password={password} confirm={confirm} onPassword={setPassword} onConfirm={setConfirm} label="Password" />
          </div>
        )}
        <p>
          <span className="font-medium text-ink">Turning it off:</span> come back here and choose Turn off. Then only this PC can open Backbloom again. To
          remove the Windows permission as well, open Windows Security → Firewall &amp; network protection → Allow an app through firewall.
        </p>
      </div>
    </Dialog>
  );
}

/** The addresses other devices open, with a QR code for the first. */
export function RemoteAccessAddresses({ status }: { status: SetupState['remoteAccess'] }) {
  const first = status.urls[0];
  if (!first) return <Callout tone="warn">Remote access is on, but this PC has no network address right now. Check that it’s connected to your network.</Callout>;
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
        <p className="text-xs text-ink-faint">Your phone must be on the same network as this PC.</p>
      </div>
    </div>
  );
}

/** Whether other devices can open Backbloom, and whether they can sign in. Off is a normal state, not a success. */
export function RemoteAccessProtection({ setup }: { setup: SetupState }) {
  if (!setup.remoteAccess.enabled)
    return (
      <p className="flex items-center gap-2.5 text-sm text-ink-muted">
        <StatusDot tone="neutral" />
        Off — only this PC can open Backbloom.
      </p>
    );
  if (setup.passwordSet)
    return (
      <p className="flex items-start gap-2.5 text-sm">
        <span className="mt-1.5">
          <StatusDot tone="ok" />
        </span>
        <span>
          On — devices on your network can open Backbloom.
          <span className="block text-[13px] text-ink-muted">They sign in with your password. This PC never needs it.</span>
        </span>
      </p>
    );
  return (
    <Callout tone="bad" icon={<ShieldAlert size={16} />}>
      On, but no password is set, so other devices can’t sign in. Set one under Password below.
    </Callout>
  );
}

/**
 * Sets or changes the password other devices sign in with. This PC never needs it, so it may set a new one without
 * the current one (also how a forgotten password is replaced); another device that signed in confirms it.
 */
export function PasswordForm({ passwordSet, remote }: { passwordSet: boolean; remote: boolean }) {
  const { api, applySetup, setup } = useConnection();
  const { run, busy } = useAction();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const needsCurrent = remote && passwordSet;
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const problem = newPasswordProblem(next, confirm);
    if (problem) return setError(problem);
    const ok = await run(
      'password',
      passwordSet ? 'Couldn’t change the password' : 'Couldn’t set the password',
      () => api.setPassword(next, needsCurrent ? current : undefined),
      passwordSet ? 'Password changed' : 'Password set',
    );
    if (ok) {
      setCurrent('');
      setNext('');
      setConfirm('');
      if (setup && !setup.passwordSet) applySetup({ ...setup, passwordSet: true });
    } else if (needsCurrent) setError('Check the current password and try again.');
  };
  return (
    <form onSubmit={(e) => void submit(e)} className="space-y-4">
      {needsCurrent && (
        <Input className="sm:max-w-[calc(50%-0.5rem)]" label="Current password" type="password" autoComplete="current-password" value={current} onChange={setCurrent} />
      )}
      <NewPasswordFields password={next} confirm={confirm} onPassword={setNext} onConfirm={setConfirm} label={passwordSet ? 'New password' : 'Password'} />
      {error && (
        <p role="alert" className="text-[13px] text-bad">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" icon={<Lock size={15} />} disabled={busy === 'password' || !next || !confirm || (needsCurrent && !current)}>
        {passwordSet ? 'Change password' : 'Set password'}
      </Button>
    </form>
  );
}

/** App keys: a key per app (a Stream Deck plugin, a home automation…) that can be revoked on its own. */
function AppKeys() {
  const { api, session } = useConnection();
  const toast = useToast();
  const { run, busy } = useAction();
  const [tokens, setTokens] = useState<TokenInfo[] | null>(null);
  const [comment, setComment] = useState('');
  const [created, setCreated] = useState<string | null>(null);
  const load = useCallback(() => api.listTokens().then(setTokens, () => setTokens([])), [api]);
  useEffect(() => {
    void load();
  }, [load, session]);
  return (
    <div className="space-y-3">
      <div>
        <p className="text-sm font-medium">App keys</p>
        <p className="text-xs text-ink-faint">Give an app (a Stream Deck plugin, a home automation…) its own key that you can revoke at any time.</p>
      </div>
      {tokens === null ? (
        <p className="text-[13px] text-ink-faint">Loading…</p>
      ) : tokens.length === 0 ? (
        <p className="text-[13px] text-ink-faint">No apps have a key.</p>
      ) : (
        <ul className="divide-y divide-line border-y border-line">
          {tokens.map((t) => (
            <li key={t.id} className="flex items-center gap-3 py-2 text-[13px]">
              <KeyRound size={15} className="text-ink-faint" />
              <span className="min-w-0 flex-1 truncate">{t.comment}</span>
              <span className="hidden text-xs text-ink-faint sm:inline">{t.last_use ? `Last used ${t.last_use.replace('T', ' ').slice(0, 16)}` : 'Never used'}</span>
              <IconButton
                label={`Revoke the key of ${t.comment}`}
                disabled={busy === `del-${t.id}`}
                onClick={() =>
                  window.confirm(`Revoke the key of “${t.comment}”? That app loses access.`) &&
                  void run(`del-${t.id}`, 'Couldn’t revoke the key', () => api.deleteToken(t.id)).then(load)
                }
              >
                <Trash2 size={15} />
              </IconButton>
            </li>
          ))}
        </ul>
      )}
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (comment.trim().length < 5) return toast('bad', 'Name the app', 'Use at least 5 characters so you recognize it later.');
          void run('create', 'Couldn’t create a key', async () => {
            const t = await api.createToken(comment.trim());
            setCreated(t.token);
            setComment('');
            await load();
          });
        }}
      >
        <Input className="min-w-[14rem] flex-1" label="App name" placeholder="For example Stream Deck" value={comment} onChange={setComment} />
        <Button type="submit" icon={<Plus size={15} />} disabled={busy === 'create'}>
          Create key
        </Button>
      </form>
      <Dialog
        open={!!created}
        title="App key created"
        onClose={() => setCreated(null)}
        footer={
          <Button variant="primary" onClick={() => setCreated(null)}>
            Done
          </Button>
        }
      >
        <p className="mb-3 text-[13px] text-ink-muted">Copy it into the app now — it isn’t shown again.</p>
        <div className="flex items-center gap-2">
          <code className="min-w-0 flex-1 break-all rounded-lg bg-bg p-2.5 font-mono text-xs">{created}</code>
          <IconButton label="Copy key" onClick={() => created && void navigator.clipboard?.writeText(created).then(() => toast('ok', 'Copied'))}>
            <Copy size={15} />
          </IconButton>
        </div>
      </Dialog>
    </div>
  );
}

/**
 * Settings → Remote access: on/off (the `network.remoteAccess` setting, off by default), password, app keys. The port
 * is the app's `--port` option (shown in the addresses), not a setting.
 */
export function RemoteAccessTab({ ps }: { ps: PageSettings }) {
  const { setup, api, remote } = useConnection();
  const { run, busy } = useAction();
  const [asking, setAsking] = useState(false);
  const status = setup?.remoteAccess;
  const passwordSet = setup?.passwordSet ?? false;

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
    <>
      <Section
        title="Remote access"
        icon={<Smartphone size={18} />}
        description="Use Backbloom from your phone, tablet or another PC on your home network."
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
        {!setup || !status ? (
          <p className="text-[13px] text-ink-muted">Loading…</p>
        ) : (
          <>
            <RemoteAccessProtection setup={setup} />
            {status.fixedByCommandLine && (
              <Callout icon={<ShieldCheck size={15} />}>
                Backbloom was started with <span className="font-mono">--bind</span>, which decides where it listens{status.enabled ? '' : ' (this PC only)'}; this
                setting has no effect.
              </Callout>
            )}
            {status.enabled && <RemoteAccessAddresses status={status} />}
          </>
        )}
        <Advanced>
          <AppKeys />
        </Advanced>
        <RemoteAccessDialog
          open={asking}
          busy={busy === 'remote-access'}
          passwordSet={passwordSet}
          onCancel={() => setAsking(false)}
          onConfirm={(password) => void apply(true, password)}
        />
      </Section>

      <Section
        title="Password"
        icon={<Lock size={18} />}
        description={
          passwordSet
            ? 'Other devices (your phone, a tablet, another PC) sign in with it. This PC never needs it.'
            : 'No password is set. This PC never needs one; other devices do, so turning on remote access asks you to set it first.'
        }
      >
        <PasswordForm passwordSet={passwordSet} remote={remote} />
      </Section>
    </>
  );
}
