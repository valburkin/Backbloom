import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Copy, KeyRound, Lock, Plus, ShieldAlert, ShieldCheck, Trash2 } from 'lucide-react';
import { Button, Dialog, IconButton } from '../../components/ui';
import { Callout, Input, Section } from '../../components/form';
import { NewPasswordFields } from '../../components/PasswordFields';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import { useToast } from '../../app/toast';
import { hrefFor } from '../../app/router';
import { newPasswordProblem } from '../../settings/password';
import type { SetupState, TokenInfo } from '../../api/types';

/**
 * Sets or changes the password other devices sign in with. This computer never needs it, so it may set a new one
 * without the current one (also how a forgotten password is replaced); another device that signed in confirms it.
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

/** Whether other devices can reach Overglow, and whether they have to sign in. */
export function RemoteAccessProtection({ setup }: { setup: SetupState }) {
  const network = <a className="font-medium text-accent-strong underline-offset-2 hover:underline" href={hrefFor('settings', 'network')}>Settings → Network</a>;
  if (!setup.remoteAccess.enabled)
    return (
      <Callout tone="ok" icon={<ShieldCheck size={16} />}>
        Remote access is off: only this computer can open Overglow, and it never needs a password. Turn it on in {network}.
      </Callout>
    );
  if (setup.passwordSet)
    return (
      <Callout tone="ok" icon={<ShieldCheck size={16} />}>
        Remote access is on and protected: other devices must sign in with your password. This computer never needs it.
      </Callout>
    );
  return (
    <Callout tone="bad" icon={<ShieldAlert size={16} />}>
      Overglow listens on your network, but no password is set, so other devices can’t sign in. Set a password above.
    </Callout>
  );
}

function Tokens() {
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
      {tokens === null ? (
        <p className="text-[13px] text-ink-faint">Loading…</p>
      ) : tokens.length === 0 ? (
        <p className="text-[13px] text-ink-faint">No apps have a token.</p>
      ) : (
        <ul className="divide-y divide-line rounded-xl border border-line">
          {tokens.map((t) => (
            <li key={t.id} className="flex items-center gap-3 px-3 py-2.5 text-[13px]">
              <KeyRound size={15} className="text-ink-faint" />
              <span className="min-w-0 flex-1 truncate">{t.comment}</span>
              <span className="hidden text-xs text-ink-faint sm:inline">{t.last_use ? `Last used ${t.last_use.replace('T', ' ').slice(0, 16)}` : 'Never used'}</span>
              <IconButton
                label={`Revoke the token of ${t.comment}`}
                disabled={busy === `del-${t.id}`}
                onClick={() =>
                  window.confirm(`Revoke the token of “${t.comment}”? That app loses access.`) &&
                  void run(`del-${t.id}`, 'Couldn’t revoke the token', () => api.deleteToken(t.id)).then(load)
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
          void run('create', 'Couldn’t create a token', async () => {
            const t = await api.createToken(comment.trim());
            setCreated(t.token);
            setComment('');
            await load();
          });
        }}
      >
        <Input className="min-w-[14rem] flex-1" label="App name" placeholder="e.g. Stream Deck" value={comment} onChange={setComment} />
        <Button type="submit" icon={<Plus size={15} />} disabled={busy === 'create'}>
          Create token
        </Button>
      </form>
      <Dialog
        open={!!created}
        title="Token created"
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
          <IconButton label="Copy token" onClick={() => created && void navigator.clipboard?.writeText(created).then(() => toast('ok', 'Copied'))}>
            <Copy size={15} />
          </IconButton>
        </div>
      </Dialog>
    </div>
  );
}

export function SecurityTab() {
  const { setup, remote } = useConnection();
  const passwordSet = setup?.passwordSet ?? false;
  return (
    <>
      <Section
        title="Password"
        icon={<Lock size={18} />}
        description={
          passwordSet
            ? 'Other devices (your phone, a tablet, another computer) sign in with it. This computer never needs it.'
            : 'No password is set. This computer never needs one; other devices do, so remote access asks you to set it first.'
        }
      >
        <PasswordForm passwordSet={passwordSet} remote={remote} />
      </Section>

      <Section title="Remote access" icon={<ShieldCheck size={18} />}>
        {setup ? <RemoteAccessProtection setup={setup} /> : <p className="text-[13px] text-ink-muted">Loading…</p>}
      </Section>

      <Section title="App tokens" icon={<KeyRound size={18} />} description="Give an app (a Stream Deck plugin, a home automation…) its own key that you can revoke at any time.">
        <Tokens />
      </Section>
    </>
  );
}
