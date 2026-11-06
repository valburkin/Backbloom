import { useState, type FormEvent } from 'react';
import { Lightbulb, Loader2, Radio, Search, Usb, Wifi } from 'lucide-react';
import { Badge, Button, cx, TILE } from '../components/ui';
import { Advanced, Callout, Input } from '../components/form';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { useExpertTools } from '../app/expert';
import type { FindResult, FoundBoard } from '../api/types';
import { DiscoveryDialog } from '../pages/devices/DriverForm';
import { addedAddresses } from '../pages/devices/drivers';
import { CheckList, ErrorText, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';
import { hostOf } from './wled';

/** A found board in words: a title and one line (no address: that is for expert tools). */
export function boardCard(b: FoundBoard): { title: string; line: string } {
  if (b.via === 'usb') {
    const port = b.portName ?? b.port;
    return { title: 'New board', line: ['Plugged into this PC', port, b.chip].filter(Boolean).join(' · ') };
  }
  if (b.via === 'hotspot') return { title: 'New board', line: [`Its own Wi-Fi “${b.hotspot ?? 'WLED-AP'}”`, signalWords(b.signal)].filter(Boolean).join(' · ') };
  const named = b.name && b.name !== 'WLED' ? `WLED “${b.name}”` : b.isNew ? 'New board' : 'WLED board';
  const what = b.setUp ? `${b.ledCount ?? 0} LEDs set up` : 'not set up yet';
  return { title: named, line: ['On your Wi-Fi', b.mdnsName, what].filter(Boolean).join(' · ') };
}

/** A hotspot's signal in words. */
export function signalWords(signal: number | undefined): string | null {
  if (signal === undefined) return null;
  return signal >= 70 ? 'strong signal' : signal >= 40 ? 'good signal' : 'weak signal: move closer';
}

/** A board found by USB or by its hotspot isn't on the home Wi-Fi: it gets the Wi-Fi first. */
export function needsWifi(b: FoundBoard): boolean {
  return b.via !== 'network';
}

/** Boards to set up first (new, then those needing Wi-Fi), then the others by name. */
export function sortBoards(boards: readonly FoundBoard[]): FoundBoard[] {
  const route = { usb: 0, hotspot: 1, network: 2 } as const;
  return [...boards].sort((a, b) => Number(b.isNew) - Number(a.isNew) || route[a.via] - route[b.via] || a.name.localeCompare(b.name));
}

/**
 * Find: one Search over every route (USB, the home network, new boards' hotspots), the boards as cards to pick. An
 * address is typed in only under "Can’t find it?", after the things to check.
 */
export function FindStep({
  frame,
  onPick,
  onAddress,
}: {
  frame: StepFrame;
  onPick(board: FoundBoard, wifi: FindResult['wifi']): void;
  onAddress(host: string): void;
}) {
  const { api, setup, applySetup, auth } = useConnection();
  const { state } = useEditor();
  const expert = useExpertTools();
  const life = useStepLife();
  const [result, setResult] = useState<FindResult | null>(null);
  const [searching, setSearching] = useState(false);
  const [explain, setExplain] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [address, setAddress] = useState('');
  const [blinking, setBlinking] = useState<string | null>(null);
  /** "Which one is it?": the board's lights flash three times. */
  const blink = async (b: FoundBoard) => {
    if (!b.host) return;
    setBlinking(b.id);
    try {
      await api.wledIdentify(b.host);
    } catch (e) {
      if (life.alive()) setError(errorMessage(e));
    } finally {
      if (life.alive()) setBlinking(null);
    }
  };
  const added = addedAddresses(state.spec);

  const run = async () => {
    if (searching) return;
    setSearching(true);
    setError(null);
    try {
      const r = await api.findBoards();
      if (life.alive()) setResult(r);
    } catch (e) {
      if (life.alive()) setError(errorMessage(e));
    } finally {
      if (life.alive()) setSearching(false);
    }
  };
  // the search asks the devices on the network too: explained once, as every search is
  const search = () => {
    if (setup && !setup.acknowledged.includes('discovery')) setExplain(true);
    else void run();
  };
  const submitAddress = (e?: FormEvent) => {
    e?.preventDefault();
    if (address.trim()) onAddress(hostOf(address));
  };

  const boards = result ? sortBoards(result.boards) : [];
  const blocked = result?.wifi.blocked === 'location';
  const noWifi = !!result && !result.wifi.adapter;

  return (
    <>
      {frame({
        actions: (
          <Button
            variant={result && boards.length ? 'secondary' : 'primary'}
            icon={searching ? <Loader2 size={15} className="animate-spin" /> : <Search size={15} />}
            disabled={searching}
            onClick={search}
          >
            {searching ? 'Searching…' : result ? 'Search again' : 'Search'}
          </Button>
        ),
        children: (
          <>
            <StepTitle title="Find your board">Power the board on. If it has a USB port, you can also plug it into this PC.</StepTitle>
            {!result && !searching && (
              <p className="text-xs text-ink-faint">Windows may ask to let Backbloom see nearby Wi-Fi networks: that’s how it finds new boards. Allow it.</p>
            )}
            {searching && (
              <p role="status" className="flex items-center gap-2 text-sm text-ink-muted">
                <Loader2 size={15} className="animate-spin" aria-hidden /> Looking on USB, on your Wi-Fi and for new boards nearby… (about 10 seconds)
              </p>
            )}
            {error && <ErrorText>{error}</ErrorText>}
            {result && !searching && boards.length > 0 && (
              <ul className="space-y-2" aria-label="Boards found">
                {boards.map((b) => {
                  const c = boardCard(b);
                  const already = !!b.host && added.has(b.host.toLowerCase());
                  const Icon = b.via === 'usb' ? Usb : b.via === 'hotspot' ? Radio : Wifi;
                  return (
                    <li key={b.id} className="flex items-stretch gap-2">
                      <button
                        type="button"
                        disabled={already}
                        onClick={() => onPick(b, result.wifi)}
                        className={cx(
                          'flex min-w-0 flex-1 items-start gap-3 rounded-2xl px-4 py-3 text-left pointer-coarse:min-h-11',
                          TILE,
                          already && 'cursor-not-allowed text-ink-muted',
                        )}
                      >
                        <Icon size={18} className="mt-0.5 shrink-0 text-ink-faint" aria-hidden />
                        <span className="min-w-0 flex-1">
                          <span className="block text-sm font-medium">{c.title}</span>
                          <span className="mt-0.5 block text-xs text-ink-faint">{c.line}</span>
                          {expert && (
                            <span className="mt-0.5 block font-mono text-xs text-ink-faint">{[b.host, b.port, b.hotspot, b.mac, b.version].filter(Boolean).join(' · ')}</span>
                          )}
                        </span>
                        {already && <Badge>Already added</Badge>}
                      </button>
                      {b.via === 'network' && b.host && (
                        <Button
                          className="h-auto shrink-0"
                          icon={blinking === b.id ? <Loader2 size={15} className="animate-spin" /> : <Lightbulb size={15} />}
                          title="Flashes its lights three times"
                          disabled={blinking === b.id}
                          onClick={() => void blink(b)}
                        >
                          Blink
                        </Button>
                      )}
                    </li>
                  );
                })}
              </ul>
            )}
            {result && !searching && boards.length === 0 && (
              <Callout>
                <p className="font-medium text-ink">No board found.</p>
                <div className="mt-1.5">
                  <CheckList
                    items={[
                      'The board’s light is on. Just powered it on? Wait about 20 seconds, then search again.',
                      'Plug it into this PC with a USB cable, if it has a USB port (some cables only charge).',
                    ]}
                  />
                </div>
              </Callout>
            )}
            {blocked && (
              <Callout tone="warn">
                Windows doesn’t let Backbloom see nearby Wi-Fi networks, so new boards that aren’t plugged in stay hidden. To allow it, open Windows
                Settings → Privacy &amp; security → Location and turn on “Let desktop apps access your location”, then search again. Or plug the board in
                with a USB cable.
              </Callout>
            )}
            {noWifi && !blocked && <p className="text-xs text-ink-faint">This PC has no Wi-Fi, so it can’t see new boards nearby: plug a new board in with a USB cable.</p>}
            {result && result.usbSilent.length > 0 && !boards.some((b) => b.via === 'usb') && (
              <p className="text-xs text-ink-faint">
                A board on USB doesn’t answer: close other programs that may use it, or it may not run WLED yet (install WLED on it from install.wled.me).
              </p>
            )}
            {result && (
              <Advanced label="Can’t find it?">
                <CheckList
                  items={[
                    'The board is powered on, and its light is on.',
                    'A new board without USB opens its own Wi-Fi for a few minutes after it is powered on: power it off and on again, then search.',
                    'A board on your Wi-Fi must be on the same network as this PC (guest networks keep devices apart).',
                  ]}
                />
                <form onSubmit={submitAddress} className="max-w-md">
                  <Input
                    label="Its address"
                    hint="Your router’s list of devices shows it, for example 192.168.1.40."
                    value={address}
                    onChange={setAddress}
                    trailing={
                      <Button type="submit" disabled={!address.trim()} className="shrink-0">
                        Connect
                      </Button>
                    }
                  />
                </form>
              </Advanced>
            )}
          </>
        ),
      })}
      <DiscoveryDialog
        open={explain}
        onCancel={() => setExplain(false)}
        onConfirm={() => {
          setExplain(false);
          if (auth === 'admin') void api.acknowledge('discovery').then(applySetup, () => undefined);
          void run();
        }}
      />
    </>
  );
}
