import { renderToStaticMarkup } from 'react-dom/server';
import { createElement } from 'react';
import { describe, expect, it } from 'vitest';
import schemaFixture from '../../../api/__fixtures__/schema.json';
import serverInfo from '../../../api/__fixtures__/serverinfo.json';
import type { SettingsSchema } from '../../../api/types';
import { SchemaForm } from '../../../components/SchemaForm';
import { withDefaults } from '../../../schema/schema';
import { discoveryTarget, driverOptions, switchDriver } from '../drivers';

const schema = schemaFixture as unknown as SettingsSchema;

describe('drivers', () => {
  it('groups the reported drivers as Network, USB, Debug', () => {
    const opts = driverOptions(serverInfo.ledDevices.available);
    expect([...new Set(opts.map((o) => o.group))]).toEqual(['Network', 'USB', 'Debug']);
    expect(opts.find((o) => o.value === 'synclight')?.group).toBe('USB');
    expect(opts[0].group).toBe('Network');
    expect(discoveryTarget('wled', 'Network')).toBe('host');
    expect(discoveryTarget('adalight', 'USB')).toBe('output');
    expect(discoveryTarget('philipshue', 'Network')).toBeNull();
  });

  it('keeps shared settings when switching drivers and restores the previous driver settings', () => {
    const wled = { type: 'wled', host: 'wled.local', colorOrder: 'grb', refreshTime: 0, maxRetry: 5 };
    const general = schema.properties.device;
    const ada = switchDriver(wled, 'adalight', { general, driver: schema.properties.alldevices!.adalight });
    expect(ada).toMatchObject({ type: 'adalight', colorOrder: 'grb', awa_mode: true });
    expect(ada.host).toBeUndefined();
    expect(switchDriver(ada, 'wled', { general, driver: schema.properties.alldevices!.wled }, wled)).toEqual(wled);
  });

  it('renders a form for every driver schema without errors', () => {
    for (const [name, s] of Object.entries(schema.properties.alldevices!)) {
      const html = renderToStaticMarkup(createElement(SchemaForm, { schema: s, value: withDefaults(s, {}) as Record<string, unknown>, onChange: () => {} }));
      expect(html.length, name).toBeGreaterThan(50);
    }
  });
});
