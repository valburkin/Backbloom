import { useState } from 'react';
import { Plus, RefreshCw, Trash2 } from 'lucide-react';
import { stripLedCount, type Controller } from '../layout';
import { Badge, Button, Card, IconButton, StatusDot } from '../components/ui';
import { Advanced, Switch } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { SaveBar } from '../components/SaveBar';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { stripColor } from '../editor/colors';
import { validate, withDefaults } from '../schema/schema';
import type { ControllerInfo } from '../api/types';
import { deviceStatus, isUsbDevice } from '../control/health';
import { connectionSchema, DriverForm, useDriverSchemas } from './devices/DriverForm';
import { AddDeviceDialog, errorText, fetchController } from './devices/AddDeviceDialog';
import { NoLights, shownDevices } from '../setup/NoLights';
import { useExpertTools } from '../app/expert';
import { addressOf, driverName, isLampDevice, lampCount, lampList, ledCountLabel } from './devices/drivers';
import { isPerLampDevice, lampListChange } from './devices/lamps';
import { placePath } from '../setup/lights';
import { withRole } from '../setup/roles';

/** The device's state in the words Home uses too (control/health.ts). */
function ControllerStatus({ info, usb }: { info: ControllerInfo | undefined; usb: boolean }) {
  if (!info) return <span className="text-xs text-ink-faint">Not saved yet</span>;
  const s = deviceStatus(info, usb);
  return (
    <span className="flex items-center gap-2 text-xs text-ink-muted" title={info.status ?? undefined}>
      <StatusDot tone={s.tone} live={s.live} />
      {s.text}
    </span>
  );
}

function ControllerCard({ controller }: { controller: Controller }) {
  const { state, dispatch, dirty } = useEditor();
  const { backend, auth, serverInfo } = useConnection();
  const { general, drivers } = useDriverSchemas();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const total = controller.outputs.reduce((n, o) => n + o.length, 0);
  const wled = controller.kind === 'wled';
  const info = serverInfo?.controllers?.find((c) => c.id === controller.id);
  const saved = state.saved?.controllers.find((c) => c.id === controller.id)?.device;
  const type = String(controller.device?.type ?? (wled ? 'wled' : controller.kind));
  const lamps = isLampDevice(type);
  const usb = isUsbDevice(type, serverInfo?.ledDevices);
  const schema = connectionSchema(type, drivers?.[type], general);
  const invalid = !!schema && !!controller.device && validate(schema, withDefaults(schema, controller.device)).length > 0;
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

  const address = addressOf(controller);
  return (
    <Card className="p-5">
      <div className="flex items-start gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <input
              aria-label="Device name"
              style={{ width: `calc(${Math.max(4, controller.name.length)}ch + 1rem)` }}
              className="-mx-1 min-w-0 max-w-full rounded-md bg-transparent px-1 text-base font-semibold hover:bg-surface-2 focus:bg-surface-2"
              value={controller.name}
              onChange={(e) => update({ name: e.target.value }, `ctrl-name-${controller.id}`)}
            />
            <span className="text-[13px] text-ink-muted">{driverName(type)}</span>
          </div>
          <p className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[13px] text-ink-muted">
            <span>{[wled && !address ? 'Address unknown — sign in to see it' : address, `${total} ${lamps ? (total === 1 ? 'lamp' : 'lamps') : 'LEDs'}`].filter(Boolean).join(' · ')}</span>
            <ControllerStatus info={info} usb={usb} />
          </p>
        </div>
        <div className="flex items-center gap-1">
          <Switch label={`Send colors to ${controller.name}`} checked={controller.enabled !== false} onChange={(on) => update({ enabled: on ? undefined : false })} />
          {wled && (
            <IconButton label="Reload outputs from WLED" disabled={busy || !controller.host} onClick={() => void refresh()}>
              <RefreshCw size={16} className={busy ? 'animate-spin' : ''} />
            </IconButton>
          )}
          <IconButton
            label="Remove device"
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

      <table className="mt-4 w-full text-[13px]">
        <thead className="text-left text-xs text-ink-faint">
          <tr>
            <th className="py-1.5 pr-3 font-medium">Output</th>
            <th className="py-1.5 pr-3 font-medium">{lamps ? 'Lamps' : 'LEDs'}</th>
            <th className="py-1.5 font-medium">Strip</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-line border-t border-line">
          {controller.outputs.map((o) => {
            const strips = state.spec.strips.map((s, i) => ({ s, i })).filter(({ s }) => s.controllerId === controller.id && s.output === o.index);
            return (
              <tr key={o.index}>
                <td className="py-2.5 pr-3">
                  Output {o.index + 1}
                  {o.pin !== undefined && <span className="text-ink-faint"> · pin {o.pin}</span>}
                </td>
                <td className="py-2.5 pr-3 tabular-nums">
                  {wled || lamps || controller.outputs.length > 1 ? (
                    <>
                      {o.length}
                      {controller.outputs.length > 1 && (
                        <span className="text-ink-faint">
                          {' '}
                          · LEDs {o.start + 1}–{o.start + o.length}
                        </span>
                      )}
                    </>
                  ) : (
                    <input
                      type="number"
                      aria-label={`${ledCountLabel(type)} on ${controller.name}`}
                      className="h-8 w-20 rounded-md border border-line-input bg-bg px-2 text-[13px] tabular-nums"
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
                <td className="py-2.5">
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
                              {n} of {o.length} LEDs
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

      {auth === 'admin' && controller.device ? (
        <div className="mt-4" data-focus="connection" data-focus-open>
          <Advanced label="Connection settings" invalid={invalid}>
            <DriverForm
              controllerId={controller.id}
              device={controller.device}
              saved={saved}
              dirty={dirty}
              onChange={(device) => {
                // a lamp device has one LED per lamp: the outputs follow the lamp list
                const coalesce = `ctrl-device-${controller.id}`;
                const before = lampList(controller.device);
                const after = lampList(device);
                if (before && after && isPerLampDevice(controller) && before.length !== after.length) {
                  // one output and strip per lamp (as Add lights makes them): each strip stays with its lamp
                  const change = lampListChange(controller, state.spec.strips, before, after);
                  change.removeStrips.forEach((stripId) => dispatch({ type: 'removeStrip', stripId, coalesce }));
                  update({ device, outputs: change.outputs }, coalesce);
                  change.moveStrips.forEach(({ stripId, output }) => dispatch({ type: 'updateStrip', stripId, patch: { output }, coalesce }));
                  const nth = state.spec.strips.filter((s) => s.controllerId === controller.id).length;
                  change.addStrips.forEach(({ output, name }, k) =>
                    dispatch({ type: 'addStrip', controllerId: controller.id, output, name, path: withRole(placePath(state.spec.room, 'behind', nth + k), 'soft'), coalesce }),
                  );
                  return;
                }
                const n = lampCount(device);
                const out = controller.outputs[0];
                update(n !== null && controller.outputs.length === 1 && out.length !== n ? { device, outputs: [{ ...out, length: n }] } : { device }, coalesce);
              }}
            />
          </Advanced>
        </div>
      ) : null}
    </Card>
  );
}

export function DevicesPage() {
  const { state, load } = useEditor();
  const expert = useExpertTools();
  const [adding, setAdding] = useState(false);
  // the test device of a new install (colors go nowhere) only with expert tools on
  const controllers = shownDevices(state.spec.controllers, expert);
  const loaded = load.status === 'ready' || load.status === 'error';
  return (
    <PageFrame title="Devices" subtitle="The hardware your LED strips are plugged into. All devices show the same picture, in sync." actions={<SaveBar />}>
      {!loaded ? null : controllers.length === 0 ? (
        <NoLights onAdd={() => setAdding(true)} />
      ) : (
        <>
          <div className="flex items-end justify-between gap-3">
            <p className="text-sm text-ink-muted">Changes here are saved with the Save button at the top.</p>
            <Button icon={<Plus size={16} />} onClick={() => setAdding(true)}>
              Add device
            </Button>
          </div>
          {controllers.map((c) => (
            <ControllerCard key={c.id} controller={c} />
          ))}
        </>
      )}
      <AddDeviceDialog open={adding} onClose={() => setAdding(false)} />
    </PageFrame>
  );
}
