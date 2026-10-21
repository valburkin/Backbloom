// Fixtures for the mock backend: replies of the .NET app with the product owner's settings imported (serverinfo,
// sysinfo, getschema, getconfig), with personal data (host names, devices, ids) replaced.
// Loaded on demand so the production bundle doesn't carry them.
import legacyLeds from '../layout/__fixtures__/my-layout.json';
import wledTwoOutputs from '../layout/__fixtures__/wled-two-outputs.json';
import config from './__fixtures__/config.json';
import schema from './__fixtures__/schema.json';
import serverInfo from './__fixtures__/serverinfo.json';
import sysInfo from './__fixtures__/sysinfo.json';
import type { BackendConfig, ServerInfo, SettingsSchema, SysInfo } from './types';
import { ceilingFront, controllerFromWledConfig, defaultRoom, type DriverSettings, type LayoutSpec } from '../layout';

export const MOCK_FIXTURES = {
  config: config as BackendConfig,
  schema: schema as unknown as SettingsSchema,
  serverInfo: serverInfo as ServerInfo,
  sysInfo: sysInfo as SysInfo,
  leds: legacyLeds as unknown[],
  wledConfig: wledTwoOutputs as unknown,
};

export type MockFixtures = typeof MOCK_FIXTURES;

/**
 * The product owner's setup placed in the room: one strip runs along the wall behind the desk from the right, up the
 * monitor stand, around the monitor and back down to the left; the other runs across the ceiling, right to left.
 */
export function demoLayout(host: string): LayoutSpec {
  const room = defaultRoom();
  const controller = { ...controllerFromWledConfig(host, wledTwoOutputs), device: config.device as DriverSettings };
  const at = (x: number, y: number, led?: number) => ({ surface: 'front' as const, x, y, ...(led !== undefined ? { at: led } : {}) });
  return {
    version: 1,
    room,
    controllers: [controller],
    strips: [
      {
        id: 'monitor',
        name: 'Desk and monitor',
        controllerId: controller.id,
        output: 0,
        ledCount: 91,
        path: [
          at(2.67, 1.82),
          at(2, 1.82, 18),
          at(2, 1.65, 23),
          at(2.3, 1.65, 32),
          at(2.3, 1.31, 39),
          at(1.7, 1.31, 57),
          at(1.7, 1.65, 64),
          at(2, 1.65, 73),
          at(2, 1.82, 78),
          at(1.33, 1.82),
        ],
        depth: 0.15,
      },
      { id: 'ceiling', name: 'Ceiling', controllerId: controller.id, output: 1, ledCount: 77, path: ceilingFront(room), depth: 0.25, blend: 5 },
    ],
  };
}
