import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { encode } from 'uqr';
import config from '../../../api/__fixtures__/config.json';
import serverInfo from '../../../api/__fixtures__/serverinfo.json';
import type { BackendConfig, ServerInfo, SetupState } from '../../../api/types';
import { MockBackend, MOCK_LAN_URL, MOCK_PASSWORD } from '../../../api/mock';
import { RpcApi } from '../../../api/rpcApi';
import { qrPath } from '../../../components/QrCode';
import { DiscoveryDialog } from '../../devices/DriverForm';
import { RemoteAccessAddresses, RemoteAccessDialog } from '../../settings/RemoteAccess';
import { driverTarget, monitorName, welcomeSummary } from '../summary';
import { WelcomeView, type WelcomeViewProps } from '../WelcomeScreen';

const setup: SetupState = {
  firstRunCompleted: false,
  outputHeld: true,
  otherApp: null,
  importedFrom: 'C:\\Users\\me\\AppData\\Local\\HyperHDR\\db\\hyperhdr.db',
  autostart: { available: true, enabled: false },
  remoteAccess: { enabled: false, fixedByCommandLine: false, port: 18290, urls: [], advertised: false },
  acknowledged: [],
};

const noop = () => {};
function view(overrides: Partial<WelcomeViewProps> = {}): string {
  return renderToStaticMarkup(
    <WelcomeView
      setup={setup}
      config={config as BackendConfig}
      serverInfo={serverInfo as ServerInfo}
      canStart
      starting={false}
      error={null}
      autostart={false}
      onAutostart={noop}
      onStart={noop}
      onSignIn={noop}
      onLookAround={noop}
      otherAppQuit={false}
      {...overrides}
    />,
  );
}

async function connected(options: ConstructorParameters<typeof MockBackend>[0] = {}) {
  const backend = new MockBackend({ latencyMs: 0, ...options });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new RpcApi(backend) };
}

describe('welcome summary', () => {
  it('lists what the imported settings drive', async () => {
    const { api } = await connected();
    const rows = welcomeSummary(await api.getConfig(), await api.serverInfo());
    const byLabel = Object.fromEntries(rows.map((r) => [r.label, r.value]));
    expect(byLabel.LEDs).toMatch(/^168 LEDs/);
    expect(byLabel.Controller).toContain('WLED');
    expect(byLabel['Screen capture']).toBeTruthy();
    expect(byLabel['Startup effect']).toBe('Rainbow swirl fast (3 s)');
    expect(Object.keys(byLabel)).toEqual(['LEDs', 'Controller', 'Screen capture', 'Startup effect', 'Background effect']);
  });

  it('works without the settings (not signed in)', async () => {
    const { api } = await connected();
    const rows = welcomeSummary(null, await api.serverInfo());
    expect(rows.map((r) => r.label)).toEqual(['LEDs', 'Controller']);
    expect(rows[0].value).toMatch(/^168 LEDs/);
    expect(welcomeSummary(null, null)[1]).toEqual({ label: 'Controllers', value: 'none configured', details: undefined });
  });

  it('names monitors and driver targets in words', () => {
    expect(monitorName('auto')).toBe('Main monitor');
    expect(monitorName('\\\\.\\DISPLAY5|NVIDIA GeForce RTX 5080')).toBe('Display 5 (NVIDIA GeForce RTX 5080)');
    expect(monitorName('MULTI-MONITOR|NVIDIA GeForce RTX 5080')).toBe('All monitors of NVIDIA GeForce RTX 5080');
    expect(driverTarget({ type: 'wled', host: 'wled-desk.local' })).toBe('wled-desk.local');
    expect(driverTarget({ type: 'file', output: '\\\\.\\NUL' })).toBe('colors discarded');
    expect(driverTarget({ type: 'adalight', output: 'COM3' })).toBe('COM3');
  });
});

describe('welcome screen', () => {
  it('offers Start lighting, with autostart off by default', () => {
    const html = view();
    expect(html).toContain('Welcome to Overglow');
    expect(html).toContain('Your HyperHDR settings were imported');
    expect(html).toContain('Start lighting');
    expect(html).toContain('Start Overglow when I sign in to Windows');
    expect(html).not.toMatch(/type="checkbox"[^>]*checked/);
    expect(html).toContain('Nothing is sent to your LEDs until you start the lighting.');
    expect(html).toContain('hyperhdr.db');
  });

  it('waits while HyperHDR runs and tells how to quit it', () => {
    const html = view({ setup: { ...setup, otherApp: { name: 'HyperHDR', executable: 'hyperhdr.exe' } } });
    expect(html).toContain('HyperHDR is controlling your lights right now. Quit HyperHDR (tray icon → Quit) to let Overglow take over.');
    expect(html).toContain('Don’t start HyperHDR with Windows');
    expect(html).toMatch(/<button[^>]*disabled[^>]*>.*Waiting for HyperHDR to quit…/);
    expect(html).not.toContain('>Start lighting<');
  });

  it('says when HyperHDR has quit', () => {
    expect(view({ otherAppQuit: true })).toContain('HyperHDR has quit.');
  });

  it('asks to sign in before starting', () => {
    const html = view({ canStart: false, config: null });
    expect(html).toContain('Sign in to start');
    expect(html).toContain('Sign in to see the capture and effect settings.');
  });

  it('hides autostart where the app can’t start with the system', () => {
    expect(view({ setup: { ...setup, autostart: { available: false, enabled: false } } })).not.toContain('sign in to Windows');
  });
});

describe('network explanations', () => {
  it('explains remote access and the firewall prompt before turning it on', () => {
    const html = renderToStaticMarkup(<RemoteAccessDialog open busy={false} onCancel={noop} onConfirm={noop} />);
    expect(html).toContain('Let your phone and other devices connect?');
    expect(html).toContain('Windows will ask next');
    expect(html).toContain('Private');
    expect(html).toContain('not Public networks');
    expect(html).toContain('Turn off');
    expect(html).toContain('Turn on remote access');
    expect(renderToStaticMarkup(<RemoteAccessDialog open={false} busy={false} onCancel={noop} onConfirm={noop} />)).toBe('');
  });

  it('shows the address and a QR code once on', () => {
    const html = renderToStaticMarkup(<RemoteAccessAddresses status={{ ...setup.remoteAccess, enabled: true, urls: [MOCK_LAN_URL] }} />);
    expect(html).toContain(MOCK_LAN_URL);
    expect(html).toContain(`aria-label="QR code for ${MOCK_LAN_URL}"`);
    expect(renderToStaticMarkup(<RemoteAccessAddresses status={{ ...setup.remoteAccess, enabled: true }} />)).toContain('no network address');
  });

  it('explains the network search before the first Find', () => {
    const html = renderToStaticMarkup(<DiscoveryDialog open onCancel={noop} onConfirm={noop} />);
    expect(html).toContain('Search your network for LED controllers?');
    expect(html).toContain('doesn’t open a port');
  });
});

describe('QR code', () => {
  it('encodes the address with finder patterns in the corners', () => {
    const { data, size } = encode(MOCK_LAN_URL, { ecc: 'M', border: 2 });
    expect(size).toBe(29); // version 2 (25 modules) + 2 x 2 border
    expect(data[2].slice(2, 9).every(Boolean)).toBe(true); // top edge of the top-left finder
    expect(data[3].slice(3, 8).some(Boolean)).toBe(false); // its white ring
    const { size: s, path } = qrPath(MOCK_LAN_URL);
    expect(s).toBe(size);
    expect(path.startsWith('M2 2h1v1h-1z')).toBe(true);
    expect(qrPath(MOCK_LAN_URL).path).toBe(path);
  });
});

describe('mock backend setup flow (like the .NET app)', () => {
  it('holds the lights until started and while HyperHDR runs', async () => {
    const { backend, api } = await connected({ firstRun: true, otherApp: true });
    const events: SetupState[] = [];
    backend.onEvent('setup-update', (e) => events.push(e.data as SetupState));

    const state = await api.setupState();
    expect([state.firstRunCompleted, state.outputHeld, state.otherApp?.name]).toEqual([false, true, 'HyperHDR']);
    expect(state.autostart).toEqual({ available: true, enabled: false });

    const started = await api.startLighting(true);
    expect([started.firstRunCompleted, started.outputHeld, started.autostart.enabled]).toEqual([true, true, true]);

    backend.quitOtherApp();
    await new Promise((r) => setTimeout(r, 0));
    expect(events.at(-1)?.outputHeld).toBe(false);
    expect((await api.acknowledge('discovery')).acknowledged).toEqual(['discovery']);
  });

  it('turns remote access on through the setting', async () => {
    const { backend, api } = await connected();
    const events: SetupState[] = [];
    backend.onEvent('setup-update', (e) => events.push(e.data as SetupState));
    const network = (await api.getConfig()).network as Record<string, unknown>;
    expect(network.remoteAccess).toBe(false);

    await api.setConfig({ network: { ...network, remoteAccess: true } });
    await new Promise((r) => setTimeout(r, 0));
    expect(events.at(-1)?.remoteAccess).toMatchObject({ enabled: true, urls: [MOCK_LAN_URL] });
  });

  it('needs the password to start', async () => {
    const { api } = await connected({ requireAuth: true, firstRun: true });
    await expect(api.setupState()).resolves.toMatchObject({ firstRunCompleted: false });
    await expect(api.startLighting()).rejects.toThrow('No Authorization');
    await api.login(MOCK_PASSWORD);
    await expect(api.startLighting()).resolves.toMatchObject({ firstRunCompleted: true, outputHeld: false });
  });
});
