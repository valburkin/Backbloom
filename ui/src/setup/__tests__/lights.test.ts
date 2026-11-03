import { describe, expect, it } from 'vitest';
import serverInfo from '../../api/__fixtures__/serverinfo.json';
import { MockHardware } from '../../api/mockHardware';
import type { DiscoveredDevice, HueBridgeState } from '../../api/types';
import { compileLayout, defaultRoom, surfaceSize, validateLayout, type LayoutSpec } from '../../layout';
import {
  BRANDS,
  LIGHT_PLACES,
  NO_AUTH,
  defaultPlace,
  entertainmentAreas,
  homeAssistantLights,
  hueColorLights,
  hueLights,
  identifyParams,
  isAdded,
  knownLightHosts,
  knownLights,
  selectionAfterSearch,
  lightDevices,
  lightsFromSearch,
  nanoleafLight,
  placePath,
  searchableBrands,
  suggestedNames,
  type LightAuth,
  type PlacedLight,
} from '../lights';

const hw = new MockHardware();
const found = (type: string) => hw.discover(type) as unknown as DiscoveredDevice[];
const settings = (type: string, extra: Record<string, unknown>) => ({ type, ...extra });
const USER = 'mock-hue-user-0123456789';
/** The bridge as the UI reads it: three API v2 resources. */
function hueOf(h: MockHardware): HueBridgeState {
  const get = (r: string) => (h.properties('philipshue', { host: '192.168.1.20', user: USER, filter: `/clip/v2/resource/${r}` }) as { data: unknown[] }).data;
  return { configurations: get('entertainment_configuration'), lights: get('light'), services: get('entertainment') } as HueBridgeState;
}

describe('room-light brands', () => {
  it('searches only the brands the backend has a device type for', () => {
    const fixture = searchableBrands(serverInfo.ledDevices.available);
    expect(fixture).not.toContain('govee');
    expect(fixture).toEqual(['philipshue', 'nanoleaf', 'lifx', 'wiz', 'yeelight', 'home_assistant']);
    expect(searchableBrands([...serverInfo.ledDevices.available, { name: 'govee', group: 'leds_group_2_network' }])).toContain('govee');
    expect(BRANDS.find((b) => b.type === 'govee')?.missing).toMatch(/LAN Control/);
  });
});

describe('lights a search found', () => {
  it('turns each driver’s answers into lamp entries for its lamp list', () => {
    const lifx = lightsFromSearch('lifx', found('lifx'));
    expect(lifx[0]).toMatchObject({ brand: 'lifx', host: '192.168.1.41', lamp: { ipAddress: '192.168.1.41', macAddress: 'D0:73:D5:11:22:33', name: 'Lamp #0' } });
    expect(lightsFromSearch('yeelight', found('yeelight'))[0]).toMatchObject({ host: '192.168.1.48', lamp: { host: '192.168.1.48', port: 55443 } });
    expect(lightsFromSearch('govee', found('govee'))[0]).toMatchObject({ host: '192.168.1.52', lamp: { ipAddress: '192.168.1.52', sku: 'H6199' } });
    expect(lightsFromSearch('philipshue', found('philipshue'))).toEqual([]);
  });

  it('names lights the search named itself after their brand', () => {
    const lights = [...lightsFromSearch('lifx', found('lifx')), ...lightsFromSearch('govee', found('govee')), ...lightsFromSearch('yeelight', found('yeelight'))];
    expect([...suggestedNames(lights).values()]).toEqual(['LIFX 1', 'LIFX 2', 'Govee H6199', 'Yeelight 1']);
  });
});

describe('Hue', () => {
  const bridge = hueOf(hw);

  it('lists the entertainment areas with a light per channel, in the order the bridge streams them, and the lights that can stream', () => {
    expect(entertainmentAreas(bridge)).toEqual([
      {
        id: '9f2c5d1e-0000-4000-8000-000000000001',
        name: 'TV area',
        channels: [
          { channel: 0, lightId: 'light-3', v1: '3', name: 'TV right' },
          { channel: 1, lightId: 'light-1', v1: '1', name: 'TV left' },
        ],
      },
    ]);
    expect(hueColorLights(bridge).map((l) => l.name)).toEqual(['Play bar', 'TV left', 'TV right']);
    expect(hueLights('192.168.1.20', entertainmentAreas(bridge)[0]).map((l) => [l.name, l.id])).toEqual([
      ['TV right', '3'],
      ['TV left', '1'],
    ]);
  });

  it('numbers the channels of a light with several (a gradient strip)', () => {
    const area = { id: 'a', name: 'A', channels: [0, 1, 2].map((channel) => ({ channel, lightId: 'l', v1: '7', name: 'Gradient' })) };
    expect(hueLights('h', area).map((l) => l.name)).toEqual(['Gradient 1', 'Gradient 2', 'Gradient 3']);
  });

  it('pairs on the second try (after the button) and creates an area', () => {
    const fresh = new MockHardware();
    expect(fresh.properties('philipshue', { host: '192.168.1.20', pair: true })).toEqual({ error: 'link button not pressed' });
    expect(fresh.properties('philipshue', { host: '192.168.1.20', pair: true })).toMatchObject({ username: 'mock-hue-user-0123456789' });
    // v2 light ids or v1 numbers, as the backend takes them; a light that can't stream is refused
    expect(fresh.properties('philipshue', { host: '192.168.1.20', user: USER, createArea: { name: 'B', lights: ['light-2'] } })).toMatchObject({ error: expect.stringMatching(/no entertainment light/) });
    const created = fresh.properties('philipshue', { host: '192.168.1.20', user: USER, createArea: { name: 'Backbloom', lights: ['light-4', '1'] } }) as { id: string };
    const area = entertainmentAreas(hueOf(fresh)).find((a) => a.id === created.id);
    expect(area?.name).toBe('Backbloom');
    expect(area?.channels.map((c) => c.name)).toEqual(['Play bar', 'TV left']);
  });
});

describe('identify', () => {
  const auth: LightAuth = {
    hue: { '192.168.1.20': { username: 'u', clientkey: 'k', areaId: '2' } },
    nanoleaf: { '192.168.1.31:16021': { token: 't' } },
    homeAssistant: { 'ha:8123': { token: 'h' } },
  };
  it('gives each driver its own parameters', () => {
    const [lifx] = lightsFromSearch('lifx', found('lifx'));
    expect(identifyParams(lifx, auth)).toEqual({ ipAddress: '192.168.1.41', macAddress: 'D0:73:D5:11:22:33', name: 'Lamp #0' });
    expect(identifyParams(lightsFromSearch('yeelight', found('yeelight'))[0], auth)).toEqual({ hostname: '192.168.1.48', port: 55443 });
    expect(identifyParams({ key: 'h', brand: 'philipshue', name: 'x', host: '192.168.1.20', id: '3' }, auth)).toEqual({ host: '192.168.1.20', user: 'u', lightId: 3 });
    expect(identifyParams(nanoleafLight('192.168.1.31:16021', {}), auth)).toEqual({ host: '192.168.1.31:16021', token: 't' });
    expect(identifyParams(homeAssistantLights('ha:8123', [{ id: 'light.desk', name: 'Desk' }])[0], auth)).toEqual({ host: 'ha:8123', token: 'h', entityId: 'light.desk' });
    expect(identifyParams(lightsFromSearch('govee', found('govee'))[0], auth)).toEqual({ ipAddress: '192.168.1.52' });
  });
});

describe('lights in the layout', () => {
  const room = defaultRoom();

  it('puts each place on its surface, lights at the same place side by side', () => {
    for (const { value } of LIGHT_PLACES) {
      const path = placePath(room, value);
      expect(path, value).toHaveLength(2);
      for (const p of path) {
        const { w, h } = surfaceSize(room, p.surface);
        expect(p.x).toBeGreaterThanOrEqual(0);
        expect(p.x).toBeLessThanOrEqual(w);
        expect(p.y).toBeGreaterThanOrEqual(0);
        expect(p.y).toBeLessThanOrEqual(h);
      }
    }
    expect(placePath(room, 'left')[0].x).toBeLessThan(room.screen.x);
    expect(placePath(room, 'ceiling')[0].surface).toBe('ceiling');
    expect(placePath(room, 'left', 1)[0].x).toBeLessThan(placePath(room, 'left', 0)[0].x);
    expect(defaultPlace(0)).toBe('left');
    expect(defaultPlace(1)).toBe('right');
  });

  it('makes one device per brand (per bridge and server), one output and strip per light', () => {
    const lifx = lightsFromSearch('lifx', found('lifx'));
    const hue = hueLights('192.168.1.20', entertainmentAreas(hueOf(hw))[0]);
    const panels = nanoleafLight('192.168.1.31:16021', { name: 'Shapes', panelLayout: { layout: { numPanels: 9 } } });
    const placed: PlacedLight[] = [...lifx, ...hue, panels].map((l, i) => ({ ...l, label: `Light ${i + 1}`, place: defaultPlace(i) }));
    const auth: LightAuth = {
      ...NO_AUTH,
      hue: { '192.168.1.20': { username: 'u', clientkey: 'k', areaId: '9f2c5d1e-0000-4000-8000-000000000001' } },
      nanoleaf: { '192.168.1.31:16021': { token: 't' } },
    };
    const devices = lightDevices(placed, auth, room, settings);
    expect(devices.map((d) => [d.controller.name, d.controller.outputs.length, d.strips.map((s) => s.name)])).toEqual([
      ['LIFX', 2, ['Light 1', 'Light 2']],
      ['Philips Hue', 2, ['Light 3', 'Light 4']],
      ['Light 5', 1, ['Light 5']],
    ]);
    const [lifxDevice, hueDevice, nano] = devices;
    expect(lifxDevice.controller.device).toMatchObject({ type: 'lifx', lamps: [{ ipAddress: '192.168.1.41', name: 'Light 1' }, { ipAddress: '192.168.1.42', name: 'Light 2' }] });
    expect(hueDevice.controller.device).toMatchObject({ type: 'philipshue', output: '192.168.1.20', username: 'u', clientkey: 'k', useEntertainmentAPI: true, useEntertainmentAPIV2: true, entertainmentConfigurationId: '9f2c5d1e-0000-4000-8000-000000000001' });
    expect(nano.controller.outputs).toEqual([{ index: 0, start: 0, length: 9 }]);
    expect(nano.controller.device).toMatchObject({ type: 'nanoleaf', host: '192.168.1.31:16021', token: 't' });

    const spec: LayoutSpec = {
      version: 1,
      room,
      controllers: devices.map((d) => d.controller),
      strips: devices.flatMap((d, i) =>
        d.strips.map((s, j) => ({ id: `s-${i}-${j}`, name: s.name, controllerId: d.controller.id, output: s.output, ledCount: d.controller.outputs[s.output].length, path: s.path })),
      ),
    };
    expect(validateLayout(spec).filter((x) => x.severity === 'error')).toEqual([]);
    expect(compileLayout(spec).map((c) => c.leds.length)).toEqual([2, 2, 9]);
  });

  it('knows the lamps in the layout already, by their addresses', () => {
    const [device] = lightDevices(
      lightsFromSearch('lifx', found('lifx')).map((l) => ({ ...l, label: l.name, place: 'left' as const })),
      NO_AUTH,
      room,
      settings,
    );
    const known = knownLightHosts([device.controller], new Set(['192.168.1.20']));
    expect([...known].sort()).toEqual(['192.168.1.20', '192.168.1.41', '192.168.1.42']);
  });

  it('keeps what was picked when searching again, and never picks what is added already', () => {
    const lifx = lightsFromSearch('lifx', found('lifx'));
    const nano = nanoleafLight('192.168.1.31:16021', {});
    const none = knownLights([], new Set());
    // first search: everything that answered by itself
    const first = selectionAfterSearch(new Set(), new Set(), lifx, [], none);
    expect([...first]).toEqual(lifx.map((l) => l.key));
    // the user unpicks one LIFX lamp and pairs the Nanoleaf; searching again keeps both choices
    const picked = new Set([lifx[0].key, nano.key]);
    const again = selectionAfterSearch(picked, new Set(lifx.map((l) => l.key)), lifx, [nano], none);
    expect([...again].sort()).toEqual([lifx[0].key, nano.key].sort());
    // a paired Nanoleaf stays picked when a search misses it (paired devices are known by address); a LIFX lamp that
    // didn't answer this time is dropped
    expect([...selectionAfterSearch(picked, new Set(), [], [nano], none)]).toEqual([nano.key]);
    // lights in the layout already are never picked
    const known = knownLights([], new Set(['192.168.1.41', '192.168.1.31:16021']));
    expect([...selectionAfterSearch(picked, new Set(), lifx, [nano], known)]).toEqual([lifx[1].key]);
  });

  it('knows Home Assistant lights by entity, so a known server can add new ones', () => {
    const lights = homeAssistantLights('192.168.1.30:8123', [
      { id: 'light.desk_lamp', name: 'Desk lamp' },
      { id: 'light.shelf', name: 'Shelf' },
    ]);
    const [device] = lightDevices([{ ...lights[0], label: 'Desk', place: 'left' }], { ...NO_AUTH, homeAssistant: { '192.168.1.30:8123': { token: 't' } } }, room, settings);
    const known = knownLights([device.controller], new Set(['192.168.1.30:8123']));
    expect(isAdded(lights[0], known)).toBe(true);
    expect(isAdded(lights[1], known)).toBe(false);
    // other brands by address: a Hue bridge in the layout marks its lights
    expect(isAdded({ key: 'h', brand: 'philipshue', name: 'TV', host: '192.168.1.20', id: '1' }, knownLights([], new Set(['192.168.1.20'])))).toBe(true);
  });

  it('can’t blink a Hue channel whose light the bridge didn’t name', () => {
    expect(identifyParams({ key: 'h', brand: 'philipshue', name: 'Light 1', host: '192.168.1.20', id: '' }, NO_AUTH)).toBeNull();
  });

  it('lists Home Assistant lights by entity id, with its server and token', () => {
    const lights = homeAssistantLights('192.168.1.30:8123', [
      { id: 'light.desk_lamp', name: 'Desk lamp' },
      { id: 'light.shelf', name: 'Shelf' },
    ]);
    const auth: LightAuth = { ...NO_AUTH, homeAssistant: { '192.168.1.30:8123': { token: 'secret' } } };
    const [device] = lightDevices(lights.map((l) => ({ ...l, label: l.name, place: 'behind' as const })), auth, room, settings);
    expect(device.controller.device).toEqual({
      type: 'home_assistant',
      homeAssistantHost: '192.168.1.30:8123',
      longLivedAccessToken: 'secret',
      lamps: [
        { name: 'light.desk_lamp', colorModel: 0 },
        { name: 'light.shelf', colorModel: 0 },
      ],
    });
  });
});
