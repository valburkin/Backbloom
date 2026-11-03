import { useMemo, useState, type ReactNode } from 'react';
import { ArrowLeft, ArrowRight, CheckCircle2, Lightbulb, Loader2, RefreshCw, ScanSearch } from 'lucide-react';
import { Badge, Button, ChoiceCard, Dialog, cx } from '../components/ui';
import { Callout, Input, Select } from '../components/form';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import type { DiscoveredDevice, HueBridgeState } from '../api/types';
import { DiscoveryDialog, newDriverSettings, useDriverSchemas } from '../pages/devices/DriverForm';
import { addedAddresses } from '../pages/devices/drivers';
import { ErrorText, StepDots, StepTitle, errorMessage } from './parts';
import {
  LIGHT_PLACES,
  NO_AUTH,
  brandInfo,
  defaultPlace,
  entertainmentAreas,
  homeAssistantLights,
  hueColorLights,
  hueLights,
  identifyParams,
  lightDevices,
  lightsFromSearch,
  isAdded,
  knownLights,
  nanoleafLight,
  searchableBrands,
  selectionAfterSearch,
  suggestedNames,
  type FoundLight,
  type LightAuth,
  type LightBrand,
  type LightPlace,
} from './lights';
import { useAddToLayout } from './useAddToLayout';

const STEPS = ['Find', 'Which is which'] as const;

type Results = Partial<Record<LightBrand, DiscoveredDevice[]>>;

/** Per bridge or server: pairing in progress and its error; Hue: the bridge's lights and areas, the area chosen. */
interface Pairing {
  busy?: boolean;
  error?: string | null;
  hue?: HueBridgeState;
  areaId?: string;
  /** Hue: making a new area from these lights. */
  creating?: string[];
  token?: string;
}

/**
 * "Add lights" (docs/guided-setup.md): one search across the room-light device types, one prompt per brand where
 * pairing needs it, then "Which light is which" (each light blinks; the user names it and says where it is), and the
 * lights added to the layout, one device per brand.
 */
export function AddLights({ onClose, onAdded, onBack }: { onClose(): void; onAdded(name: string): void; onBack?: () => void }) {
  const { api, serverInfo, setup, applySetup, auth: session } = useConnection();
  const { state } = useEditor();
  const schemas = useDriverSchemas();
  const add = useAddToLayout();
  const [step, setStep] = useState<0 | 1>(0);
  const [explain, setExplain] = useState(false);
  const [searching, setSearching] = useState(false);
  const [results, setResults] = useState<Results | null>(null);
  const [failed, setFailed] = useState<LightBrand[]>([]);
  const [paired, setPaired] = useState<FoundLight[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [pairing, setPairing] = useState<Record<string, Pairing>>({});
  const [auth, setAuth] = useState<LightAuth>(NO_AUTH);
  const [labels, setLabels] = useState<Record<string, string>>({});
  const [places, setPlaces] = useState<Record<string, LightPlace>>({});
  const [blinking, setBlinking] = useState<string | null>(null);
  const [blinkError, setBlinkError] = useState<string | null>(null);
  const [manualHa, setManualHa] = useState('');
  /** A Home Assistant server entered by hand (the search didn't find one). */
  const [haHost, setHaHost] = useState<string | null>(null);
  const brands = searchableBrands(serverInfo?.ledDevices?.available);
  const known = knownLights(state.spec.controllers, addedAddresses(state.spec));
  const inLayout = (l: FoundLight) => isAdded(l, known);

  const direct = useMemo(
    () => (results ? (['govee', 'lifx', 'wiz', 'yeelight'] as const).flatMap((b) => lightsFromSearch(b, results[b] ?? [])) : []),
    [results],
  );
  const all = [...direct, ...paired];
  /** The lights the search before this one found by themselves: unpicked ones stay unpicked when searching again. */
  const directKeys = new Set(direct.map((l) => l.key));
  const chosen = all.filter((l) => selected.has(l.key));

  const pair = (key: string, patch: Pairing) => setPairing((p) => ({ ...p, [key]: { ...p[key], ...patch } }));
  /** Picks lights (never those in the layout already) or unpicks them. */
  const select = (lights: readonly FoundLight[], on: boolean) =>
    setSelected((s) => {
      const next = new Set(s);
      for (const l of lights) if (on && !inLayout(l)) next.add(l.key);
      else next.delete(l.key);
      return next;
    });

  const run = async () => {
    setSearching(true);
    const settled = await Promise.allSettled(brands.map((b) => api.discover(b)));
    const next: Results = {};
    const bad: LightBrand[] = [];
    settled.forEach((r, i) => {
      if (r.status === 'fulfilled') next[brands[i]] = r.value;
      else bad.push(brands[i]);
    });
    setResults(next);
    setFailed(bad);
    // what answered by itself is picked, except what is in the layout already; what was picked before stays picked
    // while it is still there. Paired lights (a Hue bridge, a Nanoleaf, Home Assistant) stay whether or not the search
    // finds their device again: it is known by its address, and searches miss devices now and then.
    const found = (['govee', 'lifx', 'wiz', 'yeelight'] as const).flatMap((b) => lightsFromSearch(b, next[b] ?? []));
    setSelected((prev) => selectionAfterSearch(prev, results ? directKeys : new Set(), found, paired, known));
    setSearching(false);
  };
  const searchNow = () => {
    if (setup && !setup.acknowledged.includes('discovery')) setExplain(true);
    else void run();
  };

  // ---- pairing ----

  const connectHue = async (host: string) => {
    pair(host, { busy: true, error: null });
    try {
      const { username, clientkey } = await api.huePair(host);
      const bridge = await api.hueBridge(host, username);
      setAuth((a) => ({ ...a, hue: { ...a.hue, [host]: { username, clientkey, areaId: '' } } }));
      const areas = entertainmentAreas(bridge);
      pair(host, { busy: false, hue: bridge, ...(areas.length === 0 ? { creating: hueColorLights(bridge).map((l) => l.id) } : {}) });
      if (areas.length === 1) chooseArea(host, bridge, areas[0].id);
    } catch (e) {
      const msg = errorMessage(e);
      pair(host, { busy: false, error: /link button/i.test(msg) ? 'The bridge didn’t get the button press. Press the round button on top of it, then Connect within 30 seconds.' : msg });
    }
  };

  const chooseArea = (host: string, bridge: HueBridgeState, areaId: string) => {
    const area = entertainmentAreas(bridge).find((a) => a.id === areaId);
    if (!area) return;
    const lights = hueLights(host, area);
    setAuth((a) => ({ ...a, hue: { ...a.hue, [host]: { ...a.hue[host], areaId } } }));
    setPaired((p) => [...p.filter((l) => !(l.brand === 'philipshue' && l.host === host)), ...lights]);
    select(lights, true);
    pair(host, { areaId, creating: undefined });
  };

  const createArea = async (host: string, lights: string[]) => {
    const user = auth.hue[host]?.username;
    if (!user || !lights.length) return;
    pair(host, { busy: true, error: null });
    try {
      const id = await api.hueCreateArea(host, user, 'Backbloom', lights);
      const bridge = await api.hueBridge(host, user);
      pair(host, { busy: false, hue: bridge });
      chooseArea(host, bridge, id);
    } catch (e) {
      pair(host, { busy: false, error: errorMessage(e) });
    }
  };

  const connectNanoleaf = async (host: string) => {
    pair(host, { busy: true, error: null });
    try {
      const token = await api.nanoleafPair(host);
      const info = await api.deviceProperties<Parameters<typeof nanoleafLight>[1]>('nanoleaf', { host, token });
      setAuth((a) => ({ ...a, nanoleaf: { ...a.nanoleaf, [host]: { token } } }));
      const light = nanoleafLight(host, info);
      setPaired((p) => [...p.filter((l) => l.key !== light.key), light]);
      select([light], true);
      pair(host, { busy: false });
    } catch (e) {
      pair(host, { busy: false, error: `${errorMessage(e)}. Hold the power button 5–7 seconds until the lights flash, then Connect again.` });
    }
  };

  const connectHa = async (host: string) => {
    const token = (pairing[host]?.token ?? '').trim();
    if (!token) return;
    pair(host, { busy: true, error: null });
    try {
      const lights = homeAssistantLights(host, await api.homeAssistantLights(host, token));
      setAuth((a) => ({ ...a, homeAssistant: { ...a.homeAssistant, [host]: { token } } }));
      setPaired((p) => [...p.filter((l) => !(l.brand === 'home_assistant' && l.host === host)), ...lights]);
      select(lights, true);
      pair(host, { busy: false, error: lights.length ? null : 'Home Assistant didn’t list any lights. Check the token, or add its lights later in the device’s connection settings on Devices.' });
    } catch (e) {
      pair(host, { busy: false, error: errorMessage(e) });
    }
  };

  // ---- which is which ----

  const blink = async (l: FoundLight) => {
    setBlinking(l.key);
    setBlinkError(null);
    try {
      const params = identifyParams(l, auth);
      if (!params) throw new Error('the bridge didn’t say which light this is, so it can’t blink');
      await api.identifyLight(l.brand, params);
    } catch (e) {
      setBlinkError(`${labels[l.key] ?? l.name}: ${errorMessage(e)}`);
    } finally {
      setBlinking((b) => (b === l.key ? null : b));
    }
  };

  const toWhich = () => {
    const names = suggestedNames(chosen);
    setLabels((prev) => Object.fromEntries(chosen.map((l) => [l.key, prev[l.key] ?? names.get(l.key) ?? l.name])));
    setPlaces((prev) => Object.fromEntries(chosen.map((l, i) => [l.key, prev[l.key] ?? defaultPlace(i)])));
    setStep(1);
  };

  const finish = () => {
    const placed = chosen.map((l) => ({ ...l, label: labels[l.key] ?? l.name, place: places[l.key] ?? 'behind' }));
    const devices = lightDevices(placed, auth, state.spec.room, (type, extra) => newDriverSettings(type, schemas, extra));
    add(devices);
    const names = devices.map((d) => d.controller.name);
    onAdded(names.join(', '));
  };

  const frame = (actions: ReactNode, body: ReactNode, back?: () => void) => (
    <Dialog
      open
      wide
      title="Add lights"
      // a stray click beside it mustn't lose a pairing (Hue's button, Nanoleaf's power button)
      closeOnBackdrop={false}
      onClose={onClose}
      footer={
        <>
          {back && (
            <Button variant="ghost" className="mr-auto" icon={<ArrowLeft size={16} />} onClick={back}>
              Back
            </Button>
          )}
          {actions}
        </>
      }
    >
      <div className="space-y-5">
        <StepDots steps={STEPS} current={step} />
        {body}
      </div>
    </Dialog>
  );

  if (step === 1)
    return frame(
      <Button variant="primary" disabled={chosen.length === 0} onClick={finish}>
        Add {chosen.length === 1 ? '1 light' : `${chosen.length} lights`}
      </Button>,
      <>
        <StepTitle title="Which light is which">Press Blink to see which one it is, then name it and say where it is.</StepTitle>
        <ul className="divide-y divide-line border-y border-line">
          {chosen.map((l) => (
            <li key={l.key} className="grid items-end gap-3 py-3 sm:grid-cols-[auto_minmax(0,1fr)_minmax(0,1fr)]">
              <Button
                className="w-28"
                icon={blinking === l.key ? <Loader2 size={15} className="animate-spin" /> : <Lightbulb size={15} />}
                disabled={blinking === l.key}
                onClick={() => void blink(l)}
              >
                Blink
              </Button>
              <Input label={`Name · ${brandInfo(l.brand).name}`} value={labels[l.key] ?? l.name} onChange={(v) => setLabels((x) => ({ ...x, [l.key]: v }))} />
              <Select<LightPlace> label="Where it is" value={places[l.key] ?? 'behind'} options={LIGHT_PLACES} onChange={(v) => setPlaces((x) => ({ ...x, [l.key]: v }))} />
            </li>
          ))}
        </ul>
        {blinkError && <ErrorText>{blinkError}</ErrorText>}
        <p className="text-xs text-ink-faint">Each light glows softly in the screen’s average color, calm at the edge of your view. You can change what it shows on the Layout page.</p>
      </>,
      () => setStep(0),
    );

  // ---- find ----

  /** A brand's devices: what the searches found, and those paired here (kept when a later search misses them). */
  const hosts = (b: LightBrand) => [...new Set([...(results?.[b] ?? []).map((d) => d.value), ...paired.filter((l) => l.brand === b).map((l) => l.host), ...Object.keys(b === 'philipshue' ? auth.hue : b === 'nanoleaf' ? auth.nanoleaf : b === 'home_assistant' ? auth.homeAssistant : {})])];
  const haHosts = [...hosts('home_assistant'), ...(haHost && !hosts('home_assistant').includes(haHost) ? [haHost] : [])];
  const found = results !== null;
  const nothing = found && all.length === 0 && hosts('philipshue').length === 0 && hosts('nanoleaf').length === 0 && haHosts.length === 0;
  const missing = found ? brands.filter((b) => (results?.[b]?.length ?? 0) === 0 && brandInfo(b).missing) : [];

  const lightRow = (l: FoundLight) => {
    const added = inLayout(l);
    // a Hue area is added whole: the bridge streams to all its lights
    if (l.brand === 'philipshue')
      return (
        <li key={l.key} className="flex items-center gap-3 py-1.5 text-sm">
          <CheckCircle2 size={16} className={cx('shrink-0', added ? 'text-ink-faint' : 'text-accent-strong')} aria-hidden />
          {l.name}
          {l.id && <span className="font-mono text-xs text-ink-faint">light {l.id}</span>}
        </li>
      );
    return (
      <li key={l.key}>
        <label className={cx('flex items-center gap-3 py-2 text-sm', added ? 'text-ink-muted' : 'cursor-pointer')}>
          <input
            type="checkbox"
            className="h-4 w-4 accent-[var(--color-accent)]"
            checked={selected.has(l.key)}
            disabled={added}
            onChange={(e) => select([l], e.target.checked)}
          />
          <span className="min-w-0 flex-1">
            {l.name}
            <span className="ml-2 font-mono text-xs text-ink-faint">{l.panels ? `${l.panels} panels` : l.brand === 'home_assistant' ? l.id : l.host}</span>
          </span>
          {added && <Badge>Already added</Badge>}
        </label>
      </li>
    );
  };

  const group = (b: LightBrand, body: ReactNode) => (
    <section key={b} aria-label={brandInfo(b).name} className="py-4">
      <h4 className="mb-2 text-sm font-semibold">{brandInfo(b).name}</h4>
      {body}
    </section>
  );

  const connectButton = (busy: boolean | undefined, onClick: () => void, label = 'Connect') => (
    <Button size="sm" icon={busy ? <Loader2 size={14} className="animate-spin" /> : undefined} disabled={busy} onClick={onClick}>
      {busy ? 'Connecting…' : label}
    </Button>
  );

  const groups: ReactNode[] = [];
  if (found) {
    for (const host of hosts('philipshue')) {
      const p = pairing[host] ?? {};
      const bridge = p.hue;
      const areas = bridge ? entertainmentAreas(bridge) : [];
      groups.push(
        group(
          'philipshue',
          !bridge ? (
            <div className="space-y-2">
              <p className="text-sm text-ink-muted">Bridge at {host}. Press the round button on top of the bridge, then Connect.</p>
              {connectButton(p.busy, () => void connectHue(host))}
              {p.error && <ErrorText>{p.error}</ErrorText>}
            </div>
          ) : (
            <div className="space-y-3">
              {areas.length > 0 && (
                <div role="radiogroup" aria-label="Entertainment area" className="grid gap-2 sm:grid-cols-2">
                  {areas.map((a) => (
                    <ChoiceCard
                      key={a.id}
                      active={p.areaId === a.id && !p.creating}
                      title={a.name}
                      description={a.channels.map((c) => c.name).join(', ')}
                      onClick={() => chooseArea(host, bridge, a.id)}
                    />
                  ))}
                  <ChoiceCard
                    active={!!p.creating}
                    title="Make a new area"
                    description="From the color lights you pick"
                    onClick={() => pair(host, { creating: hueColorLights(bridge).map((l) => l.id) })}
                  />
                </div>
              )}
              {p.creating ? (
                <div className="space-y-2">
                  <p className="text-sm text-ink-muted">
                    {areas.length ? 'Lights for the new area' : 'Hue lights stream colors in an entertainment area. Pick its lights:'}
                  </p>
                  <ul>
                    {hueColorLights(bridge).map((l) => (
                      <li key={l.id}>
                        <label className="flex cursor-pointer items-center gap-3 py-1.5 text-sm">
                          <input
                            type="checkbox"
                            className="h-4 w-4 accent-[var(--color-accent)]"
                            checked={p.creating!.includes(l.id)}
                            onChange={(e) => pair(host, { creating: e.target.checked ? [...p.creating!, l.id] : p.creating!.filter((x) => x !== l.id) })}
                          />
                          {l.name}
                        </label>
                      </li>
                    ))}
                  </ul>
                  {connectButton(p.busy, () => void createArea(host, p.creating!), 'Make the area “Backbloom”')}
                </div>
              ) : (
                (() => {
                  const lights = paired.filter((l) => l.brand === 'philipshue' && l.host === host);
                  if (!lights.length) return null;
                  const on = lights.every((l) => selected.has(l.key));
                  const added = lights.some(inLayout);
                  return (
                    <div>
                      <label className={cx('flex items-center gap-3 py-2 text-sm font-medium', added ? 'text-ink-muted' : 'cursor-pointer')}>
                        <input
                          type="checkbox"
                          className="h-4 w-4 accent-[var(--color-accent)]"
                          checked={on && !added}
                          disabled={added}
                          onChange={(e) => select(lights, e.target.checked)}
                        />
                        Add the lights of this area
                        {added && <Badge>This bridge is added already</Badge>}
                      </label>
                      <ul className={cx('pl-7', !on && 'opacity-60')}>{lights.map(lightRow)}</ul>
                    </div>
                  );
                })()
              )}
              {p.error && <ErrorText>{p.error}</ErrorText>}
            </div>
          ),
        ),
      );
    }
    for (const b of ['govee', 'lifx', 'wiz', 'yeelight'] as const) {
      const lights = direct.filter((l) => l.brand === b);
      if (lights.length) groups.push(group(b, <ul>{lights.map(lightRow)}</ul>));
    }
    for (const host of hosts('nanoleaf')) {
      const p = pairing[host] ?? {};
      const light = paired.find((l) => l.brand === 'nanoleaf' && l.host === host);
      groups.push(
        group(
          'nanoleaf',
          light ? (
            <ul>{lightRow(light)}</ul>
          ) : (
            <div className="space-y-2">
              <p className="text-sm text-ink-muted">At {host}. Hold its power button for 5–7 seconds until the lights flash, then Connect.</p>
              {connectButton(p.busy, () => void connectNanoleaf(host))}
              {p.error && <ErrorText>{p.error}</ErrorText>}
            </div>
          ),
        ),
      );
    }
    for (const host of haHosts) {
      const p = pairing[host] ?? {};
      const lights = paired.filter((l) => l.brand === 'home_assistant' && l.host === host);
      groups.push(
        group(
          'home_assistant',
          lights.length ? (
            <ul>{lights.map(lightRow)}</ul>
          ) : (
            <div className="space-y-2">
              <p className="text-sm text-ink-muted">
                At {host}. In Home Assistant, open your profile → Security → Long-lived access tokens → Create token, and paste it here.
              </p>
              <div className="flex flex-wrap items-end gap-2">
                <Input className="min-w-0 flex-1" label="Access token" type="password" value={p.token ?? ''} onChange={(token) => pair(host, { token })} autoComplete="off" />
                {connectButton(p.busy, () => void connectHa(host))}
              </div>
              {p.error && <ErrorText>{p.error}</ErrorText>}
            </div>
          ),
        ),
      );
    }
  }

  return (
    <>
      {frame(
        <>
          {found && (
            <Button variant="ghost" icon={searching ? <Loader2 size={15} className="animate-spin" /> : <RefreshCw size={15} />} disabled={searching} onClick={searchNow}>
              Search again
            </Button>
          )}
          {found ? (
            <Button variant="primary" disabled={chosen.length === 0} onClick={toWhich}>
              Next: which is which <ArrowRight size={16} />
            </Button>
          ) : (
            <Button variant="primary" icon={searching ? <Loader2 size={15} className="animate-spin" /> : <ScanSearch size={15} />} disabled={searching} onClick={searchNow}>
              {searching ? 'Searching…' : 'Search for lights'}
            </Button>
          )}
        </>,
        <>
          <StepTitle title="Find your room lights">
            Backbloom looks for {brands.map((b) => brandInfo(b).name).join(', ')} on your network. A new light needs its own app once to join your Wi-Fi.
          </StepTitle>
          {searching && (
            <p role="status" className="flex items-center gap-2 text-sm text-ink-muted">
              <Loader2 size={15} className="animate-spin" aria-hidden /> Searching your network… (about 5 seconds)
            </p>
          )}
          {nothing && !searching && <Callout>No lights found. Check that they are on and on the same Wi-Fi as this PC.</Callout>}
          {groups.length > 0 && <div className="divide-y divide-line border-y border-line">{groups}</div>}
          {found && (missing.length > 0 || failed.length > 0 || hosts('home_assistant').length === 0) && (
            <div className="space-y-2 text-xs text-ink-faint">
              {missing.map((b) => (
                <p key={b}>
                  <span className="font-medium text-ink-muted">{brandInfo(b).name}:</span> {brandInfo(b).missing}
                </p>
              ))}
              {failed.map((b) => (
                <p key={b}>{brandInfo(b).name}: the search didn’t work this time. Search again.</p>
              ))}
              {hosts('home_assistant').length === 0 && brands.includes('home_assistant') && !haHosts.length && (
                <form
                  className="flex flex-wrap items-end gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (manualHa.trim()) setHaHost(manualHa.trim());
                  }}
                >
                  <Input className="min-w-0 flex-1" label="Home Assistant not found? Its address" placeholder="homeassistant.local:8123" value={manualHa} onChange={setManualHa} />
                  <Button size="sm" type="submit" disabled={!manualHa.trim()}>
                    Use it
                  </Button>
                </form>
              )}
            </div>
          )}
        </>,
        onBack,
      )}
      <DiscoveryDialog
        open={explain}
        onCancel={() => setExplain(false)}
        onConfirm={() => {
          setExplain(false);
          if (session === 'admin') void api.acknowledge('discovery').then(applySetup, () => undefined);
          void run();
        }}
      />
    </>
  );
}
