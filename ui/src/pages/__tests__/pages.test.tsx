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
import { CalibrationPage } from '../CalibrationPage';
import { EffectsPage } from '../EffectsPage';
import { GamesPage } from '../GamesPage';
import { SettingsPage } from '../settings/SettingsPage';
import { ModeSwitch } from '../../control/ModeSwitch';

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
    ['Screen', <CapturePage />],
    ['Calibration', <CalibrationPage />],
    ['Colors', <ColorsPage />],
    ['Effects & Music', <EffectsPage />],
    ['Games', <GamesPage />],
    ...['general', 'remote-access', 'integrations', 'support', 'about'].map((t): [string, ReactNode] => [`Settings/${t}`, <SettingsPage tab={t} onTab={() => {}} />]),
  ];
  it.each(cases)('%s', (name, page) => {
    const html = render(page);
    expect(html).toContain(name.split(/[/ ]/)[0]);
  });
});

describe('mode switch', () => {
  it('offers Ambient and Game, disabled until the server reports the mode', () => {
    const html = render(<ModeSwitch />);
    expect(html).toContain('aria-label="Mode"');
    expect(html).toContain('aria-label="Ambient"');
    expect(html).toContain('aria-label="Game"');
    expect(html).toContain('disabled');
  });
});
