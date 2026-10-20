import { describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MockBackend, MOCK_PASSWORD } from '../../api/mock';
import { RpcApi } from '../../api/rpcApi';
import type { SetupState } from '../../api/types';
import { ConnectionProvider, sessionAccess } from '../connection';
import { ToastProvider } from '../toast';
import { LoginScreen } from '../../pages/LoginScreen';
import { RemoteAccessProtection, SecurityTab } from '../../pages/settings/SecurityTab';
import { newPasswordProblem, passwordStrength } from '../../settings/password';

async function connected(options: ConstructorParameters<typeof MockBackend>[0] = {}) {
  const backend = new MockBackend({ latencyMs: 0, ...options });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new RpcApi(backend) };
}

function render(node: ReactNode, backend = new MockBackend()): string {
  return renderToStaticMarkup(
    <ToastProvider>
      <ConnectionProvider backend={backend}>{node}</ConnectionProvider>
    </ToastProvider>,
  );
}

describe('who signs in', () => {
  it('gives this computer full access without a login', async () => {
    const { api } = await connected();
    expect(await sessionAccess(api, null)).toEqual({ auth: 'admin', remote: false, dropToken: false });
    await expect(api.getConfig()).resolves.toBeTruthy();
  });

  it('shows the login to other devices, and reuses their token', async () => {
    const { api } = await connected({ requireAuth: true });
    expect(await sessionAccess(api, null)).toEqual({ auth: 'login', remote: true, dropToken: false });
    expect(await sessionAccess(api, 'stale-token')).toEqual({ auth: 'login', remote: true, dropToken: true });

    const token = await api.login(MOCK_PASSWORD);
    await api.logout();
    expect(await sessionAccess(api, token ?? null)).toEqual({ auth: 'admin', remote: true, dropToken: false });
  });

  it('never offers a factory password', () => {
    const html = render(<LoginScreen />, new MockBackend({ requireAuth: true }));
    expect(html).toContain('Sign in');
    expect(html).toContain('password you set on the computer that runs Backbloom');
    expect(html).not.toMatch(/factory|default password|without signing in/i);
  });
});

describe('Settings → Security', () => {
  const setup = (change: Partial<SetupState>): SetupState => ({
    firstRunCompleted: true,
    outputHeld: false,
    otherApp: null,
    importedFrom: null,
    autostart: { available: true, enabled: false },
    remoteAccess: { enabled: false, fixedByCommandLine: false, port: 18290, urls: [], advertised: false },
    passwordSet: false,
    acknowledged: [],
    ...change,
  });
  const on = { enabled: true, fixedByCommandLine: false, port: 18290, urls: ['http://192.168.1.20:18290/'], advertised: true };

  it('sets the first password without asking for a current one', () => {
    const html = render(<SecurityTab />);
    expect(html).toContain('No password is set.');
    expect(html).toContain('Set password');
    expect(html).not.toContain('Current password');
    expect(html.match(/type="password"/g)).toHaveLength(2);
    expect(html).not.toMatch(/factory|backbloom”|“backbloom/i);
  });

  it('says whether remote access is protected', () => {
    expect(renderToStaticMarkup(<RemoteAccessProtection setup={setup({})} />)).toContain('only this computer can open Backbloom');
    expect(renderToStaticMarkup(<RemoteAccessProtection setup={setup({ remoteAccess: on, passwordSet: true })} />)).toContain('on and protected');
    expect(renderToStaticMarkup(<RemoteAccessProtection setup={setup({ remoteAccess: on })} />)).toContain('no password is set');
  });
});

describe('password hints', () => {
  it('rates length, variety and common choices', () => {
    expect(passwordStrength('')).toBe('short');
    expect(passwordStrength('abc1234')).toBe('short');
    expect(passwordStrength('backbloom1')).toBe('weak');
    expect(passwordStrength('aaaaaaaaaaaa')).toBe('weak');
    expect(passwordStrength('sunflower')).toBe('weak');
    expect(passwordStrength('Sunflower7')).toBe('fair');
    expect(passwordStrength('sunflower garden')).toBe('strong');
    expect(passwordStrength('Sunflower7!x')).toBe('strong');
  });

  it('requires the minimum length and a matching confirmation', () => {
    expect(newPasswordProblem('short', 'short')).toBe('Use at least 8 characters.');
    expect(newPasswordProblem('long-enough', 'long-enougj')).toBe('The passwords don’t match.');
    expect(newPasswordProblem('long-enough', 'long-enough')).toBeNull();
  });
});
