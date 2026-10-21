// Wire types for the Backbloom JSON-RPC protocol (WebSocket on the web server port, default 18290).
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
  /** Rejected settings (`setconfig`, `load-db`, `adjustment`): every invalid value, `path` from the section on. */
  errors?: Array<{ path: string; message: string }>;
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
  /** Human-readable endpoint, e.g. "ws://localhost:18290". */
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
  /** Effect name for EFFECT priorities, grabber name for grabbers. */
  owner?: string;
  active: boolean;
  visible: boolean;
  value?: { RGB?: [number, number, number] };
}

/** One entry of `serverinfo.adjustment` / `color.channelAdjustment` (only fields the UI reads are listed). */
export interface ChannelAdjustment {
  scaleOutput?: number;
  gamma?: number;
  saturationGain?: number;
  luminanceGain?: number;
  [key: string]: unknown;
}

/** One LED controller as `serverinfo.controllers` / `controllers-update` report it (Backbloom .NET backend). */
export interface ControllerInfo {
  id: string;
  name: string;
  /** LED driver type ("" when none is configured). */
  type: string;
  enabled: boolean;
  /** False when nothing is sent to it (disabled, or unusable driver settings: see `status`). */
  running: boolean;
  state: 'off' | 'opening' | 'ready' | 'waitingToRetry' | 'failed' | 'closing';
  status?: string | null;
  /** First LED of this controller in `leds` and in the live LED stream. */
  start: number;
  ledCount: number;
  /** `start` counts from the controller's first LED. */
  strips: Array<{ id: string; name: string; start: number; length: number; brightness: number }>;
}

export interface ServerInfo {
  hostname?: string;
  controllers?: ControllerInfo[];
  currentInstance?: number;
  instance?: InstanceInfo[];
  components?: ComponentInfo[];
  priorities?: PriorityInfo[];
  priorities_autoselect?: boolean;
  leds?: Array<{ hmin: number; hmax: number; vmin: number; vmax: number; group?: number; disabled?: boolean }>;
  ledDevices?: { available: Array<{ name: string; group: string }> };
  /** Runnable effects; `description` is a Backbloom addition. */
  effects?: Array<{ name: string; description?: string }>;
  activeEffects?: Array<{ name: string; priority: number; timeout: number }>;
  adjustment?: ChannelAdjustment[];
  /** Sound capture; the Backbloom .NET backend adds display names in `sound_devices` (`sound_available` holds ids then). */
  sound?: {
    active?: boolean;
    /** Music effects can run: capture is on, with a device (Backbloom addition; the backend refuses them otherwise). */
    available?: boolean;
    /** True while sound arrives from the device (Backbloom addition). */
    running?: boolean;
    /** Why the device couldn't be opened (Backbloom addition). */
    error?: string;
    device?: string;
    sound_available?: string[];
    sound_devices?: Array<{ id: string; name: string; kind?: 'loopback' | 'microphone' }>;
  };
  systemGrabbers?: SystemGrabbers;
  imageToLedMappingType?: string;
  /** The layout's profile whose roles the strips have now, or null (Backbloom addition; also pushed by profile-update). */
  profile?: ActiveProfile | null;
  [key: string]: unknown;
}

/** A profile of the layout that runs (serverinfo.profile). */
export interface ActiveProfile {
  id: string;
  name: string;
  /** Chosen by hand (setProfile) rather than by a running program. */
  manual: boolean;
}

/** A running program with a window, for the profile editor's program picker (`runningPrograms`). */
export interface RunningProgram {
  /** Executable name, e.g. witcher3.exe (what a profile's `apps` hold). */
  exe: string;
  /** The title of one of its windows. */
  title: string;
}

/** HDR state of the screen capture (`serverinfo.systemGrabbers.hdr`, also pushed by `capture-update`). */
export interface CaptureHdr {
  /** The `systemGrabber.hdrToneMapping` setting. */
  mode: 'auto' | 'on' | 'off';
  /** A monitor is being captured; the other values describe it. */
  capturing: boolean;
  /** Windows drives the captured monitor in HDR mode. */
  monitorHdr: boolean;
  /** HDR colors are converted to SDR (by the HDR capture or the tone mapping table). */
  toneMapping: boolean;
}

/** `serverinfo.systemGrabbers` and the `capture-update` event. `hdr` is missing without screen capture (test pattern). */
export interface SystemGrabbers {
  device?: string;
  modes?: string[];
  hdr?: CaptureHdr;
}

export interface SysInfo {
  backbloom?: { version?: string; build?: string; time?: string; readOnlyMode?: boolean; id?: string; supportUrl?: string };
  system?: {
    hostName?: string;
    prettyName?: string;
    architecture?: string;
    kernelType?: string;
    kernelVersion?: string;
    productType?: string;
    productVersion?: string;
    /** The .NET runtime, e.g. ".NET 10.0.13". */
    runtimeVersion?: string;
    wordSize?: string;
    cpuModelName?: string;
    [key: string]: unknown;
  };
}

/** One message of the `logging-update` stream. */
export interface LogMessage {
  loggerName: string;
  levelString: string;
  message: string;
  /** Milliseconds since the epoch, as a string. */
  utime: string;
  fileName?: string;
  line?: string;
  function?: string;
}

/** One `performance-update` report (see the backend's PerformanceReportType). */
export interface PerformanceReport {
  type: number;
  id: number;
  name: string;
  param1: number;
  param2: number;
  param3: number;
  param4: number;
  token: number;
  remove: number;
  refresh?: number;
  /** LED reports (type 3) of the Backbloom .NET backend: one per controller. */
  controller?: string;
  controllerName?: string;
  ledCount?: number;
  state?: string;
  /** Average time from the engine handing over a frame until the controller had it, ms (-1: no new frames). */
  latencyMs?: number;
}

/** `setup getstate` / `setup-update`: the first-run welcome, HyperHDR coexistence, autostart and remote access. */
export interface SetupState {
  /** The user started the lighting once; until then nothing is sent to the LED controllers. */
  firstRunCompleted: boolean;
  /** Nothing reaches the controllers now (first run pending, or another lighting app runs). */
  outputHeld: boolean;
  /** Another lighting app that drives the LEDs right now (HyperHDR), or null. */
  otherApp: { name: string; executable: string } | null;
  /** The HyperHDR database imported at the first start, or null. */
  importedFrom: string | null;
  autostart: { available: boolean; enabled: boolean };
  remoteAccess: {
    enabled: boolean;
    /** `--bind` decides where the service listens; the setting has no effect. */
    fixedByCommandLine: boolean;
    port: number;
    /** Addresses other devices can open (empty while off). */
    urls: string[];
    advertised: boolean;
  };
  /** The user set a password (or one was imported). Other devices need it; remote access can't be turned on without it. */
  passwordSet: boolean;
  /** Explanations the user has read ("discovery"). */
  acknowledged: string[];
}

export interface DiscoveredDevice {
  name: string;
  value: string;
}

export interface TokenInfo {
  id: string;
  comment: string;
  last_use?: string;
}

/** JSON schema node as used by the backend's settings schema (draft-03 style `required: true`). */
export interface JsonSchema {
  type?: string;
  title?: string;
  default?: unknown;
  enum?: Array<string | number>;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  minItems?: number;
  maxItems?: number;
  step?: number;
  format?: string;
  append?: string;
  access?: 'expert' | 'advanced' | string;
  pattern?: string;
  propertyOrder?: number;
  required?: boolean | string[];
  properties?: Record<string, JsonSchema>;
  items?: JsonSchema;
  additionalProperties?: boolean;
  options?: {
    hidden?: boolean;
    enum_titles?: string[];
    dependencies?: Record<string, unknown>;
  };
  [key: string]: unknown;
}

/** Full settings object as returned by `config getconfig` (per instance, includes global settings). */
export type BackendConfig = Record<string, unknown>;

/** `config getschema`: the settings schema with every LED driver's schema under `properties.alldevices`. */
export interface SettingsSchema extends JsonSchema {
  properties: Record<string, JsonSchema> & { alldevices?: Record<string, JsonSchema> };
}

/**
 * A few looping seconds of effects on the user's LEDs (`effects previews`, Backbloom addition): RGB bytes per LED in
 * the order of `leds`, frame after frame. `standard`: there were no LEDs, so the backend used its standard frame (16
 * LEDs along the top and bottom, 9 per side, clockwise from the top left).
 */
export interface EffectPreviews {
  fps: number;
  ledCount: number;
  standard: boolean;
  frames: Map<string, Uint8Array>;
}
