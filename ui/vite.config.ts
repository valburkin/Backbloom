import { defineConfig } from 'vitest/config';
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
      // a floor a little under the measured numbers, so a change that drops tests shows; components are mostly covered
      // by rendering tests of whole pages. Vitest 4 and later map V8's coverage onto the source's syntax tree, which
      // counts more branches and functions than Vitest 3 did (same tests: lines 65.8 % became 60.8 %, branches 78.1 %
      // became 52.3 %; Vitest 3 with experimentalAstAwareRemapping measures the same as Vitest 5)
      thresholds: { lines: 59, statements: 57, branches: 51, functions: 47 },
    },
  },
});
