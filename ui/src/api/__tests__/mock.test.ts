import { describe, expect, it } from 'vitest';
import { RpcApi, SUBSCRIPTIONS } from '../rpcApi';
import { MockBackend } from '../mock';
import { RpcError } from '../types';
import { saveErrorText } from '../../app/config';
import { toneMappingStatus } from '../../pages/CapturePage';
import specJson from '../../../../backend/tests/fixtures/layouts/ui-multi-controller.spec.json';

async function connected() {
  const backend = new MockBackend({ latencyMs: 0 });
  const opened = new Promise<void>((r) => backend.onStatus((s) => s === 'open' && r()));
  backend.connect();
  await opened;
  return { backend, api: new RpcApi(backend) };
}

// The mock follows the .NET app's protocol (checked against a running instance), so these double as the UI's
// expectations of the backend.
describe('MockBackend like the .NET app', () => {
  it('rejects invalid settings with per-field errors and stores nothing', async () => {
    const { api } = await connected();
    const smoothing = (await api.getConfig()).smoothing as Record<string, unknown>;
    const err = await api.setConfig({ smoothing: { ...smoothing, time_ms: 99999 } }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(RpcError);
    expect((err as RpcError).reply?.errors).toEqual([{ path: 'smoothing.time_ms', message: 'The maximum is 5000.' }]);
    expect(saveErrorText(err, { 'smoothing.time_ms': 'Transition time' })).toBe('Transition time: The maximum is 5000.');
    expect(((await api.getConfig()).smoothing as Record<string, unknown>).time_ms).toBe(smoothing.time_ms);
  });

  it('runs music effects only while sound capture is on, and ends them when it goes off', async () => {
    const { api } = await connected();
    const sound = { ...((await api.getConfig()).soundEffect as Record<string, unknown>) };
    await api.setConfig({ soundEffect: { ...sound, enable: false } });
    expect((await api.serverInfo()).sound?.available).toBe(false);
    await expect(api.runEffect('Music: spectrum')).rejects.toThrow('Turn on sound capture to use music effects');
    await expect(api.runEffect('Candle')).resolves.toBeUndefined();

    await api.setConfig({ soundEffect: { ...sound, enable: true } });
    await api.runEffect('Music: spectrum');
    expect((await api.serverInfo()).activeEffects?.map((e) => e.name)).toEqual(['Music: spectrum']);
    await api.setConfig({ soundEffect: { ...sound, enable: false } });
    expect((await api.serverInfo()).activeEffects).toEqual([]);
  });

  it('serves one effect preview per LED, in the order of leds', async () => {
    const { api } = await connected();
    const leds = (await api.serverInfo()).leds?.length ?? 0;
    const preview = await api.effectPreviews('Candle');
    expect([...preview.frames.keys()]).toEqual(['Candle']);
    expect(preview.ledCount).toBe(leds || 50);
    expect(preview.frames.get('Candle')!.length % (preview.ledCount * 3)).toBe(0);
  });

  it('activates a profile by hand until set back to automatic, and lists the programs with a window', async () => {
    const { api, backend } = await connected();
    const events: unknown[] = [];
    backend.onEvent('profile-update', (e) => events.push(e.data));
    expect(SUBSCRIPTIONS).toContain('profile-update');
    expect((await api.serverInfo()).profile).toBeNull();
    await expect(api.setProfile('witcher')).rejects.toThrow("The layout has no profile 'witcher'");

    // no program runs in the mock, so only a profile chosen by hand runs
    await api.setConfig({ layout: specJson });
    const witcher = { id: 'witcher', name: 'Witcher 3', manual: true };
    expect(await api.setProfile('witcher')).toEqual(witcher);
    expect((await api.serverInfo()).profile).toEqual(witcher);
    expect(await api.setProfile(null)).toBeNull();
    expect((await api.serverInfo()).profile).toBeNull();
    await new Promise((r) => setTimeout(r, 0));
    expect(events).toEqual([{ profile: witcher }, { profile: null }]);

    expect(await api.runningPrograms()).toEqual([
      { exe: 'vlc.exe', title: 'VLC media player' },
      { exe: 'witcher3.exe', title: 'The Witcher 3' },
    ]);
  });
  it('saves while the lights are off and stores adjustments', async () => {
    const { api } = await connected();
    await api.setComponent('ALL', false);
    const logger = (await api.getConfig()).logger as Record<string, unknown>;
    await expect(api.setConfig({ logger: { ...logger, level: 'warn' } })).resolves.toBeUndefined();
    await api.adjust({ scaleOutput: 0.5 });
    const color = (await api.getConfig()).color as { channelAdjustment: Array<{ scaleOutput: number }> };
    expect(color.channelAdjustment[0].scaleOutput).toBe(0.5);
  });

  it('restores a backup while staying connected', async () => {
    const { backend, api } = await connected();
    const backup = await api.exportSettings();
    await expect(api.importSettings(backup)).resolves.toEqual([]);
    expect(backend.status).toBe('open');
  });

  it('reports the HDR state of the capture and has no layout until one is saved', async () => {
    const { api } = await connected();
    const info = await api.serverInfo(SUBSCRIPTIONS);
    expect(info.systemGrabbers?.hdr).toEqual({ mode: 'auto', capturing: true, monitorHdr: false, toneMapping: false });
    expect(Object.keys(info).length).toBeGreaterThan(5);
    expect('layout' in (await api.getConfig())).toBe(false);
  });
});

describe('toneMappingStatus', () => {
  const hdr = (monitorHdr: boolean, toneMapping: boolean, capturing = true) => ({ mode: 'auto' as const, capturing, monitorHdr, toneMapping });

  it('says in one line what happens to the colors', () => {
    expect(toneMappingStatus(undefined)).toBeNull();
    expect(toneMappingStatus(hdr(false, false, false))?.text).toBe('Waiting for the screen capture to start.');
    expect(toneMappingStatus(hdr(false, false))?.text).toBe('Your monitor is in SDR mode — colors are used as-is.');
    expect(toneMappingStatus(hdr(true, true))?.text).toBe('HDR detected — tone mapping applied.');
    expect(toneMappingStatus(hdr(false, true))?.tone).toBe('warn');
    expect(toneMappingStatus(hdr(true, false))?.tone).toBe('warn');
  });
});
