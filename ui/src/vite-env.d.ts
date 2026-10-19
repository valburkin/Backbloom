/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Backend WebSocket, e.g. "ws://192.168.1.20:8090" or "192.168.1.20:8090". */
  readonly VITE_BACKEND?: string;
  /** "1" runs the UI against the built-in mock backend. */
  readonly VITE_MOCK?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
