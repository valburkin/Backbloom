import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// Dev: the UI talks to a running backend over its JSON-RPC WebSocket.
// Override with VITE_BACKEND=ws://host:port
export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: './',
  build: { outDir: 'dist', emptyOutDir: true },
  // The About page bundles the repository's LICENSE and 3RD_PARTY_LICENSES files.
  server: { fs: { allow: ['..'] } },
  test: { globals: true, environment: 'node' },
});
