import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WsClient, type WebSocketLike } from '../client';
import { ConnectionError, RpcError, RpcTimeoutError, type ConnectionStatus } from '../types';
import { normalizeBackendUrl, resolveBackendUrl } from '../backend';

class FakeSocket implements WebSocketLike {
  static instances: FakeSocket[] = [];
  readyState = 0;
  sent: Array<Record<string, unknown>> = [];
  onopen: ((ev: unknown) => void) | null = null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  closed = false;
  constructor(public url: string) {
    FakeSocket.instances.push(this);
  }
  send(data: string) {
    this.sent.push(JSON.parse(data));
  }
  close() {
    this.closed = true;
    this.readyState = 3;
  }
  // test helpers
  open() {
    this.readyState = 1;
    this.onopen?.({});
  }
  receive(msg: unknown) {
    this.onmessage?.({ data: typeof msg === 'string' ? msg : JSON.stringify(msg) });
  }
  drop() {
    this.readyState = 3;
    this.onclose?.({ code: 1006 });
  }
  get last() {
    return this.sent[this.sent.length - 1];
  }
}

const latest = () => FakeSocket.instances[FakeSocket.instances.length - 1];

function makeClient(opts: Partial<ConstructorParameters<typeof WsClient>[0]> = {}) {
  const statuses: ConnectionStatus[] = [];
  const c = new WsClient({ url: 'ws://test:8090', createSocket: (u) => new FakeSocket(u), timeoutMs: 1000, minDelayMs: 100, maxDelayMs: 400, ...opts });
  c.onStatus((s) => statuses.push(s));
  return { c, statuses };
}

beforeEach(() => {
  FakeSocket.instances = [];
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('WsClient', () => {
  it('connects and reports status transitions', () => {
    const { c, statuses } = makeClient();
    c.connect();
    expect(latest().url).toBe('ws://test:8090');
    expect(c.status).toBe('connecting');
    latest().open();
    expect(c.status).toBe('open');
    expect(statuses).toEqual(['connecting', 'open']);
  });

  it('sends {command, subcommand, tan, ...params} and resolves the reply matched by tan', async () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    const p1 = c.call('config', 'getconfig');
    const p2 = c.call('leddevice', 'identify', { ledDeviceType: 'blink', params: { blinkIndex: 17 } });
    const [m1, m2] = latest().sent;
    expect(m1).toEqual({ command: 'config', subcommand: 'getconfig', tan: 1 });
    expect(m2).toEqual({ command: 'leddevice', subcommand: 'identify', tan: 2, ledDeviceType: 'blink', params: { blinkIndex: 17 } });
    // replies out of order
    latest().receive({ command: 'leddevice-identify', tan: 2, success: true });
    latest().receive({ command: 'config-getconfig', tan: 1, success: true, info: { leds: [] } });
    await expect(p1).resolves.toMatchObject({ info: { leds: [] } });
    await expect(p2).resolves.toMatchObject({ success: true });
  });

  it('omits subcommand when not given', () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    void c.call('serverinfo', undefined, { subscribe: ['leds-update'] });
    expect(latest().last).toEqual({ command: 'serverinfo', tan: 1, subscribe: ['leds-update'] });
  });

  it('rejects with RpcError on success:false, even if the error reply drops the subcommand', async () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    const p = c.call('config', 'setconfig', { config: {} });
    latest().receive({ command: 'config', tan: 1, success: false, error: 'No Authorization' });
    const err = await p.catch((e) => e);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.message).toBe('No Authorization');
    expect(err.isAuthError).toBe(true);
    expect(err.command).toBe('config-setconfig');
  });

  it('times out unanswered calls and ignores late replies', async () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    const p = c.call('sysinfo');
    vi.advanceTimersByTime(1001);
    await expect(p).rejects.toBeInstanceOf(RpcTimeoutError);
    expect(() => latest().receive({ command: 'sysinfo', tan: 1, success: true })).not.toThrow();
  });

  it('honors per-call timeouts', async () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    const p = c.call('config', 'setconfig', {}, { timeoutMs: 5000 });
    vi.advanceTimersByTime(2000);
    latest().receive({ command: 'config-setconfig', tan: 1, success: true });
    await expect(p).resolves.toMatchObject({ success: true });
  });

  it('rejects immediately when not connected', async () => {
    const { c } = makeClient();
    await expect(c.call('serverinfo')).rejects.toBeInstanceOf(ConnectionError);
    c.connect();
    await expect(c.call('serverinfo')).rejects.toBeInstanceOf(ConnectionError); // still connecting
  });

  it('dispatches pushed events to command and wildcard listeners', () => {
    const { c } = makeClient();
    const got: string[] = [];
    c.onEvent('priorities-update', (e) => got.push(`p:${JSON.stringify(e.data)}`));
    const off = c.onEvent('*', (e) => got.push(`*:${e.command}`));
    c.connect();
    latest().open();
    latest().receive({ command: 'priorities-update', data: { priorities: [] } });
    off();
    latest().receive({ command: 'leds-update', data: { leds: [] } });
    expect(got).toEqual(['p:{"priorities":[]}', '*:priorities-update']);
  });

  it('ignores binary frames and malformed JSON', () => {
    const { c } = makeClient();
    const spy = vi.fn();
    c.onEvent('*', spy);
    c.connect();
    latest().open();
    latest().onmessage?.({ data: new Uint8Array([1, 2, 3]) });
    latest().receive('{not json');
    expect(spy).not.toHaveBeenCalled();
  });

  it('fails pending calls on disconnect and reconnects with exponential backoff', async () => {
    const { c, statuses } = makeClient();
    c.connect();
    latest().open();
    const p = c.call('serverinfo');
    latest().drop();
    await expect(p).rejects.toBeInstanceOf(ConnectionError);
    expect(c.status).toBe('reconnecting');

    expect(FakeSocket.instances).toHaveLength(1);
    vi.advanceTimersByTime(100);
    expect(FakeSocket.instances).toHaveLength(2);
    latest().drop(); // failed attempt → next delay 200
    vi.advanceTimersByTime(199);
    expect(FakeSocket.instances).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(FakeSocket.instances).toHaveLength(3);
    latest().drop(); // → 400 (cap)
    vi.advanceTimersByTime(400);
    expect(FakeSocket.instances).toHaveLength(4);
    latest().drop(); // stays at cap
    vi.advanceTimersByTime(400);
    expect(FakeSocket.instances).toHaveLength(5);
    latest().open();
    expect(c.status).toBe('open');
    expect(statuses.filter((s) => s === 'open')).toHaveLength(2);

    // backoff resets after a successful open
    latest().drop();
    vi.advanceTimersByTime(100);
    expect(FakeSocket.instances).toHaveLength(6);
  });

  it('tans keep increasing across reconnects', () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    void c.call('serverinfo').catch(() => {});
    latest().drop();
    vi.advanceTimersByTime(100);
    latest().open();
    void c.call('serverinfo');
    expect(latest().last.tan).toBe(2);
  });

  it('close() stops reconnecting and rejects pending calls', async () => {
    const { c } = makeClient();
    c.connect();
    latest().open();
    const p = c.call('serverinfo');
    const sock = latest();
    c.close();
    await expect(p).rejects.toBeInstanceOf(ConnectionError);
    expect(sock.closed).toBe(true);
    expect(c.status).toBe('closed');
    vi.advanceTimersByTime(10000);
    expect(FakeSocket.instances).toHaveLength(1);
  });

  it('ignores events from a socket that was replaced', () => {
    const { c } = makeClient();
    c.connect();
    const first = latest();
    c.close();
    c.connect();
    first.open();
    expect(c.status).toBe('connecting');
    latest().open();
    expect(c.status).toBe('open');
  });
});

describe('backend URL resolution', () => {
  const loc = (over: Partial<{ protocol: string; host: string; hostname: string; search: string }> = {}) => ({
    protocol: 'http:',
    host: 'pc.local:8090',
    hostname: 'pc.local',
    search: '',
    ...over,
  });

  it('defaults to the page host (backend serves UI + WebSocket on one port)', () => {
    expect(resolveBackendUrl(loc())).toBe('ws://pc.local:8090');
    expect(resolveBackendUrl(loc({ protocol: 'https:', host: 'pc.local:8092' }))).toBe('wss://pc.local:8092');
  });

  it('uses port 8090 on the page hostname in dev', () => {
    expect(resolveBackendUrl(loc({ host: 'localhost:5173', hostname: 'localhost' }), undefined, true)).toBe('ws://localhost:8090');
  });

  it('prefers ?backend over VITE_BACKEND over the default', () => {
    expect(resolveBackendUrl(loc(), '10.0.0.2:8090')).toBe('ws://10.0.0.2:8090');
    expect(resolveBackendUrl(loc({ search: '?backend=10.0.0.3:8090' }), '10.0.0.2:8090')).toBe('ws://10.0.0.3:8090');
  });

  it('normalizes http/ws/bare forms', () => {
    expect(normalizeBackendUrl('http://a:1/')).toBe('ws://a:1');
    expect(normalizeBackendUrl('https://a:1')).toBe('wss://a:1');
    expect(normalizeBackendUrl('ws://a:1')).toBe('ws://a:1');
    expect(normalizeBackendUrl('a:1', true)).toBe('wss://a:1');
  });
});
