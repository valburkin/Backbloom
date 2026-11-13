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
  test: {
    globals: true,
    environment: 'node',
    // npm run coverage (see docs/development.md)
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'lcov', 'cobertura'],
      reportsDirectory: 'coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/**/__fixtures__/**',
        'src/**/*.d.ts',
        'src/main.tsx',
        // the demo backend behind ?mock, not the app's logic
        'src/api/mock.ts',
        'src/api/mockHardware.ts',
        // types only
        'src/layout/types.ts',
      ],
      // a floor a little under the measured numbers (logic modules, *.ts: above 90 % of lines), so a change that
      // drops tests shows; components are mostly covered by rendering tests of whole pages
      thresholds: { lines: 63, statements: 63, branches: 76, functions: 59 },
    },
  },
});
