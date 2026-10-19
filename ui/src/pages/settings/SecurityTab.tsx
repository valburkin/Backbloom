import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Copy, KeyRound, Lock, Plus, ShieldAlert, ShieldCheck, Trash2 } from 'lucide-react';
import { Button, Dialog, IconButton } from '../../components/ui';
import { Callout, Input, Section, Toggle } from '../../components/form';
import type { PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import { useToast } from '../../app/toast';
import type { TokenInfo } from '../../api/types';

type Obj = Record<string, unknown>;

function PasswordForm() {
  const { api, defaultPassword } = useConnection();
  const { run, busy } = useAction();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (next.length < 8) return setError('Use at least 8 characters for the new password.');
    if (next !== confirm) return setError('The new passwords don’t match.');
    const ok = await run('password', 'Couldn’t change the password', () => api.changePassword(current, next), 'Password changed');
    if (ok) {
      setCurrent('');
      setNext('');
      setConfirm('');
    } else setError('Check the current password and try again.');
  };
  return (
    <form onSubmit={(e) => void submit(e)} className="space-y-4">
      {defaultPassword && (
        <Callout tone="warn" icon={<ShieldAlert size={16} />}>
          You’re still using the factory password (“overglow”). Anyone who knows it can change your settings.
        </Callout>
      )}
      <div className="grid gap-4 sm:grid-cols-3">
        <Input label="Current password" type="password" autoComplete="current-password" value={current} onChange={setCurrent} />
        <Input label="New password" type="password" autoComplete="new-password" value={next} onChange={setNext} hint="At least 8 characters." />
        <Input label="Repeat new password" type="password" autoComplete="new-password" value={confirm} onChange={setConfirm} />
      </div>
      {error && <p role="alert" className="text-[13px] text-bad">{error}</p>}
      <Button type="submit" variant="primary" icon={<Lock size={15} />} disabled={busy === 'password' || !current || !next}>
        Change password
      </Button>
    </form>
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

function AddressList({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  const [text, setText] = useState<string | null>(null);
  return (
    <Input
      label="Allowed IP addresses"
      hint="Separate addresses with commas."
      value={text ?? value.join(', ')}
      onChange={(v) => {
        setText(v);
        onChange(
          v
            .split(',')
            .map((x) => x.trim())
            .filter(Boolean),
        );
      }}
    />
  );
}

export function SecurityTab({ ps }: { ps: PageSettings }) {
  const net = ps.value<Obj>('network');
  const set = (k: string) => (v: unknown) => ps.setPath('network', [k], v);
  const whitelist = Array.isArray(net.ipWhitelist) ? (net.ipWhitelist as string[]) : [];
  return (
    <>
      <Section title="Password" icon={<Lock size={18} />} description="Needed to change settings from this page.">
        <PasswordForm />
      </Section>

      <Section title="Who can change settings" icon={<ShieldCheck size={18} />}>
        <Toggle
          label="Ask for the password on my home network"
          hint="When off, anyone on your Wi-Fi or LAN can open this page and change everything without a password."
          checked={net.localAdminAuth === true}
          onChange={set('localAdminAuth')}
        />
        {net.localAdminAuth !== true && (
          <Callout tone="warn" icon={<ShieldAlert size={16} />}>
            No password is needed on your home network. Only turn this off on a network you fully trust.
          </Callout>
        )}
        <Toggle
          label="Apps need permission"
          hint="Apps and plugins that control the lights must use a token (see below)."
          checked={net.apiAuth === true}
          onChange={set('apiAuth')}
        />
        {net.apiAuth === true && (
          <>
            <Toggle
              label="…also on my home network"
              hint="Off: apps on your home network may control the lights without a token."
              checked={net.localApiAuth === true}
              onChange={set('localApiAuth')}
            />
            <Toggle
              label="Allow access from the internet"
              hint="Lets devices outside your home network connect (if your router forwards the port)."
              checked={net.internetAccessAPI === true}
              onChange={set('internetAccessAPI')}
            />
          </>
        )}
        {net.apiAuth === true && net.internetAccessAPI === true && (
          <>
            <Callout tone="bad" icon={<ShieldAlert size={16} />}>
              Overglow is reachable from the internet. Use a strong password, and restrict access to known addresses if you can.
            </Callout>
            <Toggle
              label="Only from these addresses"
              checked={net.restirctedInternetAccessAPI === true}
              onChange={set('restirctedInternetAccessAPI')}
            />
            {net.restirctedInternetAccessAPI === true && (
              <AddressList value={whitelist} onChange={set('ipWhitelist')} />
            )}
          </>
        )}
      </Section>

      <Section title="App tokens" icon={<KeyRound size={18} />} description="Give an app (a Stream Deck plugin, a home automation…) its own key that you can revoke at any time.">
        <Tokens />
      </Section>
    </>
  );
}
