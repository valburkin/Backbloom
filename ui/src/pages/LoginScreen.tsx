import { useState, type FormEvent } from 'react';
import { Eye, Lock } from 'lucide-react';
import { Button, TextField } from '../components/ui';
import { useConnection } from '../app/connection';
import { Logo } from '../components/Logo';
import { MOCK_PASSWORD } from '../api/mock';

/** The backend's factory default (the legacy UI signs in with it automatically; here it's an explicit choice). */
const FACTORY_PASSWORD = 'overglow';

export function LoginScreen() {
  const { login, continueReadOnly, tokenRequired, defaultPassword, backend, sessionError } = useConnection();
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const attempt = async (pw: string) => {
    setBusy(true);
    setError(null);
    try {
      await login(pw);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      setError(msg === 'No Authorization' ? 'That password didn’t work. Try again.' : msg);
    } finally {
      setBusy(false);
    }
  };

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (password.length < 8) {
      setError('The password has at least 8 characters.');
      return;
    }
    void attempt(password);
  };

  return (
    <div className="relative flex h-full items-center justify-center overflow-hidden p-6">
      <div
        aria-hidden
        className="pointer-events-none absolute -top-40 left-1/2 h-[520px] w-[820px] -translate-x-1/2 rounded-full opacity-30 blur-3xl"
        style={{ background: 'radial-gradient(closest-side, #8b7cf6, transparent), radial-gradient(closest-side, #22d3ee55, transparent)' }}
      />
      <form onSubmit={onSubmit} className="relative w-full max-w-sm rounded-2xl border border-line bg-surface/90 p-7 shadow-2xl backdrop-blur" aria-labelledby="login-title">
        <div className="mb-6 flex items-center gap-3">
          <Logo size={36} />
          <div>
            <h1 id="login-title" className="text-lg font-semibold">
              Sign in
            </h1>
            <p className="text-[13px] text-ink-muted">Enter the password of your lighting service to change settings.</p>
          </div>
        </div>
        <TextField label="Password" type="password" value={password} onChange={setPassword} autoFocus />
        {error && (
          <p role="alert" className="mt-2 text-[13px] text-bad">
            {error}
          </p>
        )}
        {sessionError && !error && <p className="mt-2 text-[13px] text-warn">{sessionError}</p>}
        <Button type="submit" variant="primary" className="mt-4 w-full" disabled={busy} icon={<Lock size={16} />}>
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>

        {defaultPassword && (
          <div className="mt-4 rounded-xl border border-warn/30 bg-warn/10 p-3 text-[13px] text-warn">
            This system still uses the factory password.
            <button type="button" className="ml-1 font-medium underline underline-offset-2" disabled={busy} onClick={() => void attempt(FACTORY_PASSWORD)}>
              Continue with the factory password
            </button>
            <span className="text-warn/80"> — consider changing it in Settings later.</span>
          </div>
        )}

        {backend.mode === 'mock' && (
          <p className="mt-4 text-center text-xs text-ink-faint">
            Demo mode — the password is <code className="font-mono text-ink-muted">{MOCK_PASSWORD}</code>
          </p>
        )}

        {!tokenRequired && (
          <button
            type="button"
            onClick={continueReadOnly}
            className="mx-auto mt-5 flex items-center gap-1.5 text-[13px] text-ink-muted hover:text-ink"
          >
            <Eye size={14} />
            Look around without signing in
          </button>
        )}
      </form>
    </div>
  );
}
