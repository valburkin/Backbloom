import { useState, type FormEvent } from 'react';
import { ChevronDown, ChevronRight, Cpu, Loader2, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { controllerFromWledConfig, stripLedCount, type Controller, type LayoutSpec } from '../layout';
import { Badge, Button, Card, Dialog, EmptyState, IconButton, Segmented, Stepper, StatusDot, TextField } from '../components/ui';
import { PageFrame } from '../components/PageFrame';
import { Select, Switch } from '../components/form';
import { SaveBar } from '../components/SaveBar';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { stripColor } from '../editor/colors';
import type { ControllerInfo } from '../api/types';
import { DriverForm, newDriverSettings, useDriverSchemas } from './devices/DriverForm';
import { driverName, driverOptions } from './devices/drivers';

function errorText(e: unknown, host: string): string {
  if (e instanceof DOMException && e.name === 'AbortError') return `${host} didn’t answer in time.`;
  if (e instanceof TypeError) return `Couldn’t reach ${host}. Check that the controller is powered on and on the same network.`;
  return e instanceof Error ? e.message : String(e);
}

async function fetchController(fetchCfg: (h: string, s?: AbortSignal) => Promise<unknown>, host: string, name?: string): Promise<Controller> {
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

/** Adds a controller and one strip per output, so the new LEDs show up on the Layout page right away. */
function useAddController() {
  const { state, dispatch } = useEditor();
  return (controller: Controller) => {
    const id = uniqueId(state.spec, controller.id);
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

type AddMode = 'wled' | 'other';

function AddControllerDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { backend, serverInfo } = useConnection();
  const schemas = useDriverSchemas();
  const add = useAddController();
  const [mode, setMode] = useState<AddMode>('wled');
  const [host, setHost] = useState('');
  const [name, setName] = useState('');
  const [type, setType] = useState('ddp');
  const [count, setCount] = useState(60);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [found, setFound] = useState<Controller | null>(null);
  const options = driverOptions(serverInfo?.ledDevices?.available).filter((o) => o.value !== 'wled');

  const reset = () => {
    setHost('');
    setName('');
    setError(null);
    setFound(null);
    setBusy(false);
  };
  const close = () => {
    reset();
    onClose();
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
    add({
      id: label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || type,
      name: label,
      kind: type,
      host: '',
      outputs: [{ index: 0, start: 0, length: Math.max(1, Math.floor(count)) }],
      device: newDriverSettings(type, schemas),
    });
    close();
  };

  return (
    <Dialog
      open={open}
      title="Add an LED controller"
      onClose={close}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            Cancel
          </Button>
          {mode === 'other' ? (
            <Button variant="primary" disabled={!type || count < 1} onClick={addOther}>
              Add controller
            </Button>
          ) : found ? (
            <Button
              variant="primary"
              onClick={() => {
                add(found);
                close();
              }}
            >
              Add controller
            </Button>
          ) : (
            <Button variant="primary" disabled={busy || !host.trim()} onClick={() => void lookUp()}>
              {busy ? 'Looking…' : 'Find controller'}
            </Button>
          )}
        </>
      }
    >
      <div className="mb-4">
        <Segmented<AddMode>
          label="Kind of controller"
          size="sm"
          value={mode}
          onChange={(m) => {
            setMode(m);
            setError(null);
          }}
          options={[
            { value: 'wled', label: 'WLED' },
            { value: 'other', label: 'Other driver' },
          ]}
        />
      </div>
      {mode === 'wled' ? (
        <>
          <form onSubmit={lookUp} className="space-y-4">
            <TextField label="Hostname or IP address" placeholder="e.g. wled-living-room.local or 192.168.1.40" value={host} onChange={(v) => { setHost(v); setFound(null); }} autoFocus />
            <TextField label="Name (optional)" placeholder="e.g. Desk lights" value={name} onChange={setName} />
            <button type="submit" hidden />
          </form>
          {busy && (
            <p className="mt-4 flex items-center gap-2 text-[13px] text-ink-muted">
              <Loader2 size={15} className="animate-spin" /> Asking the controller about its outputs…
            </p>
          )}
          {error && (
            <p role="alert" className="mt-4 rounded-lg border border-bad/30 bg-bad/10 p-3 text-[13px] text-bad">
              {error}
            </p>
          )}
          {found && (
            <div className="mt-4 rounded-xl border border-line bg-surface-2 p-4">
              <p className="text-sm font-medium">{found.name}</p>
              <p className="text-xs text-ink-muted">{found.host}</p>
              <ul className="mt-3 space-y-1 text-[13px]">
                {found.outputs.map((o) => (
                  <li key={o.index} className="flex justify-between">
                    <span>Output {o.index + 1}{o.pin !== undefined ? ` · pin ${o.pin}` : ''}</span>
                    <span className="tabular-nums text-ink-muted">{o.length} LEDs</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </>
      ) : (
        <div className="space-y-4">
          <Select label="Driver" value={type} options={options} onChange={setType} />
          <TextField label="Name (optional)" placeholder={`e.g. ${driverName(type)} behind the TV`} value={name} onChange={setName} />
          <Stepper label="LEDs on the controller" value={count} min={1} max={10000} onChange={setCount} />
          <p className="text-[13px] text-ink-muted">You can enter the address and other driver settings after adding it.</p>
        </div>
      )}
    </Dialog>
  );
}

const STATE_TEXT: Record<ControllerInfo['state'], { text: string; tone: 'ok' | 'warn' | 'bad' | 'neutral' }> = {
  ready: { text: 'Sending colors', tone: 'ok' },
  opening: { text: 'Connecting…', tone: 'warn' },
  waitingToRetry: { text: 'Can’t reach it, retrying', tone: 'warn' },
  failed: { text: 'Gave up connecting', tone: 'bad' },
  closing: { text: 'Switching off', tone: 'neutral' },
  off: { text: 'Idle', tone: 'neutral' },
};

function ControllerStatus({ info }: { info: ControllerInfo | undefined }) {
  if (!info) return <span className="text-xs text-ink-faint">Not saved yet</span>;
  const s = info.running ? STATE_TEXT[info.state] : { text: info.status || 'Not running', tone: 'neutral' as const };
  return (
    <span className="flex items-center gap-2 text-xs text-ink-muted" title={info.status ?? undefined}>
      <StatusDot tone={s.tone} />
      {s.text}
    </span>
  );
}

function ControllerCard({ controller, index }: { controller: Controller; index: number }) {
  const { state, dispatch, dirty } = useEditor();
  const { backend, auth, serverInfo } = useConnection();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [driverOpen, setDriverOpen] = useState(index === 0);
  const total = controller.outputs.reduce((n, o) => n + o.length, 0);
  const wled = controller.kind === 'wled';
  const info = serverInfo?.controllers?.find((c) => c.id === controller.id);
  const saved = state.saved?.controllers.find((c) => c.id === controller.id)?.device;
  const update = (patch: Partial<Omit<Controller, 'id'>>, coalesce?: string) =>
    dispatch({ type: 'updateController', controllerId: controller.id, patch, coalesce });

  const refresh = async () => {
    setBusy(true);
    setError(null);
    try {
      const fresh = await fetchController((x, s) => backend.fetchWledConfig(x, s), controller.host, controller.name);
      update({ outputs: fresh.outputs });
    } catch (e) {
      setError(errorText(e, controller.host));
    } finally {
      setBusy(false);
    }
  };

  const type = controller.device?.type ?? (wled ? 'wled' : controller.kind);
  return (
    <Card className="p-5">
      <div className="flex items-start gap-4">
        <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-surface-3 text-accent-strong">
          <Cpu size={20} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <input
              aria-label="Controller name"
              className="min-w-0 rounded-md bg-transparent px-1 -mx-1 text-base font-semibold hover:bg-surface-2 focus:bg-surface-2"
              value={controller.name}
              onChange={(e) => update({ name: e.target.value }, `ctrl-name-${controller.id}`)}
            />
            <Badge tone="accent">{driverName(type)}</Badge>
          </div>
          <p className="flex flex-wrap items-center gap-x-3 text-[13px] text-ink-muted">
            <span>
              {wled ? controller.host || 'Address unknown — sign in to see it' : `Controller ${index + 1}`} · {total} LEDs
            </span>
            <ControllerStatus info={info} />
          </p>
        </div>
        <div className="flex items-center gap-1">
          <Switch
            label={`Send colors to ${controller.name}`}
            checked={controller.enabled !== false}
            onChange={(on) => update({ enabled: on ? undefined : false })}
          />
          {wled && (
            <IconButton label="Read outputs from the controller again" disabled={busy || !controller.host} onClick={() => void refresh()}>
              <RefreshCw size={16} className={busy ? 'animate-spin' : ''} />
            </IconButton>
          )}
          <IconButton
            label="Remove controller"
            onClick={() => {
              const n = state.spec.strips.filter((s) => s.controllerId === controller.id).length;
              if (!n || window.confirm(`Remove ${controller.name} and its ${n} strip${n === 1 ? '' : 's'} from the layout?`))
                dispatch({ type: 'removeController', controllerId: controller.id });
            }}
          >
            <Trash2 size={16} />
          </IconButton>
        </div>
      </div>
      {error && <p className="mt-3 text-[13px] text-bad">{error}</p>}

      <div className="mt-4 overflow-hidden rounded-xl border border-line">
        <table className="w-full text-[13px]">
          <thead className="bg-surface-2 text-left text-xs text-ink-faint">
            <tr>
              <th className="px-3 py-2 font-medium">Output</th>
              <th className="px-3 py-2 font-medium">LEDs</th>
              <th className="px-3 py-2 font-medium">Strip</th>
            </tr>
          </thead>
          <tbody>
            {controller.outputs.map((o) => {
              const strips = state.spec.strips
                .map((s, i) => ({ s, i }))
                .filter(({ s }) => s.controllerId === controller.id && s.output === o.index);
              return (
                <tr key={o.index} className="border-t border-line">
                  <td className="px-3 py-2.5">
                    Output {o.index + 1}
                    {o.pin !== undefined && <span className="text-ink-faint"> · pin {o.pin}</span>}
                  </td>
                  <td className="px-3 py-2.5 tabular-nums">
                    {wled || controller.outputs.length > 1 ? (
                      <>
                        {o.length} <span className="text-ink-faint">(#{o.start}–{o.start + o.length - 1})</span>
                      </>
                    ) : (
                      <input
                        type="number"
                        aria-label={`LEDs on output ${o.index + 1} of ${controller.name}`}
                        className="h-7 w-20 rounded-md border border-line bg-bg px-2 text-[13px] tabular-nums"
                        min={1}
                        max={10000}
                        value={o.length}
                        onChange={(e) => {
                          const length = Math.max(1, Math.min(10000, Math.floor(Number(e.target.value) || 1)));
                          update({ outputs: [{ ...o, length }] }, `ctrl-length-${controller.id}`);
                        }}
                      />
                    )}
                  </td>
                  <td className="px-3 py-2.5">
                    {strips.length === 0 ? (
                      <span className="text-ink-faint">Not used</span>
                    ) : (
                      strips.map(({ s, i }) => {
                        const n = stripLedCount(s);
                        return (
                          <span key={s.id} className="mr-3 inline-flex items-center gap-1.5">
                            <span className="h-2 w-2 rounded-full" style={{ background: stripColor(i) }} aria-hidden />
                            {s.name}
                            {n !== o.length && (
                              <Badge tone="warn">
                                {n} / {o.length}
                              </Badge>
                            )}
                          </span>
                        );
                      })
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {auth === 'admin' && controller.device ? (
        <div className="mt-4 rounded-xl border border-line">
          <button
            type="button"
            aria-expanded={driverOpen}
            className="flex w-full items-center gap-2 px-4 py-3 text-left text-[13px] font-medium"
            onClick={() => setDriverOpen((o) => !o)}
          >
            {driverOpen ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
            LED driver settings
            <span className="font-normal text-ink-faint">— how Backbloom talks to this controller</span>
          </button>
          {driverOpen && (
            <div className="border-t border-line p-4">
              <DriverForm
                controllerId={controller.id}
                device={controller.device}
                saved={saved}
                dirty={dirty}
                onChange={(device) => update({ device }, `ctrl-device-${controller.id}`)}
              />
            </div>
          )}
        </div>
      ) : null}
    </Card>
  );
}

export function DevicesPage() {
  const { state } = useEditor();
  const [adding, setAdding] = useState(false);
  const controllers = state.spec.controllers;
  const total = controllers.reduce((n, c) => n + c.outputs.reduce((m, o) => m + o.length, 0), 0);
  return (
    <PageFrame
      title="Devices"
      subtitle="Your LED controllers: how Backbloom reaches each one, and the outputs your strips are wired to. One picture feeds them all."
      actions={<SaveBar />}
    >
      <div className="flex items-end justify-between gap-3">
        <div>
          <h2 className="text-[15px] font-semibold">Controllers</h2>
          <p className="text-[13px] text-ink-muted">
            {controllers.length > 1
              ? `${controllers.length} controllers with ${total} LEDs, all updated together.`
              : 'The data outputs of your controller and the strips wired to them.'}{' '}
            Saved with the layout (top right).
          </p>
        </div>
        <Button icon={<Plus size={16} />} onClick={() => setAdding(true)}>
          Add controller
        </Button>
      </div>
      {controllers.length === 0 ? (
        <Card>
          <EmptyState icon={<Cpu size={28} />} title="No controllers yet">
            Add the controller your LED strips are connected to: a WLED device, or any other supported driver.
          </EmptyState>
        </Card>
      ) : (
        controllers.map((c, i) => <ControllerCard key={c.id} controller={c} index={i} />)
      )}
      <AddControllerDialog open={adding} onClose={() => setAdding(false)} />
    </PageFrame>
  );
}
