import { useState } from 'react';
import { Lightbulb, Loader2, ScanSearch } from 'lucide-react';
import { Badge, Button, Dialog } from '../../components/ui';
import { Callout, Select } from '../../components/form';
import { SchemaForm } from '../../components/SchemaForm';
import { useConnection } from '../../app/connection';
import { useConfig } from '../../app/config';
import { useAction } from '../../app/useAction';
import type { DiscoveredDevice, JsonSchema } from '../../api/types';
import type { DriverSettings } from '../../layout';
import { hrefFor } from '../../app/router';
import { validate, withDefaults } from '../../schema/schema';
import { discoveryTarget, driverName, driverOptions, switchDriver } from './drivers';

/** The general `device` schema and every driver's schema (`alldevices`), once the settings schema is loaded. */
export function useDriverSchemas(): { general?: JsonSchema; drivers?: Record<string, JsonSchema> } {
  const { schema } = useConfig();
  return {
    general: schema?.properties.device,
    drivers: schema?.properties.alldevices as Record<string, JsonSchema> | undefined,
  };
}

/** Driver settings for a new controller of `type`: the driver's defaults plus `extra` (e.g. the host). */
export function newDriverSettings(type: string, schemas: { general?: JsonSchema; drivers?: Record<string, JsonSchema> }, extra: Record<string, unknown> = {}): DriverSettings {
  const general = withDefaults(schemas.general, {}) as Record<string, unknown>;
  return { ...switchDriver({ ...general, type }, type, { general: schemas.general, driver: schemas.drivers?.[type] }), ...extra, type };
}

/**
 * LED driver of one controller: type, connection settings from the driver's schema, discovery and a test blink.
 * Edits go into the layout (saved with it); `saved` is the controller's driver settings as last saved.
 */
export function DriverForm({
  controllerId,
  device,
  saved,
  dirty,
  onChange,
}: {
  controllerId: string;
  device: DriverSettings;
  saved?: DriverSettings;
  /** The layout has unsaved changes: the test blink would use the old settings. */
  dirty: boolean;
  onChange: (device: DriverSettings) => void;
}) {
  const { serverInfo, api } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const [found, setFound] = useState<DiscoveredDevice[] | null>(null);
  const { general, drivers } = useDriverSchemas();
  const type = String(device.type ?? '');
  const driverSchema = drivers?.[type];
  const shown = withDefaults(driverSchema, withDefaults(general, device)) as DriverSettings;
  const options = driverOptions(serverInfo?.ledDevices?.available);
  const group = options.find((o) => o.value === type)?.group;
  const target = discoveryTarget(type, group);
  const smoothing = (config?.smoothing ?? {}) as { enable?: boolean; updateFrequency?: number };
  const issues = driverSchema ? validate(driverSchema, shown) : [];
  const key = (k: string) => `${controllerId}-${k}`;

  const discover = () =>
    void run(key('discover'), 'Couldn’t search for devices', async () => {
      setFound(await api.discover(type));
    });

  const known = options.some((o) => o.value === type);
  return (
    <div className="space-y-4">
      <Select
        label="Driver"
        value={type}
        options={known ? options : [{ value: type, label: `${driverName(type)} (not available in this version)`, group: 'Other' }, ...options]}
        onChange={(t) => onChange(switchDriver(shown, t, { general, driver: drivers?.[t] }, saved) as DriverSettings)}
        hint={group === 'USB' ? 'Connected with a USB cable.' : group === 'Network' ? 'Connected over Wi-Fi or Ethernet.' : undefined}
      />
      {driverSchema ? (
        <SchemaForm
          schema={driverSchema}
          value={shown}
          issues={issues}
          onChange={(v) => onChange(v as DriverSettings)}
          overrides={
            type === 'file'
              ? { output: { label: 'File path', hint: String.raw`Where the colors are written. \\.\NUL discards them.` } }
              : target
                ? {
                    [target]: {
                      trailing: (
                        <Button icon={busy === key('discover') ? <Loader2 size={15} className="animate-spin" /> : <ScanSearch size={15} />} disabled={busy === key('discover')} onClick={discover}>
                          Find
                        </Button>
                      ),
                    },
                  }
                : {}
          }
        />
      ) : (
        <Callout tone="warn">This driver has no settings description in this version of the service.</Callout>
      )}
      {general && (
        <SchemaForm
          schema={general}
          value={shown}
          omit={['type']}
          onChange={(v) => onChange(v as DriverSettings)}
          overrides={
            smoothing.enable === true
              ? {
                  refreshTime: {
                    hint: (
                      <>
                        While smoothing is on, colors are sent {String(smoothing.updateFrequency ?? 50)} times a second (set under{' '}
                        <a className="text-accent-strong hover:underline" href={hrefFor('colors')}>
                          Colors → Smoothness
                        </a>
                        ), so this value has no effect.
                      </>
                    ),
                  },
                }
              : {}
          }
        />
      )}
      <div className="flex flex-wrap items-center gap-2 border-t border-line pt-4">
        <Button
          size="sm"
          icon={<Lightbulb size={15} />}
          disabled={busy === key('blink') || dirty}
          title={dirty ? 'Save first: the test uses the saved settings' : undefined}
          onClick={() => void run(key('blink'), 'Couldn’t blink the LED', () => api.blinkLed(0, controllerId), 'The first LED blinks red, green, blue')}
        >
          Test: blink the first LED
        </Button>
        {dirty && <Badge tone="warn">Save to apply</Badge>}
      </div>
      <Dialog open={found !== null} title={`Devices found for ${driverName(type)}`} onClose={() => setFound(null)}>
        {found?.length ? (
          <ul className="space-y-1.5">
            {found.map((d) => (
              <li key={d.value}>
                <button
                  type="button"
                  className="w-full rounded-lg border border-line bg-surface-2 px-3 py-2.5 text-left text-[13px] hover:border-accent"
                  onClick={() => {
                    if (target) onChange({ ...shown, [target]: d.value });
                    setFound(null);
                  }}
                >
                  {d.name}
                  <span className="block font-mono text-xs text-ink-faint">{d.value}</span>
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-[13px] text-ink-muted">
            Nothing found. Check that the controller is powered on{group === 'USB' ? ' and plugged in' : ' and on the same network'}, or enter the {group === 'USB' ? 'port' : 'address'} yourself.
          </p>
        )}
      </Dialog>
    </div>
  );
}
