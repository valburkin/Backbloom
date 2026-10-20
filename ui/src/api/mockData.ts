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

export const MOCK_FIXTURES = {
  config: config as BackendConfig,
  schema: schema as unknown as SettingsSchema,
  serverInfo: serverInfo as ServerInfo,
  sysInfo: sysInfo as SysInfo,
  leds: legacyLeds as unknown[],
  wledConfig: wledTwoOutputs as unknown,
};

export type MockFixtures = typeof MOCK_FIXTURES;
