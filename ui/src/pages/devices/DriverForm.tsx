import { useState, type ReactNode } from 'react';
import { Lightbulb, Loader2, ScanSearch } from 'lucide-react';
import { Badge, Button, cx, Dialog, TILE } from '../../components/ui';
import { Callout, Select } from '../../components/form';
import { SchemaForm } from '../../components/SchemaForm';
import { useConnection } from '../../app/connection';
import { useConfig } from '../../app/config';
import { useAction } from '../../app/useAction';
import { useExpertTools } from '../../app/expert';
import type { DiscoveredDevice, JsonSchema } from '../../api/types';
import type { DriverSettings } from '../../layout';
import { hrefFor } from '../../app/router';
import { validate, withDefaults } from '../../schema/schema';
import { discoversOnNetwork, discoveryTarget, driverName, driverOptions, switchDriver, type DriverOption } from './drivers';

/** Shown once before the first search on the network: what the search sends and that it opens nothing. */
export function DiscoveryDialog({ open, onCancel, onConfirm }: { open: boolean; onCancel(): void; onConfirm(): void }) {
  return (
    <Dialog
      open={open}
      title="Search your network for lights?"
      onClose={onCancel}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="primary" icon={<ScanSearch size={15} />} onClick={onConfirm}>
            Search
          </Button>
        </>
      }
    >
      <p className="text-sm text-ink-muted">
        Backbloom asks the devices on your home network once whether they’re lights it can use (just like your phone looking for a printer). Nothing
        is changed on them. Windows usually won’t ask for permission. If it does, allow <span className="font-medium text-ink">Private networks</span> only.
      </p>
    </Dialog>
  );
}

/**
 * Searching for devices of a type: on the network (explained once first, see DiscoveryDialog) or on this PC's USB
 * ports. Render `dialog` once; `search(type, done)` calls `done` with what answered.
 */
export function useDeviceSearch() {
  const { api, setup, applySetup, auth } = useConnection();
  const { run, busy } = useAction();
  const [pending, setPending] = useState<{ type: string; done: (found: DiscoveredDevice[]) => void } | null>(null);
  const go = (type: string, done: (found: DiscoveredDevice[]) => void) =>
    void run(`discover-${type}`, 'Couldn’t search for devices', async () => done(await api.discover(type)));
  const search = (type: string, done: (found: DiscoveredDevice[]) => void) => {
    if (discoversOnNetwork(type) && setup && !setup.acknowledged.includes('discovery')) setPending({ type, done });
    else go(type, done);
  };
  const dialog = (
    <DiscoveryDialog
      open={pending !== null}
      onCancel={() => setPending(null)}
      onConfirm={() => {
        const p = pending;
        setPending(null);
        if (auth === 'admin') void api.acknowledge('discovery').then(applySetup, () => undefined);
        if (p) go(p.type, p.done);
      }}
    />
  );
  return { search, searching: (type: string) => busy === `discover-${type}`, dialog };
}

/** Devices a search found, to pick one from (fills in the address or port); those in `added` can't be picked again. */
export function FoundDevices({ found, usb, added, onPick }: { found: DiscoveredDevice[]; usb: boolean; added?: ReadonlySet<string>; onPick(d: DiscoveredDevice): void }) {
  if (!found.length)
    return (
      <p className="text-[13px] text-ink-muted">
        Nothing found. Check that the device is on{usb ? ' and plugged in' : ' and connected to the same network as this PC'}, or enter the{' '}
        {usb ? 'port' : 'address'} yourself.
      </p>
    );
  return (
    <ul className="space-y-1.5">
      {found.map((d) => {
        const already = added?.has(d.value.toLowerCase()) ?? false;
        return (
          <li key={d.value}>
            <button
              type="button"
              disabled={already}
              className={cx('flex w-full items-center gap-3 rounded-xl px-3.5 py-2.5 text-left text-[13px] disabled:cursor-not-allowed disabled:text-ink-muted', TILE)}
              onClick={() => onPick(d)}
            >
              <span className="min-w-0 flex-1">
                {d.name}
                {d.name !== d.value && <span className="block font-mono text-xs text-ink-faint">{d.value}</span>}
              </span>
              {already && <Badge>Already added</Badge>}
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/** The general `device` schema and every driver's schema (`alldevices`), once the settings schema is loaded. */
export function useDriverSchemas(): { general?: JsonSchema; drivers?: Record<string, JsonSchema> } {
  const { schema } = useConfig();
  return {
    general: schema?.properties.device,
    drivers: schema?.properties.alldevices as Record<string, JsonSchema> | undefined,
  };
}

/** Driver settings for a new device of `type`: the driver's defaults plus `extra` (e.g. the host). */
export function newDriverSettings(type: string, schemas: { general?: JsonSchema; drivers?: Record<string, JsonSchema> }, extra: Record<string, unknown> = {}): DriverSettings {
  const general = withDefaults(schemas.general, {}) as Record<string, unknown>;
  return { ...switchDriver({ ...general, type }, type, { general: schemas.general, driver: schemas.drivers?.[type] }), ...extra, type };
}

/**
 * One form for a device's connection settings: the device type's own settings, then the shared ones (color order,
 * resend interval) at the end. WLED and DDP put the colors in order themselves, so color order is an expert setting
 * there.
 */
export function connectionSchema(type: string, driver: JsonSchema | undefined, general: JsonSchema | undefined): JsonSchema | undefined {
  const shared = Object.fromEntries(
    Object.entries(general?.properties ?? {})
      .filter(([k]) => k !== 'type')
      .map(([k, s]): [string, JsonSchema] => [
        k,
        { ...s, propertyOrder: 100 + (s.propertyOrder ?? 0), ...(k === 'colorOrder' && (type === 'wled' || type === 'ddp') ? { access: 'expert' } : {}) },
      ]),
  );
  if (!driver && !general) return undefined;
  return { ...(driver ?? { type: 'object' }), properties: { ...(driver?.properties ?? {}), ...shared } };
}

/** Device type picker options: the test device only with expert tools on (or while it is the chosen one). */
export function useDeviceTypes(current?: string): DriverOption[] {
  const { serverInfo } = useConnection();
  const expert = useExpertTools();
  return driverOptions(serverInfo?.ledDevices?.available).filter((o) => o.value !== 'file' || expert || o.value === current);
}

/**
 * Connection settings of one device: device type, its settings from the driver's schema, search and a test blink.
 * Edits go into the layout (saved with it); `saved` is the device's settings as last saved.
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
  const { api } = useConnection();
  const { config } = useConfig();
  const { run, busy } = useAction();
  const devices = useDeviceSearch();
  const [found, setFound] = useState<DiscoveredDevice[] | null>(null);
  const { general, drivers } = useDriverSchemas();
  const type = String(device.type ?? '');
  const driverSchema = drivers?.[type];
  const schema = connectionSchema(type, driverSchema, general);
  const shown = withDefaults(schema, device) as DriverSettings;
  const options = useDeviceTypes(type);
  const group = options.find((o) => o.value === type)?.group;
  const target = discoveryTarget(type, group);
  const smoothing = (config?.smoothing ?? {}) as { enable?: boolean; updateFrequency?: number };
  const issues = schema ? validate(schema, shown) : [];
  const key = (k: string) => `${controllerId}-${k}`;
  const searching = devices.searching(type);

  const overrides: Record<string, { label?: string; hint?: ReactNode; trailing?: ReactNode }> = {};
  if (type === 'file') overrides.output = { label: 'File path', hint: String.raw`Where the colors are written. \\.\NUL discards them.` };
  else if (target)
    overrides[target] = {
      trailing: (
        <Button
          icon={searching ? <Loader2 size={15} className="animate-spin" /> : <ScanSearch size={15} />}
          disabled={searching}
          onClick={() => devices.search(type, setFound)}
        >
          {group === 'USB' ? 'Find' : 'Search'}
        </Button>
      ),
    };
  if (smoothing.enable === true)
    overrides.refreshTime = {
      hint: (
        <>
          While transitions are on, colors are sent {String(smoothing.updateFrequency ?? 50)} times a second (LED updates, on the{' '}
          <a className="text-accent-strong hover:underline" href={hrefFor('colors')}>
            Colors
          </a>{' '}
          page), so this value has no effect.
        </>
      ),
    };

  const known = options.some((o) => o.value === type);
  return (
    <div className="space-y-5">
      <Select
        label="Device type"
        value={type}
        options={known ? options : [{ value: type, label: `${driverName(type)} (not available in this version)`, group: 'Other' }, ...options]}
        onChange={(t) => onChange(switchDriver(shown, t, { general, driver: drivers?.[t] }, saved) as DriverSettings)}
        hint={group === 'USB' ? 'Connected with a USB cable.' : group === 'Network' ? 'Connected over Wi-Fi or Ethernet.' : undefined}
      />
      {!driverSchema && <Callout tone="warn">This device type can’t be set up in this version of Backbloom.</Callout>}
      {schema && <SchemaForm schema={schema} value={shown} issues={issues} omit={['type']} onChange={(v) => onChange(v as DriverSettings)} overrides={overrides} />}
      <div className="flex flex-wrap items-center gap-2">
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
      {devices.dialog}
      <Dialog open={found !== null} title={`${driverName(type)} devices found`} onClose={() => setFound(null)}>
        <FoundDevices
          found={found ?? []}
          usb={group === 'USB'}
          onPick={(d) => {
            if (target) onChange({ ...shown, [target]: d.value });
            setFound(null);
          }}
        />
      </Dialog>
    </div>
  );
}
