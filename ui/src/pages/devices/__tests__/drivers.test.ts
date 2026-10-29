import { renderToStaticMarkup } from 'react-dom/server';
import { createElement } from 'react';
import { describe, expect, it } from 'vitest';
import schemaFixture from '../../../api/__fixtures__/schema.json';
import serverInfo from '../../../api/__fixtures__/serverinfo.json';
import type { SettingsSchema } from '../../../api/types';
import { SchemaForm } from '../../../components/SchemaForm';
import { fieldsOf, withDefaults } from '../../../schema/schema';
import { connectionSchema } from '../DriverForm';
import { discoveryTarget, driverOptions, isLampDevice, lampCount, ledCountLabel, switchDriver } from '../drivers';

const schema = schemaFixture as unknown as SettingsSchema;

describe('drivers', () => {
  it('groups the reported device types as Network, USB, Testing', () => {
    const opts = driverOptions(serverInfo.ledDevices.available);
    expect([...new Set(opts.map((o) => o.group))]).toEqual(['Network', 'USB', 'Testing']);
    expect(opts.find((o) => o.value === 'synclight')?.group).toBe('USB');
    expect(opts[0].group).toBe('Network');
    expect(discoveryTarget('wled', 'Network')).toBe('host');
    expect(discoveryTarget('adalight', 'USB')).toBe('output');
    expect(discoveryTarget('philipshue', 'Network')).toBe('output');
    expect(discoveryTarget('home_assistant', 'Network')).toBe('homeAssistantHost');
    expect(discoveryTarget('udpe131', 'Network')).toBeNull();
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

describe('lamp devices', () => {
  it('count one LED per lamp instead of asking for a number', () => {
    expect(lampCount({ type: 'wiz', lamps: [{}, {}, {}] })).toBe(3);
    expect(lampCount({ type: 'yeelight', lights: [{}] })).toBe(1);
    expect(lampCount({ type: 'home_assistant' })).toBe(1);
    expect(lampCount({ type: 'wled' })).toBeNull();
    expect(isLampDevice('philipshue')).toBe(false);
    expect(ledCountLabel('nanoleaf')).toBe('Number of panels');
    expect(ledCountLabel('ddp')).toBe('Number of LEDs');
  });
});

describe('connection settings', () => {
  it('put color order out of the way where WLED orders the colors itself', () => {
    const general = schema.properties.device;
    const wled = connectionSchema('wled', schema.properties.alldevices!.wled, general)!;
    expect(wled.properties!.colorOrder.access).toBe('expert');
    expect(connectionSchema('adalight', schema.properties.alldevices!.adalight, general)!.properties!.colorOrder.access).toBe('advanced');
    // the shared settings come after the device type's own
    expect(fieldsOf(wled).map((f) => f.key).slice(-2)).toEqual(['colorOrder', 'refreshTime']);
  });
});
