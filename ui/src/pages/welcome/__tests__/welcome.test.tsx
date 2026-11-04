import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { encode } from 'uqr';
import config from '../../../api/__fixtures__/config.json';
import serverInfo from '../../../api/__fixtures__/serverinfo.json';
import type { BackendConfig, ServerInfo, SetupState } from '../../../api/types';
import { MockBackend, MOCK_LAN_URL, MOCK_PASSWORD, REMOTE_ACCESS_NEEDS_PASSWORD } from '../../../api/mock';
import { RpcApi } from '../../../api/rpcApi';
import { qrPath } from '../../../components/QrCode';
import { DiscoveryDialog } from '../../devices/DriverForm';
import { RemoteAccessAddresses, RemoteAccessDialog } from '../../settings/RemoteAccess';
import { deviceLine, driverTarget, importedItems, monitorName, welcomeSummary } from '../summary';
import { FIRST_BRIGHTNESS, firstBrightness, nextPage, WelcomeView, type WelcomeViewProps } from '../WelcomeScreen';
import type { Controller } from '../../../layout';
import { addedAddresses } from '../../devices/drivers';
import { FoundDevices } from '../../devices/DriverForm';

const setup: SetupState = {
  firstRunCompleted: false,
  outputHeld: true,
  otherApp: null,
  importedFrom: 'C:\\Users\\me\\AppData\\Local\\HyperHDR\\db\\hyperhdr.db',
  autostart: { available: true, enabled: false },
  remoteAccess: { enabled: false, fixedByCommandLine: false, port: 18290, urls: [], advertised: false },
  passwordSet: false,
  acknowledged: [],
};

const wled: Controller = {
  id: 'desk',
  name: 'Desk',
  kind: 'wled',
  host: 'wled-desk.local',
  outputs: [{ index: 0, start: 0, length: 91 }, { index: 1, start: 91, length: 77 }],
  device: { type: 'wled', host: 'wled-desk.local' },
};
const testDevice: Controller = { id: 'controller-1', name: 'LED controller', kind: 'file', host: '', outputs: [{ index: 0, start: 0, length: 1 }], device: { type: 'file', output: '/dev/null' } };

const noop = () => {};
function view(overrides: Partial<WelcomeViewProps> = {}): string {
  return renderToStaticMarkup(
    <WelcomeView
      setup={setup}
      config={config as BackendConfig}
      serverInfo={serverInfo as ServerInfo}
      step={0}
      onStep={noop}
      devices={[wled]}
      onAddDevice={noop}
      next="layout"
      expert={false}
      starting={false}
      error={null}
      autostart={false}
      onAutostart={noop}
      onStart={noop}
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

describe('setup', () => {
  it('welcomes and says what came over from HyperHDR, without technical details', () => {
    const html = view();
    expect(html).toContain('Welcome to Backbloom');
    expect(html).toContain('Step 1 of 5');
    expect(html).toContain('Copied from HyperHDR');
    expect(html).toContain('Your device: Desk (WLED at wled-desk.local)');
    expect(html).toContain('168 LEDs and where they are');
    expect(html).toContain('Look around first');
    expect(html).not.toContain('hyperhdr.db');
    expect(html).not.toContain('Screen capture');
  });

  it('shows the technical details with expert tools on', () => {
    const html = view({ expert: true });
    expect(html).toContain('Technical details');
    expect(html).toContain('hyperhdr.db');
  });

  it('says nothing about an import on a new install', () => {
    expect(view({ setup: { ...setup, importedFrom: null } })).not.toContain('HyperHDR');
  });

  it('explains briefly that it waits while HyperHDR runs, and when it has quit', () => {
    const html = view({ setup: { ...setup, otherApp: { name: 'HyperHDR', executable: 'hyperhdr.exe' } } });
    expect(html).toContain('HyperHDR is controlling your lights right now.');
    expect(html).toContain('Backbloom waits and takes over by itself when you quit HyperHDR');
    expect(view({ otherAppQuit: true })).toContain('HyperHDR has quit.');
  });

  it('confirms the device, or asks to add one instead of the test device', () => {
    const found = view({ step: 1 });
    expect(found).toContain('Your lights');
    expect(found).toContain('Desk (WLED)');
    expect(found).not.toContain('wled-desk.local');
    expect(view({ step: 1, expert: true })).toContain('Desk (WLED at wled-desk.local)');
    expect(found).toContain('168 LEDs');
    // the hub: what is added, three ways to add more, one primary to go on
    expect(found).toContain('Add more');
    expect(found).toContain('LED strips on a WLED board');
    expect(found).toContain('Room lights');
    expect(found).toContain('Another kind of device');
    expect(found.match(/bg-brand text-accent-ink font-semibold/g)).toHaveLength(1);
    expect(found).toMatch(/>Next/);

    const none = view({ step: 1, devices: [testDevice] });
    // the ways to add lights right here: a new WLED strip, room lights, or a device that is set up already
    expect(none).toContain('What do you have?');
    expect(none).toContain('LED strips on a WLED board');
    expect(none).toContain('Room lights');
    expect(none).toContain('Another kind of device');
    expect(none).toContain('Skip for now');
    expect(none).not.toContain('/dev/null');
    expect(view({ step: 1, devices: null })).toContain('Looking at your setup');
  });

  it('asks what each light does, and shows a trace with its own buttons instead', () => {
    const roles = view({ step: 2 });
    expect(roles).toContain('What each light does');
    expect(roles).toContain('Skip — use the defaults');
    expect(roles.match(/bg-brand text-accent-ink font-semibold/g)).toHaveLength(1);
    const trace = view({ step: 2, trace: { title: 'Trace your strip', intro: 'LED 1 is bright.', children: <p>drawing</p>, actions: <button type="button">Use this route</button> } });
    expect(trace).toContain('Trace your strip');
    expect(trace).toContain('Use this route');
    expect(trace).not.toContain('Skip — use the defaults');
    expect(trace).not.toContain('>Back<');
  });

  it('turns the lights on, with Start with Windows off by default', () => {
    const html = view({ step: 4 });
    expect(html).toContain('Turn on your lights');
    expect(html).toContain('Your lights stay as they are until you turn them on here.');
    expect(html).toContain('Then show Backbloom where your strips are');
    expect(html).toContain('>Turn on lights<');
    expect(html).toContain('Start with Windows');
    expect(html).toContain('Off unless you check it. You can change it later in Settings or from the tray icon.');
    expect(html).not.toMatch(/type="checkbox"[^>]*checked/);
    expect(view({ step: 4, setup: { ...setup, autostart: { available: false, enabled: false } } })).not.toContain('Start with Windows');
  });

  it('can turn on while HyperHDR runs: Backbloom takes over when it quits', () => {
    const html = view({ step: 4, setup: { ...setup, otherApp: { name: 'HyperHDR', executable: 'hyperhdr.exe' } } });
    expect(html).toContain('HyperHDR still runs');
    expect(html).not.toMatch(/<button[^>]* disabled=""[^>]*>(<[^>]*>)*Turn on lights/);
  });

  it('goes on where setup isn’t finished', () => {
    expect(nextPage([testDevice], true)).toBe('devices');
    expect(nextPage([], true)).toBe('devices');
    // Turn on saves the layout: Home, unless it can't be saved as it is
    expect(nextPage([wled], false)).toBe('layout');
    expect(nextPage([wled, testDevice], true)).toBe('home');
  });

  it('lists what was imported in plain words', () => {
    expect(importedItems([wled], 168)).toEqual(['Your device: Desk (WLED at wled-desk.local)', '168 LEDs and where they are', 'Your screen sync, color and effect settings']);
    expect(importedItems([testDevice], 0)).toEqual(['Your screen sync, color and effect settings']);
    expect(deviceLine({ ...wled, name: 'LED controller' })).toBe('WLED at wled-desk.local');
  });
});

describe('first brightness', () => {
  it('starts a new install at 60% on Home, unless the brightness was changed or the settings were imported', () => {
    expect(FIRST_BRIGHTNESS).toBeCloseTo(0.22, 2);
    expect(firstBrightness(null, undefined)).toBe(FIRST_BRIGHTNESS);
    expect(firstBrightness(null, 1)).toBe(FIRST_BRIGHTNESS);
    expect(firstBrightness(null, 0.6)).toBeNull();
    expect(firstBrightness('C:\\HyperHDR', 1)).toBeNull();
  });
});

describe('device search results', () => {
  it('marks devices that are already added and keeps them from being picked', () => {
    const added = addedAddresses({ version: 1, room: undefined as never, controllers: [wled, testDevice], strips: [] });
    expect([...added]).toEqual(['wled-desk.local']);
    const html = renderToStaticMarkup(
      <FoundDevices
        found={[
          { value: 'wled-desk.local', name: '192.168.1.40 (wled-desk.local)' },
          { value: 'wled-shelf.local', name: '192.168.1.57 (wled-shelf.local)' },
        ]}
        usb={false}
        added={added}
        onPick={noop}
      />,
    );
    expect(html.match(/Already added/g)).toHaveLength(1);
    expect(html.match(/<button[^>]* disabled=""/g)).toHaveLength(1);
  });
});

describe('network explanations', () => {
  it('explains remote access and the firewall prompt before turning it on', () => {
    const html = renderToStaticMarkup(<RemoteAccessDialog open busy={false} passwordSet onCancel={noop} onConfirm={noop} />);
    expect(html).toContain('Let your phone and other devices connect?');
    expect(html).toContain('Windows will ask next');
    expect(html).toContain('Private');
    expect(html).toContain('not Public networks');
    expect(html).toContain('Turn off');
    expect(html).toContain('Turn on remote access');
    expect(html).toContain('sign in with your Backbloom password');
    expect(html).not.toContain('type="password"');
    expect(renderToStaticMarkup(<RemoteAccessDialog open={false} busy={false} passwordSet onCancel={noop} onConfirm={noop} />)).toBe('');
  });

  it('asks for a password before turning remote access on when none is set', () => {
    const html = renderToStaticMarkup(<RemoteAccessDialog open busy={false} passwordSet={false} onCancel={noop} onConfirm={noop} />);
    expect(html).toContain('Choose a password for other devices.');
    expect(html.match(/type="password"/g)).toHaveLength(2);
    expect(html).toContain('Repeat the password');
    expect(html).toContain('Use at least 8 characters.');
    // nothing to confirm until the password is valid
    expect(html).toMatch(/<button[^>]*disabled[^>]*>(<[^>]*>)*Set password and turn on/);
  });

  it('shows the address and a QR code once on', () => {
    const html = renderToStaticMarkup(<RemoteAccessAddresses status={{ ...setup.remoteAccess, enabled: true, urls: [MOCK_LAN_URL] }} />);
    expect(html).toContain(MOCK_LAN_URL);
    expect(html).toContain(`aria-label="QR code for ${MOCK_LAN_URL}"`);
    expect(renderToStaticMarkup(<RemoteAccessAddresses status={{ ...setup.remoteAccess, enabled: true }} />)).toContain('no network address');
  });

  it('explains the network search before the first search', () => {
    const html = renderToStaticMarkup(<DiscoveryDialog open onCancel={noop} onConfirm={noop} />);
    expect(html).toContain('Search your network for lights?');
    expect(html).toContain('Nothing is changed on them.');
    expect(html).toContain('Private networks');
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

    expect((await api.serverInfo()).controllers?.[0].state).toBe('off');
    const started = await api.startLighting(true);
    expect([started.firstRunCompleted, started.outputHeld, started.autostart.enabled]).toEqual([true, true, true]);

    backend.quitOtherApp();
    await new Promise((r) => setTimeout(r, 0));
    expect(events.at(-1)?.outputHeld).toBe(false);
    expect((await api.serverInfo()).controllers?.[0].state).toBe('ready');
    expect((await api.acknowledge('discovery')).acknowledged).toEqual(['discovery']);
  });

  it('turns remote access on through the setting once a password is set', async () => {
    const { backend, api } = await connected();
    const events: SetupState[] = [];
    backend.onEvent('setup-update', (e) => events.push(e.data as SetupState));
    const network = (await api.getConfig()).network as Record<string, unknown>;
    expect(network.remoteAccess).toBe(false);
    expect((await api.setupState()).passwordSet).toBe(false);

    await expect(api.setConfig({ network: { ...network, remoteAccess: true } })).rejects.toThrow(REMOTE_ACCESS_NEEDS_PASSWORD);
    await expect(api.setPassword('short')).rejects.toThrow('at least 8 characters');
    await api.setPassword('long-enough-1');
    await new Promise((r) => setTimeout(r, 0));
    expect(events.at(-1)?.passwordSet).toBe(true);
    await api.setConfig({ network: { ...network, remoteAccess: true } });
    await new Promise((r) => setTimeout(r, 0));
    expect(events.at(-1)?.remoteAccess).toMatchObject({ enabled: true, urls: [MOCK_LAN_URL] });
  });

  it('needs the password to start from another device', async () => {
    const { api } = await connected({ requireAuth: true, firstRun: true });
    await expect(api.setupState()).rejects.toThrow('No Authorization');
    await expect(api.startLighting()).rejects.toThrow('No Authorization');
    await api.login(MOCK_PASSWORD);
    await expect(api.startLighting()).resolves.toMatchObject({ firstRunCompleted: true, outputHeld: false });
  });
});

describe('demo modes', () => {
  it('starts a new install with only the test device and nothing imported', async () => {
    const { api } = await connected({ firstRun: true, fresh: true });
    const state = await api.setupState();
    expect([state.firstRunCompleted, state.importedFrom]).toEqual([false, null]);
    const cfg = await api.getConfig();
    expect((cfg.device as { type: string }).type).toBe('file');
    expect(cfg.leds).toHaveLength(1);
  });

  it('has a device that can’t be reached (?mock=offline)', async () => {
    const { api } = await connected({ offline: true, demoLayout: true });
    expect((await api.serverInfo()).controllers?.[0].state).toBe('waitingToRetry');
  });
});
