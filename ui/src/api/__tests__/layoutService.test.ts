import { describe, expect, it } from 'vitest';
import legacyLeds from '../../layout/__fixtures__/my-layout.json';
import { compileLayout, defaultRoom, shapePath, type LayoutSpec } from '../../layout';
import { RpcApi } from '../rpcApi';
import { MockBackend, MOCK_PASSWORD } from '../mock';
import { emptySpecFor, fallbackController, IMPORTED_NOTE, isLayoutSpec, loadLayout, saveBlockers, saveLayout } from '../layoutService';
import { RpcError } from '../types';

async function connected(opts: ConstructorParameters<typeof MockBackend>[0] = {}) {
  const backend = new MockBackend({ latencyMs: 0, ...opts });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new RpcApi(backend) };
}

describe('MockBackend', () => {
  it('requires login for config when auth is on, like the real backend', async () => {
    const { api } = await connected({ requireAuth: true });
    expect(await api.adminRequired()).toBe(true);
    const err = await api.getConfig().catch((e) => e);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.isAuthError).toBe(true);
    await expect(api.login('wrong-password')).rejects.toThrow('No Authorization');
    const token = await api.login(MOCK_PASSWORD);
    expect(token).toBeTruthy();
    expect(await api.adminRequired()).toBe(false);
  });
});

describe('isLayoutSpec', () => {
  it('accepts room layouts and turns down layouts in an older shape', () => {
    expect(isLayoutSpec({ version: 1, room: defaultRoom(), controllers: [], strips: [] })).toBe(true);
    expect(isLayoutSpec({ version: 1, controllers: [], strips: [] })).toBe(false);
    expect(isLayoutSpec({ version: 1, room: defaultRoom(), controllers: [], strips: [{ id: 's', segments: [] }] })).toBe(false);
  });
});

describe('loadLayout', () => {
  it('falls back to the legacy leds + WLED outputs when no layout is saved', async () => {
    const { api } = await connected();
    const res = await loadLayout(api, { admin: true, serverInfo: await api.serverInfo() });
    expect(res.source).toBe('legacy');
    expect(res.spec.controllers).toHaveLength(1);
    expect(res.spec.controllers[0].outputs.map((o) => o.length)).toEqual([91, 77]);
    // the legacy device becomes the controller's driver settings
    expect(res.spec.controllers[0].device).toEqual((await api.getConfig()).device);
    // The imported strips run around the screen of a default room and light every LED (infer.test checks where).
    expect(res.spec.room).toEqual(defaultRoom());
    const leds = compileLayout(res.spec)[0].leds;
    expect(leds).toHaveLength(legacyLeds.length);
    expect(leds.every((r) => !r.disabled)).toBe(true);
  });

  it('prefers a saved layout setting', async () => {
    const saved: LayoutSpec = { version: 1, room: defaultRoom(), controllers: [{ id: 'c', name: 'C', kind: 'wled', host: 'c.local', outputs: [] }], strips: [] };
    const { api } = await connected({ config: { layout: saved } });
    const res = await loadLayout(api, { admin: true, serverInfo: null });
    expect(res.source).toBe('saved');
    expect(res.spec).toEqual(saved);
  });

  it('uses serverinfo leds without admin access', async () => {
    const { api } = await connected({ requireAuth: true });
    const res = await loadLayout(api, { admin: false, serverInfo: await api.serverInfo() });
    expect(res.spec.controllers[0].outputs[0].length).toBe(legacyLeds.length);
    expect(res.note).toMatch(/Sign in/);
  });
});

describe('saveLayout', () => {
  it('writes the compiled leds and the layout spec, keeping other settings', async () => {
    const { api } = await connected();
    const si = await api.serverInfo();
    const { spec } = await loadLayout(api, { admin: true, serverInfo: si });
    const edited: LayoutSpec = { ...spec, strips: spec.strips.map((s, i) => (i === 0 ? { ...s, name: 'Renamed' } : s)) };
    const before = await api.getConfig();
    const res = await saveLayout(api, edited, ['Rainbow swirl', 'Music: quatro for LED strip (MULTI COLOR)']);
    expect(res).toMatchObject({ ledCount: 168, controllers: 1, layoutPersisted: true });
    const after = await api.getConfig();
    expect(isLayoutSpec(after.layout)).toBe(true);
    expect((after.layout as LayoutSpec).strips[0].name).toBe('Renamed');
    expect(after.device).toEqual(before.device); // other settings preserved
    expect((after.leds as unknown[]).length).toBe(168);
    // a layout that never had presets gets the default ones, saved and returned
    expect((after.layout as LayoutSpec).profiles?.map((p) => p.name)).toEqual(['Gaming', 'Movie', 'Music', 'Cozy']);
    expect(res.spec.profiles).toEqual((after.layout as LayoutSpec).profiles);
    // once: a layout with presets (or an empty list) keeps them
    expect((await saveLayout(api, { ...edited, profiles: [] })).spec.profiles).toEqual([]);
    expect((await api.getConfig()).layout).toMatchObject({ profiles: [] });
  });

  it('saves layouts that span several controllers', async () => {
    const { api } = await connected();
    const { spec } = await loadLayout(api, { admin: true, serverInfo: await api.serverInfo() });
    const desk = { id: 'desk', name: 'Desk', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }], device: { type: 'ddp', host: '10.0.0.9' } };
    const multi: LayoutSpec = {
      ...spec,
      controllers: [...spec.controllers, desk],
      strips: [
        ...spec.strips,
        { id: 'desk-strip', name: 'Desk strip', controllerId: 'desk', output: 0, brightness: 0.5, ledCount: 30, path: shapePath(spec.room, 'behind-furniture') },
      ],
    };
    expect(saveBlockers(multi, 0)).toEqual([]);

    expect(await saveLayout(api, multi)).toMatchObject({ ledCount: 198, controllers: 2, layoutPersisted: true });

    const after = await api.getConfig();
    expect((after.leds as unknown[]).length).toBe(198);
    expect((after.layout as LayoutSpec).controllers[1].device).toEqual({ type: 'ddp', host: '10.0.0.9' });
    const info = await api.serverInfo();
    expect(info.controllers?.map((c) => [c.id, c.start, c.ledCount])).toEqual([
      [spec.controllers[0].id, 0, 168],
      ['desk', 168, 30],
    ]);
    await api.blinkLed(4, 'desk');
    await expect(api.blinkLed(30, 'desk')).rejects.toThrow(/no LED 30/);
  });

  it('saveBlockers reports errors and missing controllers', () => {
    expect(saveBlockers({ version: 1, room: defaultRoom(), controllers: [], strips: [] }, 2).map((b) => b.reason)).toEqual(['noController', 'errors']);
  });
});

describe('loadLayout without the device’s outputs', () => {
  it('shows every LED on one output when the WLED controller doesn’t answer, and says so', async () => {
    const device = { type: 'wled', host: 'wled-offline.local' };
    const { api } = await connected({ config: { device } });

    const res = await loadLayout(api, { admin: true, serverInfo: null });

    expect(res.source).toBe('legacy');
    expect(res.spec.controllers[0]).toMatchObject({ kind: 'wled', host: 'wled-offline.local', device, outputs: [{ index: 0, start: 0, length: legacyLeds.length }] });
    expect(res.note).toBe(`Couldn’t read the outputs of wled-offline.local (Failed to fetch), so all ${legacyLeds.length} LEDs are shown as one output. ${IMPORTED_NOTE}`);
  });

  it('takes one output for drivers whose outputs are entered by hand', async () => {
    const device = { type: 'adalight', output: 'COM3' };
    const { api } = await connected({ config: { device } });

    const res = await loadLayout(api, { admin: true, serverInfo: null });

    expect(res.spec.controllers[0]).toMatchObject({ name: 'LED device', kind: 'adalight', host: '', device });
    expect(res.spec.controllers[0].outputs).toEqual([{ index: 0, start: 0, length: legacyLeds.length }]);
    expect(res.note).toBe(IMPORTED_NOTE);
  });
});

describe('emptySpecFor', () => {
  it('puts one strip per output around the screen, along the ceiling, then behind the desk', () => {
    const controller = { ...fallbackController('wled.local', 0), outputs: [0, 1, 2, 3].map((i) => ({ index: i, start: i * 10, length: i === 3 ? 0 : 10 })) };

    const spec = emptySpecFor(controller);

    expect(spec.strips.map((s) => s.name)).toEqual(['Strip 1', 'Strip 2', 'Strip 3', 'Strip 4']);
    expect(spec.strips.map((s) => s.path)).toEqual(
      (['around-screen', 'ceiling-front', 'behind-furniture', 'behind-furniture'] as const).map((id) => shapePath(spec.room, id)),
    );
    // a strip has at least one LED, even on an empty output
    expect(spec.strips.map((s) => s.ledCount)).toEqual([10, 10, 10, 1]);
    expect(isLayoutSpec(spec)).toBe(true);
    expect(emptySpecFor(fallbackController('', 50)).strips.map((s) => s.name)).toEqual(['Main strip']);
  });
});

describe('saveLayout without a controller', () => {
  it('refuses before writing anything', async () => {
    const { api } = await connected();
    const before = await api.getConfig();

    await expect(saveLayout(api, { version: 1, room: defaultRoom(), controllers: [], strips: [] })).rejects.toThrow('The layout has no controller.');
    expect(await api.getConfig()).toEqual(before);
  });
});
