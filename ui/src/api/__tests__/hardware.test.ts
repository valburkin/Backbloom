import { describe, expect, it } from 'vitest';
import { RpcApi } from '../rpcApi';
import { MockBackend, type MockOptions } from '../mock';
import { MOCK_BOARD_HOST, MOCK_BOARD_PORT, MOCK_EXISTING_HOST, MOCK_HOTSPOT_HOST } from '../mockHardware';
import { waitForBoard } from '../../setup/wled';

async function connected(options: MockOptions = {}) {
  const backend = new MockBackend({ latencyMs: 0, restartMs: 40, applyMs: 0, ...options });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new RpcApi(backend) };
}

/** A `hardware` subcommand the UI doesn't wrap (the protocol's USB and access point calls), straight to the backend. */
async function hw<T = Record<string, unknown>>(api: RpcApi, subcommand: string, params?: Record<string, unknown>): Promise<T> {
  return ((await api.backend.call('hardware', subcommand, params)) as { info?: unknown }).info as T;
}
const usbScan = (api: RpcApi) => hw<{ boards: Array<Record<string, unknown> & { port: string }>; silent: unknown[] }>(api, 'usb-scan');
const usbWifi = (api: RpcApi, port: string, ssid: string, password: string) => hw(api, 'usb-wifi', { port, ssid, password });

// The mock speaks the `hardware` protocol of docs/guided-setup.md, so these double as the UI's expectations of it.
describe('hardware protocol (mock)', () => {
  it('reports the monitors with their sizes', async () => {
    const { api } = await connected();
    const screens = await api.screens();
    expect(screens[0]).toEqual({ name: 'DELL U2723QE', primary: true, widthMm: 597, heightMm: 336 });
    expect(screens[1].widthMm).toBeNull();
  });

  it('puts a new board on Wi-Fi over USB, refusing a wrong password', async () => {
    const { api } = await connected();
    const [board] = (await usbScan(api)).boards;
    expect(board).toMatchObject({ port: MOCK_BOARD_PORT, state: 'ready', firmware: 'WLED', address: null });
    const networks = (await hw<{ networks: unknown[] }>(api, 'usb-networks', { port: board.port })).networks;
    expect(networks[0]).toEqual({ ssid: 'Home Wi-Fi', signal: 92, secure: true });
    await expect(usbWifi(api, board.port, 'Home Wi-Fi', 'wrongpass')).rejects.toThrow('couldn’t join Home Wi-Fi: check the password');
    await expect(usbWifi(api, board.port, 'Garage', '')).rejects.toThrow(/too far/);
    // not on Wi-Fi yet: nothing answers at the address
    await expect(api.wledRead(MOCK_BOARD_HOST)).rejects.toThrow(/Couldn’t reach/);
    expect(await usbWifi(api, board.port, 'Home Wi-Fi', 'correct horse')).toEqual({ address: `http://${MOCK_BOARD_HOST}`, host: MOCK_BOARD_HOST });
    expect((await usbScan(api)).boards[0]).toMatchObject({ state: 'provisioned', address: `http://${MOCK_BOARD_HOST}` });
    expect(await api.wledRead(MOCK_BOARD_HOST)).toMatchObject({ name: 'WLED', ledCount: 30, wifi: { ssid: 'Home Wi-Fi' } });
    // and a network search finds it now
    expect((await api.discover('wled')).map((d) => d.value)).toContain(MOCK_BOARD_HOST);
  });

  it('writes the strip, restarts when the pin changes, and lights test patterns', async () => {
    let t = 0;
    const { api, backend } = await connected({ restartMs: 4000, now: () => t });
    await usbWifi(api, MOCK_BOARD_PORT, 'Home Wi-Fi', 'correct horse');
    const output = { index: 0, strip: 'ws2812b', pin: 16, length: 67, start: 0, colorOrder: 'GRB' as const, reverse: false };
    expect(await api.wledWrite(MOCK_BOARD_HOST, { outputs: [output], maxCurrent: 5100, ledCurrent: 60 })).toBe(false);
    expect(await api.wledWrite(MOCK_BOARD_HOST, { outputs: [{ ...output, pin: 17 }] })).toBe(true);
    // restarting: no answer until the board's clock passed the restart
    await expect(api.wledRead(MOCK_BOARD_HOST)).rejects.toThrow();
    t += 3999;
    await expect(api.wledRead(MOCK_BOARD_HOST)).rejects.toThrow();
    t += 1;
    const back = await api.wledRead(MOCK_BOARD_HOST);
    expect(back).toMatchObject({ ledCount: 67, maxCurrent: 5100, ledCurrent: 60, outputs: [{ pin: 17 }] });
    await api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'range', count: 67 });
    await api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'off' });
    expect(backend.hardware.tests).toEqual([
      { output: 0, pattern: 'range', count: 67 },
      { output: 0, pattern: 'off' },
    ]);
    await expect(api.wledTest(MOCK_BOARD_HOST, { output: 3, pattern: 'first' })).rejects.toThrow('The board has no output 4.');
    // a route's corners: the output dim, these LEDs bright (from 1, at most 64)
    await api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'points', leds: [1, 30, 67] });
    await expect(api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'points', leds: [] })).rejects.toThrow('1 to 64');
    await expect(api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'points', leds: [68] })).rejects.toThrow("leds must be 1 to 67");
    // like the backend: only LEDs the output has
    await expect(api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'range', count: 68 })).rejects.toThrow("count must be 1 to 67 (the output's LEDs)");
  });

  it('answers a write without a restart only once the board applied it', async () => {
    const { api, backend } = await connected({ applyMs: 30 });
    await usbWifi(api, MOCK_BOARD_PORT, 'Home Wi-Fi', 'correct horse');
    const output = { index: 0, strip: 'ws2812b', pin: 16, length: 30, start: 0, colorOrder: 'GRB' as const, reverse: false };
    const order: string[] = [];
    // a check lit after the write (as the setup's serial queue does it) lands after the board applied it
    const write = api.wledWrite(MOCK_BOARD_HOST, { outputs: [{ ...output, length: 117 }] }).then(() => order.push('applied'));
    await write.then(() => api.wledTest(MOCK_BOARD_HOST, { output: 0, pattern: 'range', count: 77 })).then(() => order.push('lit'));
    expect(order).toEqual(['applied', 'lit']);
    expect(backend.hardware.tests).toEqual([{ output: 0, pattern: 'range', count: 77 }]);
  });

  it('joins Wi-Fi from its own access point when there is no USB', async () => {
    let t = 0;
    const { api } = await connected({ usb: false, now: () => t });
    expect(await usbScan(api)).toEqual({ boards: [], silent: [] });
    expect((await hw<{ restarting: boolean }>(api, 'wled-wifi', { host: '4.3.2.1', ssid: 'Home Wi-Fi', password: 'correct horse' })).restarting).toBe(true);
    // the wait polls until the restart is over (the clock steps with each pause)
    const read = await waitForBoard(() => api.wledRead(MOCK_HOTSPOT_HOST), { sleep: async (ms) => void (t += ms), now: () => t, intervalMs: 1000, timeoutMs: 30000 });
    expect(read.wifi.ssid).toBe('Home Wi-Fi');
  });

  it('is for signed-in sessions only', async () => {
    const { api } = await connected({ requireAuth: true });
    await expect(usbScan(api)).rejects.toThrow('No Authorization');
  });
});

describe('finding boards (mock)', () => {
  it('finds new boards on USB and by their hotspot, and the owner’s board on the network', async () => {
    const { api } = await connected({ demoLayout: true, stepMs: 0 });
    const r = await api.findBoards();
    expect(r.wifi).toEqual({ adapter: true, currentSsid: 'Home Wi-Fi', blocked: null, savedPassword: true });
    expect(r.boards.map((b) => [b.via, b.isNew])).toEqual([
      ['usb', true],
      ['hotspot', true],
      ['network', false],
    ]);
    expect(r.boards[2]).toMatchObject({ name: 'Desk', host: MOCK_EXISTING_HOST, ledCount: 168, setUp: true });
  });

  it('puts a hotspot board on the Wi-Fi with progress on the way, then finds it on the network', async () => {
    const { api, backend } = await connected({ usb: false, stepMs: 0 });
    const steps: string[] = [];
    backend.onEvent('hardware-progress-update', (e) => steps.push((e.data as { step: string }).step));
    const [board] = (await api.findBoards()).boards;
    expect(board).toMatchObject({ via: 'hotspot', hotspot: 'WLED-AP', isNew: true });
    await expect(api.connectWifi(board.id, 'Home Wi-Fi', 'wrongpass')).rejects.toThrow(/didn’t show up on Home Wi-Fi: check the Wi-Fi password/);
    expect(await api.connectWifi(board.id, 'Home Wi-Fi', 'correct horse')).toMatchObject({ host: MOCK_HOTSPOT_HOST, name: 'WLED' });
    await new Promise((r) => setTimeout(r, 0));
    expect(steps.slice(-4)).toEqual(['joining-hotspot', 'sending', 'rejoining', 'waiting']);
    // on the network now: still WLED's first-start settings (new), but no Wi-Fi step any more
    expect((await api.findBoards()).boards).toEqual([expect.objectContaining({ via: 'network', host: MOCK_HOTSPOT_HOST, isNew: true, setUp: false })]);
  });

  it('tells boards apart, uses the saved password and flashes the board that joined', async () => {
    const { api, backend } = await connected({ hotspot: false, stepMs: 0 });
    const r = await api.findBoards();
    expect(r.wifi.savedPassword).toBe(true);
    expect(r.boards[0]).toMatchObject({ via: 'usb', portName: 'Silicon Labs CP210x (COM5)', chip: 'ESP32', firmware: 'WLED' });
    const joined = await api.connectWifi(r.boards[0].id, 'Home Wi-Fi', { useSavedPassword: true });
    await api.wledIdentify(joined.host);
    expect(backend.hardware.identified).toContainEqual({ type: 'wled', params: { host: joined.host } });
    expect((await api.findBoards()).boards[0]).toMatchObject({ via: 'network', mdnsName: 'wled-c3d2e1', port: MOCK_BOARD_PORT });
  });

  it('finds the strips: temporary outputs on likely pins, kept or put back', async () => {
    const { api, backend } = await connected({ hotspot: false, stepMs: 0 });
    const [b] = (await api.findBoards()).boards;
    const { host } = await api.connectWifi(b.id, 'Home Wi-Fi', 'correct horse');
    // the reference board: strips on GPIO 16 and 2
    expect(backend.hardware.wiredAt(host).map((w) => w.pin)).toEqual([16, 2]);
    const candidates = await api.wledProbeStart(host, 1);
    expect(candidates.map((c) => c.pin)).toEqual([16, 2, 17, 4, 5, 18]);
    // each long enough for a strip: its first LEDs lit, the rest black (no leftover colors)
    expect((await api.wledRead(host)).outputs.map((o) => [o.pin, o.length])).toContainEqual([2, 300]);
    await api.wledTest(host, { output: 1, pattern: 'color', color: [255, 255, 255] });
    // not kept: every LED black first, then the board's outputs from before
    await api.wledProbeEnd(host, false);
    expect(backend.hardware.tests.at(-1)).toEqual({ pattern: 'black' });
    expect((await api.wledRead(host)).outputs.map((o) => o.pin)).toEqual([16]);
    expect((await api.wledProbeStart(host, 2))[0].pin).toBe(19);
  });

  it('keeps the strips found: the write turns the search’s lights off, ending it keeps the write', async () => {
    const { api, backend } = await connected({ hotspot: false, stepMs: 0, restartMs: 0 });
    const [b] = (await api.findBoards()).boards;
    const { host } = await api.connectWifi(b.id, 'Home Wi-Fi', 'correct horse');
    await api.wledProbeStart(host, 1);
    const tests = backend.hardware.tests.length;
    await api.wledWrite(host, { outputs: [{ strip: 'ws2812b', pin: 16, length: 60 }, { strip: 'ws2812b', pin: 2, length: 60 }] });
    // shorter outputs and fewer of them: every LED black first
    expect(backend.hardware.tests.slice(tests)).toEqual([{ pattern: 'black' }]);
    await api.wledProbeEnd(host, true);
    expect(backend.hardware.tests.slice(tests)).toEqual([{ pattern: 'black' }]);
    expect((await api.wledRead(host)).outputs.map((o) => [o.pin, o.length])).toEqual([
      [16, 60],
      [2, 60],
    ]);
  });

  it('sees no hotspots while Windows hides nearby networks', async () => {
    const { api } = await connected({ usb: false, locationBlocked: true, stepMs: 0 });
    const r = await api.findBoards();
    expect(r.boards).toEqual([]);
    expect(r.wifi.blocked).toBe('location');
  });

  it('refuses tests on a board a running device streams to', async () => {
    const { api } = await connected({ demoLayout: true, stepMs: 0 });
    await expect(api.wledTest(MOCK_EXISTING_HOST, { output: 0, pattern: 'first' })).rejects.toThrow(/Backbloom is sending colors to this board/);
  });
});

describe('room-light pairing (mock)', () => {
  it('pairs Hue after the button, creates an area, and pairs Nanoleaf', async () => {
    const { api } = await connected();
    await expect(api.huePair('192.168.1.20')).rejects.toThrow('link button not pressed');
    const { username, clientkey } = await api.huePair('192.168.1.20');
    expect(username).toBeTruthy();
    expect(clientkey).toHaveLength(32);
    const id = await api.hueCreateArea('192.168.1.20', username, 'Backbloom', ['light-1', 'light-4']);
    const bridge = await api.hueBridge('192.168.1.20', username);
    expect(bridge.configurations.map((c) => c.id)).toContain(id);
    expect(bridge.lights).toHaveLength(4);
    expect(bridge.services.filter((s) => s.renderer)).toHaveLength(3);
    expect(await api.nanoleafPair('192.168.1.31:16021')).toBe('mock-nanoleaf-token');
  });

  it('lists Home Assistant’s lights, refusing a wrong token', async () => {
    const { api } = await connected();
    await expect(api.homeAssistantLights('192.168.1.30:8123', 'wrong')).rejects.toThrow(/refused the token/);
    expect((await api.homeAssistantLights('192.168.1.30:8123', 'good')).map((l) => l.id)).toEqual(['light.desk_lamp', 'light.floor_lamp', 'light.shelf']);
  });

  it('finds room lights per brand, Govee included, and remembers what blinked', async () => {
    const { api, backend } = await connected();
    expect((await api.serverInfo()).ledDevices?.available.some((d) => d.name === 'govee')).toBe(true);
    expect((await api.discover('govee'))[0]).toMatchObject({ value: '192.168.1.52', sku: 'H6199' });
    expect(await api.discover('lifx')).toHaveLength(2);
    await api.identifyLight('wiz', { ipAddress: '192.168.1.45', name: 'Bulb #0' });
    expect(backend.hardware.identified).toEqual([{ type: 'wiz', params: { ipAddress: '192.168.1.45', name: 'Bulb #0' } }]);
  });
});
