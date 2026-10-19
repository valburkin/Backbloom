import { useState, type FormEvent } from 'react';
import { Cpu, Loader2, Plus, RefreshCw, Trash2, TriangleAlert } from 'lucide-react';
import { controllerFromWledConfig, stripLedCount, type Controller } from '../layout';
import { Badge, Button, Card, Dialog, EmptyState, IconButton, TextField } from '../components/ui';
import { PageFrame } from '../components/PageFrame';
import { Callout } from '../components/form';
import { usePageSettings } from '../app/config';
import { DriverCard } from './devices/DriverCard';
import { SaveBar } from '../components/SaveBar';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { stripColor } from '../editor/colors';

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

function AddControllerDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { backend } = useConnection();
  const { dispatch } = useEditor();
  const [host, setHost] = useState('');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [found, setFound] = useState<Controller | null>(null);

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
      setFound(await fetchController((x, s) => backend.fetchWledConfig(x, s), h, name.trim()));
    } catch (err) {
      setError(errorText(err, h));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={open}
      title="Add a WLED controller"
      onClose={close}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            Cancel
          </Button>
          {found ? (
            <Button
              variant="primary"
              onClick={() => {
                dispatch({ type: 'addController', controller: found });
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
    </Dialog>
  );
}

function ControllerCard({ controller }: { controller: Controller }) {
  const { state, dispatch } = useEditor();
  const { backend } = useConnection();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const total = controller.outputs.reduce((n, o) => n + o.length, 0);

  const refresh = async () => {
    setBusy(true);
    setError(null);
    try {
      const fresh = await fetchController((x, s) => backend.fetchWledConfig(x, s), controller.host, controller.name);
      dispatch({ type: 'updateController', controllerId: controller.id, patch: { outputs: fresh.outputs } });
    } catch (e) {
      setError(errorText(e, controller.host));
    } finally {
      setBusy(false);
    }
  };

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
              onChange={(e) => dispatch({ type: 'updateController', controllerId: controller.id, patch: { name: e.target.value }, coalesce: `ctrl-name-${controller.id}` })}
            />
            <Badge tone="accent">{controller.kind.toUpperCase()}</Badge>
          </div>
          <p className="text-[13px] text-ink-muted">{controller.host || 'Address unknown — sign in to see it'} · {total} LEDs</p>
        </div>
        <div className="flex items-center gap-1">
          <IconButton label="Read outputs from the controller again" disabled={busy || !controller.host} onClick={() => void refresh()}>
            <RefreshCw size={16} className={busy ? 'animate-spin' : ''} />
          </IconButton>
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
                    {o.length} <span className="text-ink-faint">(#{o.start}–{o.start + o.length - 1})</span>
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
    </Card>
  );
}

export function DevicesPage() {
  const { state } = useEditor();
  const { auth, requestLogin } = useConnection();
  const ps = usePageSettings('devices', { 'device.host': 'Address', 'device.output': 'Port' });
  const [adding, setAdding] = useState(false);
  const controllers = state.spec.controllers;
  return (
    <PageFrame
      title="Devices"
      subtitle="Your LED controller: how Overglow reaches it, and the outputs your strips are wired to."
      actions={<SaveBar />}
      settings={ps}
      gate={false}
      unsavedLabel="LED driver settings changed"
    >
      {auth === 'admin' && ps.ready ? (
        <DriverCard ps={ps} />
      ) : auth === 'readOnly' ? (
        <Callout tone="info">
          <button type="button" className="font-medium text-accent-strong hover:underline" onClick={requestLogin}>
            Sign in
          </button>{' '}
          to see and change the LED driver settings.
        </Callout>
      ) : null}
      <div className="flex items-end justify-between gap-3 pt-2">
        <div>
          <h2 className="text-[15px] font-semibold">Outputs for the layout</h2>
          <p className="text-[13px] text-ink-muted">The data outputs of your controller and the strips wired to them. Saved with the layout (top right).</p>
        </div>
      </div>
      {controllers.length > 1 && (
        <div className="flex gap-3 rounded-xl border border-warn/30 bg-warn/10 p-4 text-[13px] text-warn">
          <TriangleAlert size={18} className="shrink-0" />
          <p>
            Layouts with more than one controller can be designed here, but saving them isn’t supported yet. Keep a single controller to
            save your layout.
          </p>
        </div>
      )}
      {controllers.length === 0 ? (
        <Card>
          <EmptyState icon={<Cpu size={28} />} title="No controllers yet">
            Add the WLED controller your LED strips are connected to.
          </EmptyState>
        </Card>
      ) : (
        controllers.map((c) => <ControllerCard key={c.id} controller={c} />)
      )}
      <Button icon={<Plus size={16} />} onClick={() => setAdding(true)}>
        Add WLED controller
      </Button>
      <AddControllerDialog open={adding} onClose={() => setAdding(false)} />
    </PageFrame>
  );
}
