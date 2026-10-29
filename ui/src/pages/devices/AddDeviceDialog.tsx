import { useState, type FormEvent } from 'react';
import { ArrowRight, CheckCircle2, Loader2, ScanSearch } from 'lucide-react';
import { controllerFromWledConfig, type Controller, type LayoutSpec } from '../../layout';
import { Button, Dialog, Segmented, Stepper } from '../../components/ui';
import { Input, Select } from '../../components/form';
import { useConnection } from '../../app/connection';
import { useEditor } from '../../app/editor';
import { useExpertTools } from '../../app/expert';
import { hrefFor } from '../../app/router';
import type { DiscoveredDevice } from '../../api/types';
import { FoundDevices, newDriverSettings, useDeviceSearch, useDeviceTypes, useDriverSchemas } from './DriverForm';
import { addedAddresses, discoveryTarget, driverName, isLampDevice, isTestDevice, lampCount, ledCountLabel } from './drivers';

export function errorText(e: unknown, host: string): string {
  if (e instanceof DOMException && e.name === 'AbortError') return `${host} didn’t answer in time.`;
  if (e instanceof TypeError) return `Couldn’t reach ${host}. Check that the device is on and connected to the same network as this PC.`;
  return e instanceof Error ? e.message : String(e);
}

export async function fetchController(fetchCfg: (h: string, s?: AbortSignal) => Promise<unknown>, host: string, name?: string): Promise<Controller> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 5000);
  try {
    const cfg = await fetchCfg(host, ctrl.signal);
    return controllerFromWledConfig(host, cfg, name || undefined);
  } finally {
    clearTimeout(timer);
  }
}

function uniqueId(spec: LayoutSpec, wanted: string): string {
  const taken = new Set(spec.controllers.map((c) => c.id));
  const base = wanted || 'controller';
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base}-${n}`)) n++;
  return `${base}-${n}`;
}

/**
 * Adds a device and one strip per output, so the new LEDs show up on the Layout page right away. The first real
 * device replaces the test device of a fresh install (unless expert tools are on: then it may be wanted).
 */
function useAddController() {
  const { state, dispatch } = useEditor();
  const expert = useExpertTools();
  return (controller: Controller) => {
    const replaced = !expert && state.spec.controllers.length > 0 && state.spec.controllers.every(isTestDevice) ? state.spec.controllers : [];
    replaced.forEach((c) => dispatch({ type: 'removeController', controllerId: c.id }));
    const remaining = { ...state.spec, controllers: state.spec.controllers.filter((c) => !replaced.includes(c)) };
    const id = uniqueId(remaining, controller.id);
    dispatch({ type: 'addController', controller: { ...controller, id } });
    controller.outputs.forEach((o, i) =>
      dispatch({
        type: 'addStrip',
        controllerId: id,
        output: o.index,
        name: controller.outputs.length > 1 ? `${controller.name} ${i + 1}` : controller.name,
      }),
    );
  };
}

/** The setting the Add dialog asks for: the address (or USB port) of a device type, if it has one. */
function addressKey(type: string, group: string | undefined, schema: { properties?: Record<string, unknown> } | undefined): string | null {
  if (type === 'file') return null;
  const target = discoveryTarget(type, group);
  if (target) return target;
  return schema?.properties && 'host' in schema.properties ? 'host' : null;
}

type AddMode = 'wled' | 'other';

/**
 * Adds a device to the layout (unsaved until the layout is saved): WLED by its address or a network search, or any
 * other device type. Without `onAdded` it ends with "Device added" and a link to the Layout page; with it, it closes
 * and calls `onAdded` (the first-run setup goes on by itself).
 */
export function AddDeviceDialog({ open, onClose, onAdded }: { open: boolean; onClose: () => void; onAdded?: (name: string) => void }) {
  const { backend } = useConnection();
  const { state } = useEditor();
  const schemas = useDriverSchemas();
  const add = useAddController();
  const search = useDeviceSearch();
  const [mode, setMode] = useState<AddMode>('wled');
  const [host, setHost] = useState('');
  const [name, setName] = useState('');
  const [type, setType] = useState('ddp');
  const [count, setCount] = useState(60);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [found, setFound] = useState<Controller | null>(null);
  const [results, setResults] = useState<DiscoveredDevice[] | null>(null);
  const [added, setAdded] = useState<string | null>(null);
  const options = useDeviceTypes().filter((o) => o.value !== 'wled');
  const searchType = mode === 'wled' ? 'wled' : type;
  const group = mode === 'wled' ? 'Network' : options.find((o) => o.value === type)?.group;
  const usb = group === 'USB';
  const field = mode === 'wled' ? 'host' : addressKey(type, group, schemas.drivers?.[type]);
  const canSearch = !!discoveryTarget(searchType, group);
  const lamps = isLampDevice(type);
  const already = addedAddresses(state.spec);
  const duplicate = !!found && already.has(found.host.toLowerCase());

  const reset = () => {
    setHost('');
    setName('');
    setError(null);
    setFound(null);
    setResults(null);
    setBusy(false);
    setAdded(null);
  };
  const close = () => {
    reset();
    onClose();
  };
  const done = (label: string) => {
    if (onAdded) {
      reset();
      onAdded(label);
    } else setAdded(label);
  };
  const finish = (c: Controller) => {
    add(c);
    done(c.name);
  };

  const lookUp = async (e?: FormEvent) => {
    e?.preventDefault();
    const h = host.trim().replace(/^https?:\/\//i, '').replace(/\/.*$/, '');
    if (!h) return;
    setBusy(true);
    setError(null);
    setFound(null);
    try {
      const controller = await fetchController((x, s) => backend.fetchWledConfig(x, s), h, name.trim());
      setFound({ ...controller, device: newDriverSettings('wled', schemas, { host: h }) });
    } catch (err) {
      setError(errorText(err, h));
    } finally {
      setBusy(false);
    }
  };

  const addOther = () => {
    const label = name.trim() || driverName(type);
    const device = newDriverSettings(type, schemas, field && host.trim() ? { [field]: host.trim() } : {});
    add({
      id: label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || type,
      name: label,
      kind: type,
      host: '',
      outputs: [{ index: 0, start: 0, length: lampCount(device) ?? Math.max(1, Math.floor(count)) }],
      device,
    });
    done(label);
  };

  const pick = (d: DiscoveredDevice) => {
    setHost(d.value);
    setFound(null);
    setResults(null);
  };

  if (added)
    return (
      <Dialog
        open={open}
        title="Device added"
        onClose={close}
        footer={
          <>
            <Button variant="ghost" onClick={close}>
              Done
            </Button>
            <a
              href={hrefFor('layout')}
              onClick={close}
              className="inline-flex h-9 items-center gap-2 rounded-lg bg-accent px-3.5 text-sm font-medium text-accent-ink hover:bg-accent-strong"
            >
              Next: place your strips <ArrowRight size={15} />
            </a>
          </>
        }
      >
        <p className="flex items-start gap-2.5 text-sm">
          <CheckCircle2 size={18} className="mt-0.5 shrink-0 text-ok" />
          <span>
            <span className="font-medium">{added}</span> is in your setup. Next, show Backbloom where its strips are on the Layout page, then save.
            {lamps && mode === 'other' && <span className="mt-1 block text-ink-muted">Add its lamps in its connection settings here on Devices.</span>}
          </span>
        </p>
      </Dialog>
    );

  const searchButton = canSearch && (
    <Button
      className="shrink-0 whitespace-nowrap"
      icon={search.searching(searchType) ? <Loader2 size={15} className="animate-spin" /> : <ScanSearch size={15} />}
      disabled={search.searching(searchType)}
      onClick={() => search.search(searchType, setResults)}
    >
      {usb ? 'Find' : 'Search my network'}
    </Button>
  );

  return (
    <Dialog
      open={open}
      title="Add device"
      onClose={close}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            Cancel
          </Button>
          {mode === 'other' ? (
            <Button variant="primary" disabled={!type || (!lamps && count < 1)} onClick={addOther}>
              Add device
            </Button>
          ) : found ? (
            <Button variant="primary" disabled={duplicate} onClick={() => finish(found)}>
              Add device
            </Button>
          ) : (
            <Button variant="primary" disabled={busy || !host.trim()} onClick={() => void lookUp()}>
              {busy ? 'Connecting…' : 'Connect'}
            </Button>
          )}
        </>
      }
    >
      <div className="space-y-4">
        <Segmented<AddMode>
          label="Type"
          value={mode}
          onChange={(m) => {
            setMode(m);
            setError(null);
            setResults(null);
            setHost('');
          }}
          options={[
            { value: 'wled', label: 'WLED' },
            { value: 'other', label: 'Other' },
          ]}
        />
        {mode === 'other' && (
          <Select
            label="Device type"
            value={type}
            options={options}
            onChange={(t) => {
              setType(t);
              setHost('');
              setResults(null);
            }}
          />
        )}
        <form onSubmit={mode === 'wled' ? lookUp : (e) => e.preventDefault()} className="space-y-4">
          {field && (
            <Input
              label={usb ? 'Port' : 'Address'}
              hint={usb ? 'Leave it on “auto” to find the device by itself.' : mode === 'wled' ? 'For example 192.168.1.40 or wled-desk.local.' : undefined}
              placeholder={usb ? 'auto' : undefined}
              value={host}
              onChange={(v) => {
                setHost(v);
                setFound(null);
              }}
              trailing={searchButton}
            />
          )}
          {results && <FoundDevices found={results} usb={usb} added={already} onPick={pick} />}
          <Input label="Name (optional)" placeholder={mode === 'wled' ? 'For example Desk lights' : `For example ${driverName(type)} behind the TV`} value={name} onChange={setName} />
          <button type="submit" hidden />
        </form>
        {mode === 'other' &&
          (lamps ? (
            <p className="text-[13px] text-ink-muted">You add the lamps in its connection settings after adding it. Each lamp gets one color.</p>
          ) : (
            <Stepper label={ledCountLabel(type)} value={count} min={1} max={10000} onChange={setCount} />
          ))}
        {busy && (
          <p className="flex items-center gap-2 text-[13px] text-ink-muted">
            <Loader2 size={15} className="animate-spin" /> Asking WLED about its outputs…
          </p>
        )}
        {error && (
          <p role="alert" className="text-[13px] text-bad">
            {error}
          </p>
        )}
        {found && (
          <div className="border-t border-line pt-3">
            <p className="text-sm font-medium">{found.name}</p>
            <p className="text-xs text-ink-muted">{found.host}</p>
            {duplicate && <p className="mt-1 text-[13px] text-warn">This device is already added.</p>}
            <ul className="mt-2 divide-y divide-line text-[13px]">
              {found.outputs.map((o) => (
                <li key={o.index} className="flex justify-between py-1.5">
                  <span>
                    Output {o.index + 1}
                    {o.pin !== undefined ? ` · pin ${o.pin}` : ''}
                  </span>
                  <span className="tabular-nums text-ink-muted">{o.length} LEDs</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
      {search.dialog}
    </Dialog>
  );
}
