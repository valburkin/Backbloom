import { defineConfig } from 'vite';

// Link previews need absolute URLs. Netlify sets URL to the site's main address during its build.
const siteUrl = (process.env.URL ?? 'http://127.0.0.1:5190').replace(/\/$/, '');

export default defineConfig({
  server: { host: '127.0.0.1', port: 5190, strictPort: true },
  preview: { host: '127.0.0.1', port: 5191, strictPort: true },
  build: {
    target: 'es2022',
    rollupOptions: { input: { main: 'index.html', privacy: 'privacy.html' } },
  },
  plugins: [
    {
      name: 'site-url',
      transformIndexHtml: (html) => html.replaceAll('%SITE_URL%', siteUrl),
    },
  ],
});
