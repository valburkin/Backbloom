import { useEffect, useState } from 'react';
import { ArrowRight, Scissors } from 'lucide-react';
import { Button } from '../components/ui';
import { Advanced, Callout, NumberField, Select } from '../components/form';
import { useConnection } from '../app/connection';
import { identifyPattern } from './FindStripsStep';
import type { ColorOrder, WledState } from '../api/types';
import { Busy, ErrorText, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';
import { PlaceCards } from './StripPlaces';
import { StripTypePicker } from './StripTypePicker';
import { presetById } from './plan';
import { boardChange, boardOutputs, ledsFromLength, placesWords, stripProblem, waitForBoard, withStrip, type BoardSettings } from './wled';

export const COLOR_ORDERS: readonly ColorOrder[] = ['GRB', 'RGB', 'BRG', 'RBG', 'BGR', 'GBR'];
const QUICK_DENSITIES = [30, 60, 96, 144];

/**
 * One strip, lit so the user knows which: what type it is (StripTypePicker), then on to its checks, where its LED count
 * comes from. Its places can be changed; the LED count, data pin and color order wait under More options. Next writes
 * the board (the type may need a restart).
 */
export function StripTypeStep({
  frame,
  host,
  board: b,
  strip: i,
  onChange,
  onPlan,
  onNext,
  onLit,
}: {
  frame: StepFrame;
  host: string;
  board: BoardSettings;
  strip: number;
  onChange(b: BoardSettings): void;
  onPlan(i: number): void;
  onNext(read: WledState): void;
  /** A test is lit on the board (the wizard puts its state back when it closes). */
  onLit?(): void;
}) {
  const { api } = useConnection();
  const life = useStepLife();
  const s = b.strips[i];
  const [changePlaces, setChangePlaces] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<typeof s>) => onChange(withStrip(b, i, patch));
  const preset = s.preset ? presetById(s.preset) : undefined;
  const problem = stripProblem(b, i);

  // this strip lit, dimly, the others dark: "this is the one"
  useEffect(() => {
    onLit?.();
    api.wledTest(host, identifyPattern(i)).catch(() => undefined);
  }, [host, i]);

  const next = async () => {
    if (busy || problem) return;
    setBusy('Saving to the board…');
    setError(null);
    try {
      const restarting = await api.wledWrite(host, boardChange(b));
      if (!life.alive()) return;
      if (restarting) setBusy('The board restarts to use the new type…');
      const read = restarting ? await waitForBoard(() => api.wledRead(host), { signal: life.signal }) : await api.wledRead(host);
      if (life.alive()) onNext(read);
    } catch (e) {
      if (life.alive()) setError(errorMessage(e));
    } finally {
      if (life.alive()) setBusy(null);
    }
  };

  return frame({
    backDisabled: !!busy,
    actions: (
      <Button variant="primary" disabled={!!busy || !!problem} title={problem ?? undefined} onClick={() => void next()}>
        Next: find how many LEDs it has <ArrowRight size={16} />
      </Button>
    ),
    children: (
      <>
        {b.strips.length > 1 && (
          <p className="text-xs text-ink-faint">
            Strip {i + 1} of {b.strips.length}
          </p>
        )}
        <StepTitle title={`This is the strip ${placesWords(s.places)}`}>
          It’s lit now.{' '}
          {!changePlaces && (
            <button type="button" className="text-accent-strong hover:underline" onClick={() => setChangePlaces(true)}>
              Not there?
            </button>
          )}
        </StepTitle>
        {changePlaces && <PlaceCards places={s.places} onChange={(places) => set({ places })} />}
        <StripTypePicker
          index={i}
          value={s.preset}
          sameAs={i > 0 ? b.strips[0].preset : null}
          onChange={(id, density) => set({ preset: id, colorOrder: id === s.preset ? s.colorOrder : null, plan: id === s.preset ? s.plan : null, ...(density ? { density } : {}) })}
        />
        {s.places.includes('screen') &&
          (s.plan ? (
            <p className="flex flex-wrap items-center gap-2 text-sm text-ink-muted">
              <Scissors size={15} aria-hidden /> Planned: {s.plan.totalLeds} LEDs on {s.plan.pieces.length} pieces.
              <Button size="sm" variant="ghost" onClick={() => onPlan(i)}>
                Change the plan
              </Button>
            </p>
          ) : (
            <Button size="sm" variant="ghost" icon={<Scissors size={15} />} onClick={() => onPlan(i)}>
              Not cut yet? Plan the cuts and power
            </Button>
          ))}
        <Advanced label="More options">
          <LengthHelper key={s.preset ?? ''} cob={!!preset?.cob} density={s.density ?? null} onCount={(n) => set({ ledCount: n })} />
          <div className="grid gap-4 sm:grid-cols-2">
            <NumberField
              label="Number of LEDs, if you know it"
              integer
              min={1}
              max={4000}
              value={s.ledCount ?? undefined}
              hint="The next check finds it anyway."
              onChange={(n) => n >= 1 && n <= 4000 && set({ ledCount: n })}
            />
            <NumberField
              label="Data pin"
              integer
              min={0}
              max={48}
              value={s.pin}
              hint="Found when the strip lit up. Change it only if you know better."
              onChange={(pin) => pin >= 0 && set({ pin })}
            />
            {preset?.clock && (
              <NumberField
                label="Clock pin"
                integer
                min={0}
                max={48}
                value={s.clockPin ?? boardOutputs(b)[i]?.clockPin}
                onChange={(clockPin) => clockPin >= 0 && set({ clockPin })}
              />
            )}
            <Select<ColorOrder | 'auto'>
              label="Color order"
              value={s.colorOrder ?? 'auto'}
              options={[{ value: 'auto', label: 'The strip type’s usual one' }, ...COLOR_ORDERS.map((o) => ({ value: o, label: o }))]}
              hint="The color check finds it for you."
              onChange={(o) => set({ colorOrder: o === 'auto' ? null : o })}
            />
          </div>
        </Advanced>
        {problem && !problem.startsWith('Pick') && <Callout tone="bad">{problem}</Callout>}
        {busy && <Busy>{busy}</Busy>}
        {error && <ErrorText>{error}</ErrorText>}
      </>
    ),
  });
}

/** "Count the LEDs from the length": its length and LEDs (COB: lit groups) per meter give its count. */
export function LengthHelper({ cob, density, onCount }: { cob: boolean; density: number | null; onCount(n: number): void }) {
  const [cm, setCm] = useState<number | undefined>(undefined);
  const [perMeter, setPerMeter] = useState<number | undefined>(density ?? (cob ? undefined : 60));
  const n = cm && perMeter ? ledsFromLength(cm, perMeter) : null;
  return (
    <div className="space-y-1.5">
      <p className="text-sm font-medium">Count the LEDs from the length</p>
      <div className="flex flex-wrap items-end gap-3">
        <NumberField label="Length" unit="cm" value={cm} min={1} max={10000} onChange={(v) => setCm(v > 0 ? v : undefined)} />
        <NumberField
          label={cob ? 'Lit groups per meter' : 'LEDs per meter'}
          integer
          min={1}
          max={1000}
          value={perMeter}
          onChange={(v) => v >= 1 && v <= 1000 && setPerMeter(v)}
        />
        <Button size="sm" disabled={!n} onClick={() => n && onCount(n)}>
          {n ? `Use ${n} LEDs` : 'Use'}
        </Button>
      </div>
      {!cob && (
        <div className="flex flex-wrap gap-1" role="group" aria-label="Common LEDs per meter">
          {QUICK_DENSITIES.map((d) => (
            <Button key={d} size="sm" variant={perMeter === d ? 'secondary' : 'ghost'} onClick={() => setPerMeter(d)}>
              {d}/m
            </Button>
          ))}
        </div>
      )}
      <p className="text-xs text-ink-faint">
        {cob
          ? 'A COB strip lights in groups of LEDs: the product page gives them as “IC/m” or “pixels/m” (not its LEDs/m, which are hundreds).'
          : 'From the product page or the label on the reel.'}
      </p>
    </div>
  );
}
