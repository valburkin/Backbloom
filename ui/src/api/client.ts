import { BackendEvents } from './emitter';
import {
  ConnectionError,
  RpcError,
  RpcTimeoutError,
  type Backend,
  type CallOptions,
  type ConnectionStatus,
  type RpcEvent,
  type RpcReply,
  type RpcRequest,
} from './types';

/** The subset of the browser WebSocket the client relies on (lets tests inject a fake). */
export interface WebSocketLike {
  readonly readyState: number;
  onopen: ((ev: unknown) => void) | null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

export type WebSocketFactory = (url: string) => WebSocketLike;

export interface WsClientOptions {
  url: string;
  /** Defaults to the browser WebSocket. */
  createSocket?: WebSocketFactory;
  /** Per-call timeout (the legacy UI used 7 s). */
  timeoutMs?: number;
  /** Reconnect backoff: starts at minDelayMs, doubles per failed attempt, capped at maxDelayMs. */
  minDelayMs?: number;
  maxDelayMs?: number;
}

const WS_OPEN = 1;

interface Pending {
  command: string;
  resolve: (reply: RpcReply) => void;
  reject: (err: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Reconnecting JSON-RPC client. One promise per `tan`; pushed events (no pending tan, has `data`)
 * go to `onEvent` listeners. Calls made while disconnected reject immediately with ConnectionError —
 * per-connection state (auth, subscriptions, current instance) is lost on reconnect anyway, so callers
 * re-run their session setup when the status returns to "open".
 */
export class WsClient implements Backend {
  readonly mode = 'live' as const;
  readonly url: string;
  private readonly createSocket: WebSocketFactory;
  private readonly timeoutMs: number;
  private readonly minDelayMs: number;
  private readonly maxDelayMs: number;

  private socket: WebSocketLike | null = null;
  private _status: ConnectionStatus = 'idle';
  private nextTan = 1;
  private pending = new Map<number, Pending>();
  private events = new BackendEvents();
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private delayMs: number;
  private stopped = false;

  constructor(options: WsClientOptions) {
    this.url = options.url;
    this.createSocket = options.createSocket ?? ((url) => new WebSocket(url) as unknown as WebSocketLike);
    this.timeoutMs = options.timeoutMs ?? 7000;
    this.minDelayMs = options.minDelayMs ?? 500;
    this.maxDelayMs = options.maxDelayMs ?? 10000;
    this.delayMs = this.minDelayMs;
  }

  get status(): ConnectionStatus {
    return this._status;
  }

  connect(): void {
    this.stopped = false;
    if (this.socket || this.reconnectTimer) return;
    this.open(false);
  }

  close(): void {
    this.stopped = true;
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    const s = this.socket;
    this.socket = null;
    if (s) {
      s.onopen = s.onclose = s.onerror = s.onmessage = null;
      try {
        s.close(1000, 'client closed');
      } catch {
        /* ignore */
      }
    }
    this.failPending('Connection closed');
    this.setStatus('closed');
  }

  call(command: string, subcommand?: string, params?: Record<string, unknown>, options?: CallOptions): Promise<RpcReply> {
    const label = subcommand ? `${command}-${subcommand}` : command;
    const socket = this.socket;
    if (!socket || this._status !== 'open' || socket.readyState !== WS_OPEN) {
      return Promise.reject(new ConnectionError('Not connected to the lighting service', label));
    }
    const tan = this.nextTan++;
    if (this.nextTan > 0x7fffffff) this.nextTan = 1;
    const msg: RpcRequest = { ...params, command, tan };
    if (subcommand) msg.subcommand = subcommand;
    const timeoutMs = options?.timeoutMs ?? this.timeoutMs;

    return new Promise<RpcReply>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(tan);
        reject(new RpcTimeoutError(label, timeoutMs));
      }, timeoutMs);
      this.pending.set(tan, { command: label, resolve, reject, timer });
      try {
        socket.send(JSON.stringify(msg));
      } catch (e) {
        clearTimeout(timer);
        this.pending.delete(tan);
        reject(new ConnectionError(e instanceof Error ? e.message : 'Send failed', label));
      }
    });
  }

  onStatus(listener: (status: ConnectionStatus) => void): () => void {
    return this.events.onStatus(listener);
  }

  onEvent(command: string, listener: (event: RpcEvent) => void): () => void {
    return this.events.onEvent(command, listener);
  }

  async fetchWledConfig(host: string, signal?: AbortSignal): Promise<unknown> {
    const res = await fetch(`http://${host}/json/cfg`, { signal, cache: 'no-store' });
    if (!res.ok) throw new Error(`The controller answered with HTTP ${res.status}`);
    return res.json();
  }

  // ---- internals ----

  private setStatus(status: ConnectionStatus): void {
    if (status === this._status) return;
    this._status = status;
    this.events.emitStatus(status);
  }

  private open(isRetry: boolean): void {
    this.reconnectTimer = null;
    this.setStatus(isRetry ? 'reconnecting' : 'connecting');
    let socket: WebSocketLike;
    try {
      socket = this.createSocket(this.url);
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;
    socket.onopen = () => {
      if (this.socket !== socket) return;
      this.delayMs = this.minDelayMs;
      this.setStatus('open');
    };
    socket.onmessage = (ev) => {
      if (this.socket === socket) this.handleMessage(ev.data);
    };
    socket.onerror = () => {
      /* onclose follows and drives reconnects */
    };
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = null;
      this.failPending('Connection lost');
      if (this.stopped) {
        this.setStatus('closed');
        return;
      }
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.stopped) return;
    this.setStatus('reconnecting');
    const delay = this.delayMs;
    this.delayMs = Math.min(this.maxDelayMs, this.delayMs * 2);
    this.reconnectTimer = setTimeout(() => this.open(true), delay);
  }

  private failPending(message: string): void {
    const all = [...this.pending.values()];
    this.pending.clear();
    for (const p of all) {
      clearTimeout(p.timer);
      p.reject(new ConnectionError(message, p.command));
    }
  }

  private handleMessage(data: unknown): void {
    if (typeof data !== 'string') return; // binary frames are live-video images; not used here
    let msg: Record<string, unknown>;
    try {
      msg = JSON.parse(data) as Record<string, unknown>;
    } catch {
      return;
    }
    if (!msg || typeof msg !== 'object') return;
    const tan = typeof msg.tan === 'number' ? msg.tan : undefined;
    // The LED stream's messages carry the tan of "ledcolors ledstream-start"; they are events, not its reply.
    const isEvent = typeof msg.command === 'string' && msg.command.endsWith('-update') && ('data' in msg || 'result' in msg);
    const p = tan !== undefined && !isEvent ? this.pending.get(tan) : undefined;
    if (p && tan !== undefined) {
      this.pending.delete(tan);
      clearTimeout(p.timer);
      const reply = msg as unknown as RpcReply;
      if (reply.success === false) p.reject(new RpcError(String(reply.error ?? 'Unknown error'), p.command, reply));
      else p.resolve(reply);
      return;
    }
    // Pushed events carry `data`; the log and LED streams use `result` instead.
    if (typeof msg.command === 'string' && ('data' in msg || 'result' in msg)) {
      this.events.emitEvent({ command: msg.command, data: 'data' in msg ? msg.data : msg.result });
    }
  }
}
