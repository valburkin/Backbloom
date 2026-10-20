/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend WebSocket, e.g. "ws://192.168.1.20:18290" or "192.168.1.20:18290". */
  readonly VITE_BACKEND?: string;
  /** "1" runs the UI against the built-in mock backend. */
  readonly VITE_MOCK?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
