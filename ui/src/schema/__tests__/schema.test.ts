import { describe, expect, it } from 'vitest';
import schemaFixture from '../../api/__fixtures__/schema.json';
import type { SettingsSchema } from '../../api/types';
import { dependenciesMet, enumOptions, fieldLabel, fieldsOf, fieldUnit, humanize, propertyAt, validate, withDefaults } from '../schema';

const schema = schemaFixture as unknown as SettingsSchema;
const drivers = schema.properties.alldevices!;

describe('schema helpers', () => {
  it('orders visible fields by propertyOrder and drops hidden ones', () => {
    const hue = fieldsOf(drivers.philipshue).map((f) => f.key);
    expect(hue).not.toContain('blackLightsTimeout'); // options.hidden
    const wled = fieldsOf(drivers.wled);
    expect(wled.map((f) => f.key)).toEqual(['host', 'brightnessMax', 'brightnessMaxLevel', 'restoreOriginalState', 'maxRetry']);
    expect(wled.find((f) => f.key === 'maxRetry')?.required).toBe(true);
    expect(fieldsOf(drivers.adalight).find((f) => f.key === 'delayAfterConnect')?.tier).toBe('expert');
  });

  it('labels fields in plain language with units and enum titles', () => {
    expect(fieldLabel('host', drivers.wled.properties!.host)).toBe('Address');
    expect(fieldLabel('colorModel', drivers.yeelight.properties!.colorModel)).toBe('Color mode');
    expect(fieldLabel('colorModel', drivers.home_assistant.properties!.lamps.items!.properties!.colorModel)).toBe('Color model');
    expect(enumOptions(drivers.ddp.properties!.white_algorithm).find((o) => o.value === 'hyperserial_custom')?.label).toBe('HyperSerial, custom');
    expect(fieldLabel('ipAddress', drivers.lifx.properties!.lamps.items!.properties!.ipAddress)).toBe('IP address');
    expect(fieldLabel('someKey_x', { title: 'edt_unknown_key_title' })).toBe('Some key x');
    expect(humanize('brightnessMaxLevel')).toBe('Brightness max level');
    expect(fieldUnit(schema.properties.smoothing.properties!.time_ms)).toBe('ms');
    expect(enumOptions(schema.properties.device.properties!.colorOrder)[1]).toEqual({ value: 'bgr', label: 'BGR' });
  });

  it('evaluates dependencies, including one-of lists and truthy checks', () => {
    const smoothing = schema.properties.smoothing.properties!;
    expect(dependenciesMet(smoothing.stiffness, { type: 'HybridRgbInterpolator' })).toBe(true);
    expect(dependenciesMet(smoothing.stiffness, { type: 'Stepper' })).toBe(false);
    expect(dependenciesMet(drivers.wled.properties!.brightnessMaxLevel, { brightnessMax: false })).toBe(false);
    const hue = drivers.philipshue.properties!;
    expect(dependenciesMet(hue.groupId, { useEntertainmentAPI: true, useEntertainmentAPIV2: false })).toBe(true);
    expect(dependenciesMet(hue.groupId, { useEntertainmentAPI: true, useEntertainmentAPIV2: true })).toBe(false);
  });

  it('fills defaults without touching existing values', () => {
    const v = withDefaults(drivers.wled, { host: 'wled.local', maxRetry: 5 }) as Record<string, unknown>;
    expect(v).toMatchObject({ host: 'wled.local', maxRetry: 5, brightnessMax: true, brightnessMaxLevel: 255, restoreOriginalState: false });
    expect(withDefaults(schema.properties.smoothing, {})).toMatchObject({ time_ms: 350, type: 'HybridRgbInterpolator' });
  });

  it('validates ranges, integers, patterns, nested arrays and skips fields hidden by dependencies', () => {
    const smoothing = schema.properties.smoothing;
    const ok = withDefaults(smoothing, {});
    expect(validate(smoothing, ok)).toEqual([]);
    expect(validate(smoothing, { ...(ok as object), time_ms: 99999 })).toEqual([{ path: 'time_ms', message: 'The maximum is 5000.' }]);
    expect(validate(smoothing, { ...(ok as object), time_ms: 100.5 })[0].message).toBe('Use a whole number.');
    // stiffness only matters for the hybrid interpolators
    expect(validate(smoothing, { ...(ok as object), type: 'Stepper', stiffness: -5 })).toEqual([]);
    const lifx = withDefaults(drivers.lifx, { lamps: [{ name: 'Desk', ipAddress: '10.0.0.2', macAddress: 'nope' }] });
    expect(validate(drivers.lifx, lifx)).toEqual([{ path: 'lamps.0.macAddress', message: 'This doesn’t look right.' }]);
    expect(validate(schema.properties.general, withDefaults(schema.properties.general, { name: 'abc' }))[0].path).toBe('name');
    expect(validate({ type: 'string' }, 5)[0].message).toBe('Enter text.');
    expect(validate({ type: 'boolean' }, 'yes')[0].message).toBe('Choose on or off.');
  });

  it('gives every device schema setting a tier: only what is needed to connect is essential', () => {
    const essential = (name: string) => fieldsOf(drivers[name]).filter((f) => f.tier === 'basic').map((f) => f.key);
    expect(essential('wled')).toEqual(['host']);
    expect(essential('udpe131')).toEqual(['host', 'universe']);
    expect(essential('adalight')).toEqual(['output']);
    expect(fieldsOf(drivers.wled).find((f) => f.key === 'maxRetry')?.tier).toBe('expert');
    expect(fieldsOf(drivers.ddp).find((f) => f.key === 'enable_ice_rgbw')?.tier).toBe('advanced');
    expect(fieldsOf(drivers.ddp).find((f) => f.key === 'ice_white_led_intensity')?.tier).toBe('expert');
    expect(fieldsOf(schema.properties.device).find((f) => f.key === 'refreshTime')?.tier).toBe('expert');
    // serial ports are found by themselves, and the log level matches the Support tab's default
    expect([drivers.sp616e.properties!.output.default, drivers.skydimo.properties!.output.default]).toEqual(['auto', 'auto']);
    expect(schema.properties.logger.properties!.level.default).toBe('warn');
  });

  it('finds nested property schemas', () => {
    expect(propertyAt(schema, 'color', 'channelAdjustment', '[]', 'gamma')?.default).toBe(1.5);
    expect(propertyAt(schema, 'nope', 'x')).toBeUndefined();
  });
});
