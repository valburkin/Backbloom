// Wire types for the HyperHDR JSON-RPC protocol (WebSocket on the web server port, default 8090).
//
// Requests:  { command, subcommand?, tan, ...params }   (the server validates with additionalProperties: false)
// Replies:   { command, tan, success, info?, error? }   (command is "cmd" or "cmd-subcmd"; errors may omit the subcommand,
//                                                        so replies are matched by tan only)
// Events:    { command: "<name>-update", data }         (pushed after `serverinfo` with `subscribe: [...]`)

export type ConnectionStatus = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'closed';

export interface RpcRequest {
  command: string;
  subcommand?: string;
  tan: number;
  [param: string]: unknown;
}

export interface RpcReply {
  command: string;
  tan?: number;
  success: boolean;
  info?: unknown;
  error?: string;
}

export interface RpcEvent {
  command: string;
  data: unknown;
}

export interface CallOptions {
  /** Milliseconds before the promise rejects with RpcTimeoutError. */
  timeoutMs?: number;
}

export class RpcError extends Error {
  readonly command: string;
  readonly reply?: RpcReply;
  constructor(message: string, command: string, reply?: RpcReply) {
    super(message);
    this.name = 'RpcError';
    this.command = command;
    this.reply = reply;
  }
  /** The backend rejected the call because this session isn't signed in (as admin). */
  get isAuthError(): boolean {
    return this.message === 'No Authorization';
  }
}

export class RpcTimeoutError extends RpcError {
  constructor(command: string, timeoutMs: number) {
    super(`No reply to "${command}" within ${Math.round(timeoutMs / 100) / 10} s`, command);
    this.name = 'RpcTimeoutError';
  }
}

export class ConnectionError extends RpcError {
  constructor(message: string, command: string) {
    super(message, command);
    this.name = 'ConnectionError';
  }
}

/** Transport-level interface shared by the live WebSocket client and the mock backend. */
export interface Backend {
  readonly mode: 'live' | 'mock';
  /** Human-readable endpoint, e.g. "ws://localhost:8090". */
  readonly url: string;
  readonly status: ConnectionStatus;
  connect(): void;
  close(): void;
  /** Send one command. Resolves with the success reply; rejects with RpcError / RpcTimeoutError / ConnectionError. */
  call(command: string, subcommand?: string, params?: Record<string, unknown>, options?: CallOptions): Promise<RpcReply>;
  onStatus(listener: (status: ConnectionStatus) => void): () => void;
  /** Subscribe to pushed events by command name ("priorities-update"...), or "*" for all. */
  onEvent(command: string, listener: (event: RpcEvent) => void): () => void;
  /**
   * Read a WLED controller's `GET /json/cfg`. Live: direct browser fetch (WLED sends permissive CORS headers);
   * a backend proxy can replace it later without touching callers.
   */
  fetchWledConfig(host: string, signal?: AbortSignal): Promise<unknown>;
}

// ---- Typed slices of backend payloads (only the fields the UI uses; everything else is passed through) ----

export interface InstanceInfo {
  instance: number;
  friendly_name: string;
  running: boolean;
}

export interface ComponentInfo {
  name: string;
  enabled: boolean;
}

export interface PriorityInfo {
  priority: number;
  componentId: string;
  origin: string;
  owner?: string;
  active: boolean;
  visible: boolean;
}

export interface ServerInfo {
  hostname?: string;
  currentInstance?: number;
  instance?: InstanceInfo[];
  components?: ComponentInfo[];
  priorities?: PriorityInfo[];
  leds?: Array<{ hmin: number; hmax: number; vmin: number; vmax: number; group?: number; disabled?: boolean }>;
  ledDevices?: { available: Array<{ name: string; group: string }> };
  [key: string]: unknown;
}

export interface SysInfo {
  hyperhdr?: { version?: string; build?: string; readOnlyMode?: boolean; id?: string };
  system?: { hostName?: string; prettyName?: string; architecture?: string };
}

/** Full settings object as returned by `config getconfig` (per instance, includes global settings). */
export type BackendConfig = Record<string, unknown>;
