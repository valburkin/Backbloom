import { describe, expect, it } from 'vitest';
import legacyLeds from '../../layout/__fixtures__/my-layout.json';
import { compileLayout, type LayoutSpec } from '../../layout';
import { HyperApi } from '../hyperhdr';
import { MockBackend, MOCK_PASSWORD } from '../mock';
import { isLayoutSpec, loadLayout, saveBlockers, saveLayout } from '../layoutService';
import { RpcError } from '../types';

async function connected(opts: ConstructorParameters<typeof MockBackend>[0] = {}) {
  const backend = new MockBackend({ latencyMs: 0, ...opts });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new HyperApi(backend) };
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

describe('loadLayout', () => {
  it('falls back to the legacy leds + WLED outputs when no layout is saved', async () => {
    const { api } = await connected();
    const res = await loadLayout(api, { admin: true, serverInfo: await api.serverInfo() });
    expect(res.source).toBe('legacy');
    expect(res.spec.controllers).toHaveLength(1);
    expect(res.spec.controllers[0].outputs.map((o) => o.length)).toEqual([91, 77]);
    // Re-compiling the imported legacy layout reproduces the original rects.
    const leds = compileLayout(res.spec)[0].leds;
    expect(leds).toHaveLength(legacyLeds.length);
    leds.forEach((r, i) => {
      expect(r.hmin).toBeCloseTo(legacyLeds[i].hmin, 2);
      expect(r.vmax).toBeCloseTo(legacyLeds[i].vmax, 2);
    });
  });

  it('prefers a saved layout setting', async () => {
    const saved: LayoutSpec = { version: 1, controllers: [], strips: [] };
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
  it('writes the full config with compiled leds and the layout spec', async () => {
    const { api } = await connected();
    const si = await api.serverInfo();
    const { spec } = await loadLayout(api, { admin: true, serverInfo: si });
    const edited: LayoutSpec = { ...spec, strips: spec.strips.map((s, i) => (i === 0 ? { ...s, name: 'Renamed' } : s)) };
    const before = await api.getConfig();
    const res = await saveLayout(api, edited, si);
    expect(res).toMatchObject({ instance: 0, ledCount: 168, layoutPersisted: true });
    const after = await api.getConfig();
    expect(isLayoutSpec(after.layout)).toBe(true);
    expect((after.layout as LayoutSpec).strips[0].name).toBe('Renamed');
    expect(after.device).toEqual(before.device); // other settings preserved
    expect((after.leds as unknown[]).length).toBe(168);
  });

  it('refuses multi-controller layouts', async () => {
    const { api } = await connected();
    const c = { id: 'a', name: 'A', kind: 'wled' as const, host: 'a', outputs: [] };
    const spec: LayoutSpec = { version: 1, controllers: [c, { ...c, id: 'b' }], strips: [] };
    await expect(saveLayout(api, spec, null)).rejects.toThrow(/single-controller/);
    expect(saveBlockers(spec, 0).map((b) => b.reason)).toEqual(['multiController']);
  });

  it('saveBlockers reports errors and missing controllers', () => {
    expect(saveBlockers({ version: 1, controllers: [], strips: [] }, 2).map((b) => b.reason)).toEqual(['noController', 'errors']);
  });
});
