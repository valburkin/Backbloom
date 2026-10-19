import { useState } from 'react';
import { Lightbulb, Loader2, Plug, ScanSearch } from 'lucide-react';
import { Badge, Button, Dialog, StatusDot } from '../../components/ui';
import { Callout, Section, Select } from '../../components/form';
import { SchemaForm } from '../../components/SchemaForm';
import type { PageSettings } from '../../app/config';
import { useConnection } from '../../app/connection';
import { useAction } from '../../app/useAction';
import type { DiscoveredDevice, JsonSchema } from '../../api/types';
import { hrefFor } from '../../app/router';
import { validate } from '../../schema/schema';
import { discoveryTarget, driverName, driverOptions, switchDriver } from './drivers';

type Obj = Record<string, unknown>;

/** LED driver of the current instance: type, connection settings from the driver's schema, discovery and a test. */
export function DriverCard({ ps }: { ps: PageSettings }) {
  const { serverInfo, api } = useConnection();
  const { run, busy } = useAction();
  const [found, setFound] = useState<DiscoveredDevice[] | null>(null);
  const device = ps.value<Obj>('device');
  const type = String(device.type ?? '');
  const general = ps.schemaOf('device');
  // `alldevices` maps each driver type to its schema.
  const drivers = ps.schemaOf('alldevices') as unknown as Record<string, JsonSchema> | undefined;
  const driverSchema = drivers?.[type];
  const options = driverOptions(serverInfo?.ledDevices?.available);
  const group = options.find((o) => o.value === type)?.group;
  const target = discoveryTarget(type, group);
  const smoothing = ps.value<Obj>('smoothing');
  const sending = serverInfo?.components?.find((c) => c.name === 'LEDDEVICE')?.enabled === true;
  const issues = driverSchema ? validate(driverSchema, device) : [];

  const discover = () =>
    void run('discover', 'Couldn’t search for devices', async () => {
      setFound(await api.discover(type));
    });

  const known = options.some((o) => o.value === type);
  return (
    <Section
      title="LED driver"
      icon={<Plug size={18} />}
      description="How Overglow talks to your LED controller."
      actions={
        <span className="flex items-center gap-2 text-xs text-ink-muted">
          <StatusDot tone={sending ? 'ok' : 'neutral'} />
          {sending ? 'Sending colors' : 'Idle'}
        </span>
      }
    >
      <Select
        label="Controller type"
        value={type}
        options={known ? options : [{ value: type, label: `${driverName(type)} (not available in this version)`, group: 'Other' }, ...options]}
        onChange={(t) => ps.set('device', switchDriver(device, t, { general, driver: drivers?.[t] }, ps.baseline.device ?? undefined))}
        hint={group === 'USB' ? 'Connected with a USB cable.' : group === 'Network' ? 'Connected over Wi-Fi or Ethernet.' : undefined}
      />
      {driverSchema ? (
        <SchemaForm
          schema={driverSchema}
          value={device}
          issues={issues}
          onChange={(v) => ps.set('device', v)}
          overrides={
            type === 'file'
              ? { output: { label: 'File path', hint: String.raw`Where the colors are written. \\.\NUL discards them.` } }
              : target
              ? {
                  [target]: {
                    trailing: (
                      <Button icon={busy === 'discover' ? <Loader2 size={15} className="animate-spin" /> : <ScanSearch size={15} />} disabled={busy === 'discover'} onClick={discover}>
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
          value={device}
          omit={['type']}
          onChange={(v) => ps.set('device', v)}
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
          disabled={busy === 'blink' || ps.dirty}
          title={ps.dirty ? 'Save the driver settings first' : undefined}
          onClick={() => void run('blink', 'Couldn’t blink the LED', () => api.blinkLed(0), 'The first LED blinks red, green, blue')}
        >
          Test: blink the first LED
        </Button>
        {ps.dirty && <Badge tone="warn">Save to apply</Badge>}
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
                    if (target) ps.set('device', { ...device, [target]: d.value });
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
            Nothing found. Check that the controller is powered on{target === 'host' ? ' and on the same network' : ' and plugged in'}, or enter the {target === 'host' ? 'address' : 'port'} yourself.
          </p>
        )}
      </Dialog>
    </Section>
  );
}
