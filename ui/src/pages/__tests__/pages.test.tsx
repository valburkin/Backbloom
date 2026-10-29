import { describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MockBackend } from '../../api/mock';
import { ConnectionProvider } from '../../app/connection';
import { ConfigProvider } from '../../app/config';
import { EditorProvider } from '../../app/editor';
import { ToastProvider } from '../../app/toast';
import { HomePage } from '../HomePage';
import { DevicesPage } from '../DevicesPage';
import { CapturePage } from '../CapturePage';
import { ColorsPage } from '../ColorsPage';
import { EffectsPage } from '../EffectsPage';
import { SettingsPage } from '../settings/SettingsPage';

function render(page: ReactNode): string {
  return renderToStaticMarkup(
    <ToastProvider>
      <ConnectionProvider backend={new MockBackend()}>
        <ConfigProvider>
          <EditorProvider>{page}</EditorProvider>
        </ConfigProvider>
      </ConnectionProvider>
    </ToastProvider>,
  );
}

// Pages must render before any data has arrived (no server info, no config yet).
describe('pages render without data', () => {
  const cases: Array<[string, ReactNode]> = [
    ['Home', <HomePage />],
    ['Devices', <DevicesPage />],
    ['Screen sync', <CapturePage />],
    ['Colors', <ColorsPage />],
    ['Effects & Music', <EffectsPage />],
    ...['general', 'remote-access', 'integrations', 'support', 'about'].map((t): [string, ReactNode] => [`Settings/${t}`, <SettingsPage tab={t} onTab={() => {}} />]),
  ];
  it.each(cases)('%s', (name, page) => {
    const html = render(page);
    expect(html).toContain(name.split(/[/ ]/)[0]);
  });
});
