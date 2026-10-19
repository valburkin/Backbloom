import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { HyperApi, SUBSCRIPTIONS } from '../api/hyperhdr';
import type { Backend, ComponentInfo, ConnectionStatus, InstanceInfo, PriorityInfo, ServerInfo, SysInfo } from '../api/types';

/**
 * - checking: talking to the backend to find out what's needed
 * - login:    admin password needed (login screen shown)
 * - admin:    full access
 * - readOnly: user chose to look around without signing in (no config access, no saving)
 */
export type AuthState = 'checking' | 'login' | 'admin' | 'readOnly';

export interface ConnectionContextValue {
  backend: Backend;
  api: HyperApi;
  status: ConnectionStatus;
  auth: AuthState;
  /** Bumps on every (re)established session; consumers reload per-session data when it changes. */
  session: number;
  /** Backend still uses its factory-default password. */
  defaultPassword: boolean;
  /** Every command needs auth (remote access); read-only browsing isn't possible. */
  tokenRequired: boolean;
  serverInfo: ServerInfo | null;
  sysInfo: SysInfo | null;
  sessionError: string | null;
  login(password: string): Promise<void>;
  continueReadOnly(): void;
  requestLogin(): void;
  logout(): Promise<void>;
}

const Ctx = createContext<ConnectionContextValue | null>(null);

const TOKEN_KEY = 'ambient.loginToken';

function readToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}
function writeToken(token: string | null): void {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token);
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch {
    /* storage unavailable */
  }
}

function message(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

export function ConnectionProvider({ backend, children }: { backend: Backend; children: ReactNode }) {
  const api = useMemo(() => new HyperApi(backend), [backend]);
  const [status, setStatus] = useState<ConnectionStatus>(backend.status);
  const [auth, setAuth] = useState<AuthState>('checking');
  const [session, setSession] = useState(0);
  const [defaultPassword, setDefaultPassword] = useState(false);
  const [tokenRequired, setTokenRequired] = useState(false);
  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null);
  const [sysInfo, setSysInfo] = useState<SysInfo | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const readOnlyChosen = useRef(false);
  const generation = useRef(0);

  const loadInfo = useCallback(
    async (gen: number) => {
      const [si, sys] = await Promise.all([api.serverInfo(SUBSCRIPTIONS), api.sysInfo().catch(() => null)]);
      if (gen !== generation.current) return;
      setServerInfo(si);
      setSysInfo(sys);
    },
    [api],
  );

  const startSession = useCallback(async () => {
    const gen = ++generation.current;
    setAuth('checking');
    setSessionError(null);
    try {
      const needsToken = await api.tokenRequired();
      let adminNeeded = await api.adminRequired();
      if (adminNeeded) {
        const token = readToken();
        if (token) {
          try {
            await api.loginWithToken(token);
            adminNeeded = false;
          } catch {
            writeToken(null);
          }
        }
      }
      if (gen !== generation.current) return;
      setTokenRequired(needsToken);
      if (adminNeeded) {
        setDefaultPassword(await api.newPasswordRequired().catch(() => false));
        if (gen !== generation.current) return;
        if (readOnlyChosen.current && !needsToken) {
          await loadInfo(gen);
          setAuth('readOnly');
        } else {
          setAuth('login');
        }
      } else {
        await loadInfo(gen);
        setAuth('admin');
      }
      if (gen === generation.current) setSession((s) => s + 1);
    } catch (e) {
      if (gen === generation.current) setSessionError(message(e));
    }
  }, [api, loadInfo]);

  // Connection lifecycle.
  useEffect(() => {
    const offStatus = backend.onStatus((s) => {
      setStatus(s);
      if (s === 'open') void startSession();
      else generation.current++;
    });
    backend.connect();
    if (backend.status === 'open') void startSession();
    return () => {
      offStatus();
      backend.close();
    };
  }, [backend, startSession]);

  // Pushed updates keep serverInfo fresh.
  useEffect(() => {
    const offs = [
      backend.onEvent('priorities-update', (e) => {
        const d = e.data as { priorities?: PriorityInfo[] };
        setServerInfo((si) => (si && d.priorities ? { ...si, priorities: d.priorities } : si));
      }),
      backend.onEvent('components-update', (e) => {
        const d = e.data as ComponentInfo;
        setServerInfo((si) => {
          if (!si?.components || typeof d?.name !== 'string') return si;
          const exists = si.components.some((c) => c.name === d.name);
          return {
            ...si,
            components: exists ? si.components.map((c) => (c.name === d.name ? { ...c, enabled: d.enabled } : c)) : [...si.components, d],
          };
        });
      }),
      backend.onEvent('instance-update', (e) => {
        if (Array.isArray(e.data)) setServerInfo((si) => (si ? { ...si, instance: e.data as InstanceInfo[] } : si));
      }),
      backend.onEvent('leds-update', (e) => {
        const d = e.data as { leds?: ServerInfo['leds'] };
        setServerInfo((si) => (si && Array.isArray(d.leds) ? { ...si, leds: d.leds } : si));
      }),
    ];
    return () => offs.forEach((off) => off());
  }, [backend]);

  const login = useCallback(
    async (password: string) => {
      const token = await api.login(password);
      if (token) writeToken(token);
      readOnlyChosen.current = false;
      const gen = ++generation.current;
      await loadInfo(gen); // re-subscribe: settings-update is only granted to admin sessions
      setAuth('admin');
      setSession((s) => s + 1);
    },
    [api, loadInfo],
  );

  const continueReadOnly = useCallback(() => {
    readOnlyChosen.current = true;
    const gen = ++generation.current;
    setAuth('checking');
    loadInfo(gen)
      .then(() => {
        if (gen !== generation.current) return;
        setAuth('readOnly');
        setSession((s) => s + 1);
      })
      .catch((e) => {
        if (gen === generation.current) {
          setSessionError(message(e));
          setAuth('login');
        }
      });
  }, [loadInfo]);

  const requestLogin = useCallback(() => {
    readOnlyChosen.current = false;
    setAuth('login');
  }, []);

  const logout = useCallback(async () => {
    writeToken(null);
    readOnlyChosen.current = false;
    await api.logout().catch(() => undefined);
    void startSession();
  }, [api, startSession]);

  const value: ConnectionContextValue = {
    backend,
    api,
    status,
    auth,
    session,
    defaultPassword,
    tokenRequired,
    serverInfo,
    sysInfo,
    sessionError,
    login,
    continueReadOnly,
    requestLogin,
    logout,
  };
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useConnection(): ConnectionContextValue {
  const v = useContext(Ctx);
  if (!v) throw new Error('useConnection outside ConnectionProvider');
  return v;
}
