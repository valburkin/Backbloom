import { useState, type ReactNode } from 'react';
import { Printer } from 'lucide-react';
import { Button, Segmented } from '../components/ui';
import { Advanced, Callout, NumberField, Select, Toggle } from '../components/form';
import type { ScreenInfo } from '../api/types';
import { PlanDrawing, PlanLegend } from './PlanDrawing';
import { PrintArea } from './parts';
import {
  ASPECTS,
  DEFAULT_PLAN_INPUT,
  START_LABELS,
  STRIP_PRESETS,
  diagonalInches,
  normalizeInput,
  presetById,
  screenFromDiagonal,
  startOptions,
  supplyText,
  type AspectId,
  type ChecklistItem,
  type CornerJoin,
  type PlanInput,
  type StripPlan,
} from './plan';

/** What the plan form holds: the plan's input and how the screen's size was given. */
export interface PlanForm {
  input: PlanInput;
  /** `detected`: a monitor's size from this PC (`screen` = its index); else the diagonal, or width and height. */
  source: 'detected' | 'diagonal' | 'size';
  screen: number;
  diagonal: number;
  aspect: AspectId;
}

export const DEFAULT_PLAN_FORM: PlanForm = { input: DEFAULT_PLAN_INPUT, source: 'diagonal', screen: 0, diagonal: 27, aspect: '16:9' };

const STORAGE_KEY = 'backbloom.stripPlan';

/** The plan from the last visit (kept on this PC, so a plan made before buying parts is still there). */
export function loadPlanForm(): PlanForm | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const f = JSON.parse(raw) as PlanForm;
    return f && f.input && f.input.screen ? { ...DEFAULT_PLAN_FORM, ...f, input: normalizeInput({ ...DEFAULT_PLAN_INPUT, ...f.input }) } : null;
  } catch {
    return null;
  }
}

export function savePlanForm(form: PlanForm): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(form));
  } catch {
    /* storage unavailable */
  }
}

/** Monitors this PC reports a size for. */
export function sizedScreens(screens: readonly ScreenInfo[]): Array<ScreenInfo & { widthMm: number; heightMm: number }> {
  return screens.filter((s): s is ScreenInfo & { widthMm: number; heightMm: number } => !!s.widthMm && !!s.heightMm);
}

/** The form with the screen's size taken from its source. */
export function withScreen(form: PlanForm, screens: readonly ScreenInfo[]): PlanForm {
  const sized = sizedScreens(screens);
  if (form.source === 'detected' && sized[form.screen])
    return { ...form, input: { ...form.input, screen: { widthMm: sized[form.screen].widthMm, heightMm: sized[form.screen].heightMm } } };
  if (form.source === 'diagonal') return { ...form, input: { ...form.input, screen: screenFromDiagonal(form.diagonal, form.aspect) } };
  return form;
}

const CORNERS: Array<{ value: CornerJoin; label: string; title: string }> = [
  { value: 'connector', label: 'Corner connectors', title: 'Clip-on L-shaped connectors: no soldering' },
  { value: 'wire', label: 'Soldered wires', title: 'Short wires soldered between the pieces' },
  { value: 'fold', label: 'Folded', title: 'The strip folded at 45° to turn the corner, no cuts' },
];

/** Inputs, drawing, power and checklist of "Plan your strips". */
export function PlanStep({ form, plan, screens, onChange }: { form: PlanForm; plan: StripPlan | null; screens: readonly ScreenInfo[]; onChange(form: PlanForm): void }) {
  const [fromBehind, setFromBehind] = useState(false);
  const [printing, setPrinting] = useState(false);
  const input = form.input;
  const set = (patch: Partial<PlanInput>) => onChange(withScreen({ ...form, input: normalizeInput({ ...input, ...patch }) }, screens));
  const setForm = (patch: Partial<PlanForm>) => onChange(withScreen({ ...form, ...patch }, screens));
  const preset = presetById(input.preset) ?? STRIP_PRESETS[0];
  const sized = sizedScreens(screens);
  const supply = input.supply;


  const sourceOptions = [
    ...sized.map((s, i) => ({ value: `detected:${i}`, label: `${s.name} (${diagonalInches(s)}″)` })),
    { value: 'diagonal', label: 'Enter the diagonal' },
    { value: 'size', label: 'Enter width and height' },
  ];
  const sourceValue = form.source === 'detected' ? `detected:${form.screen}` : form.source;

  return (
    <div className="space-y-5">
      <div className="grid gap-6 md:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]">
        <div className="space-y-4">
          <Select
            label="Screen"
            value={sourceValue}
            options={sourceOptions}
            hint={sized.length ? 'Its size as the monitor reports it.' : undefined}
            onChange={(v) => (v.startsWith('detected:') ? setForm({ source: 'detected', screen: Number(v.slice(9)) }) : setForm({ source: v as PlanForm['source'] }))}
          />
          {form.source === 'diagonal' && (
            <div className="flex flex-wrap items-end gap-3">
              <NumberField label="Diagonal" unit="inches" value={form.diagonal} min={10} max={120} onChange={(d) => d >= 10 && d <= 120 && setForm({ diagonal: d })} />
              <Segmented<AspectId> label="Aspect ratio" value={form.aspect} options={ASPECTS.map((a) => ({ value: a.id, label: a.id }))} onChange={(aspect) => setForm({ aspect })} size="sm" />
            </div>
          )}
          {form.source === 'size' && (
            <div className="flex flex-wrap gap-3">
              <NumberField
                label="Width"
                unit="cm"
                value={Math.round(input.screen.widthMm) / 10}
                min={10}
                max={400}
                onChange={(cm) => cm > 0 && set({ screen: { ...input.screen, widthMm: Math.round(cm * 10) } })}
              />
              <NumberField
                label="Height"
                unit="cm"
                value={Math.round(input.screen.heightMm) / 10}
                min={10}
                max={250}
                onChange={(cm) => cm > 0 && set({ screen: { ...input.screen, heightMm: Math.round(cm * 10) } })}
              />
            </div>
          )}

          <div className="space-y-1.5">
            <p className="text-sm font-medium">Where the strip goes</p>
            <Segmented<'3' | '4'>
              label="Where the strip goes"
              value={String(input.sides) as '3' | '4'}
              options={[
                { value: '3', label: 'Left, top, right' },
                { value: '4', label: 'All four sides' },
              ]}
              onChange={(v) => set({ sides: v === '4' ? 4 : 3 })}
            />
            <p className="text-xs text-ink-faint">{input.sides === 3 ? 'The usual for a monitor on a stand.' : 'For a wall-mounted screen, or around the stand.'}</p>
          </div>
          {input.sides === 4 && (
            <NumberField
              label="Gap for the stand"
              unit="cm"
              value={Math.round(input.standGapMm) / 10}
              min={0}
              max={100}
              hint="0 for a wall mount."
              onChange={(cm) => cm >= 0 && set({ standGapMm: Math.round(cm * 10) })}
            />
          )}

          <div className="space-y-1.5">
            <p className="text-sm font-medium">Where it starts</p>
            <Segmented
              label="Where it starts"
              size="sm"
              value={input.start}
              options={startOptions(input.sides).map((s) => ({ value: s, label: START_LABELS[s] }))}
              onChange={(start) => set({ start })}
            />
            {input.sides === 4 ? (
              <div className="pt-1">
                <Segmented<'cw' | 'ccw'>
                  label="Direction"
                  size="sm"
                  value={input.clockwise ? 'cw' : 'ccw'}
                  options={[
                    { value: 'cw', label: 'Clockwise' },
                    { value: 'ccw', label: 'Counterclockwise' },
                  ]}
                  onChange={(v) => set({ clockwise: v === 'cw' })}
                />
              </div>
            ) : null}
            <p className="text-xs text-ink-faint">Where the board is: the strip’s data comes in there. Directions as seen from your seat.</p>
          </div>

          <Select
            label="Strip type"
            value={input.preset}
            options={STRIP_PRESETS.map((p) => ({ value: p.id, label: `${p.name} · ${p.volts} V` }))}
            hint={preset.hint}
            onChange={(id) => set({ preset: id, density: presetById(id)?.defaultDensity ?? input.density })}
          />
          <div className="space-y-1.5">
            <p className="text-sm font-medium">LEDs per meter</p>
            <Segmented
              label="LEDs per meter"
              size="sm"
              value={String(input.density)}
              options={preset.densities.map((d) => ({ value: String(d), label: String(d) }))}
              onChange={(d) => set({ density: Number(d) })}
            />
            <p className="text-xs text-ink-faint">As printed on the reel{preset.groupOf > 1 ? `, counting each group of ${preset.groupOf} as one` : ''}.</p>
          </div>
          <div className="space-y-1.5">
            <p className="text-sm font-medium">Corners</p>
            <Segmented<CornerJoin> label="Corners" size="sm" value={input.corners} options={CORNERS} onChange={(corners) => set({ corners })} />
          </div>

          <Advanced label="More options" changed={(input.insetMm !== DEFAULT_PLAN_INPUT.insetMm ? 1 : 0) + (supply ? 1 : 0)} onReset={() => set({ insetMm: DEFAULT_PLAN_INPUT.insetMm, supply: null })}>
            <NumberField
              label="Distance from the edge"
              unit="cm"
              value={input.insetMm / 10}
              min={0}
              max={20}
              hint="How far inside the screen’s edge the strip runs on its back."
              onChange={(cm) => cm >= 0 && cm <= 20 && set({ insetMm: Math.round(cm * 10) })}
            />
            <Toggle
              label="I have a power supply already"
              hint="Checks that it fits the strip."
              checked={!!supply}
              onChange={(on) => set({ supply: on ? { volts: preset.volts, amps: 4 } : null })}
            />
            {supply && (
              <div className="flex flex-wrap items-end gap-3">
                <Segmented
                  label="Supply volts"
                  size="sm"
                  value={String(supply.volts)}
                  options={['5', '12', '24'].map((v) => ({ value: v, label: `${v} V` }))}
                  onChange={(v) => set({ supply: { ...supply, volts: Number(v) } })}
                />
                <NumberField label="Current" unit="A" value={supply.amps} min={0.5} max={100} onChange={(a) => a > 0 && set({ supply: { ...supply, amps: a } })} />
              </div>
            )}
          </Advanced>
        </div>

        <div className="space-y-3">
          {plan ? (
            <>
              <PlanDrawing plan={plan} fromBehind={fromBehind} />
              <div className="flex flex-wrap items-center justify-between gap-2">
                <Segmented<'front' | 'back'>
                  label="Seen from"
                  size="sm"
                  value={fromBehind ? 'back' : 'front'}
                  options={[
                    { value: 'front', label: 'From your seat' },
                    { value: 'back', label: 'From behind' },
                  ]}
                  onChange={(v) => setFromBehind(v === 'back')}
                />
              </div>
              <PlanLegend fromBehind={fromBehind} />
              <PlanSummary plan={plan} />
            </>
          ) : (
            <Callout tone="warn">The screen is too small for a strip plan: check its size.</Callout>
          )}
        </div>
      </div>

      {plan && (
        <section aria-labelledby="plan-checklist" className="space-y-3 border-t border-line pt-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 id="plan-checklist" className="text-sm font-semibold">
              Checklist
            </h3>
            <Button size="sm" icon={<Printer size={15} />} onClick={() => setPrinting(true)}>
              Print the plan
            </Button>
          </div>
          <Checklist items={plan.checklist} />
        </section>
      )}
      {printing && plan && (
        <PrintArea onDone={() => setPrinting(false)}>
          <PrintedPlan plan={plan} />
        </PrintArea>
      )}
    </div>
  );
}

/** LED count, strip length and power, with what needs attention. */
export function PlanSummary({ plan }: { plan: StripPlan }) {
  const s = plan.suggestedSupply;
  const supply = plan.input.supply;
  return (
    <div className="space-y-2">
      <dl className="divide-y divide-line text-sm">
        <Row label="LEDs">
          {plan.totalLeds} on {plan.pieces.length} pieces, {(plan.stripMm / 1000).toFixed(2)} m of strip
        </Row>
        <Row label="Full white">
          {plan.fullWhiteA.toFixed(1)} A at {plan.preset.volts} V ({Math.round(plan.fullWhiteA * plan.preset.volts)} W)
        </Row>
        <Row label="Power supply">
          {supply ? `Yours: ${supply.volts} V, ${supply.amps} A` : supplyText(s)}
          <span className="block text-xs text-ink-faint">The board keeps the LEDs under {(plan.limitMa / 1000).toFixed(1)} A.</span>
        </Row>
        <Row label="Power feeds">
          {plan.feeds.length <= 1 ? 'At the start only' : plan.feeds.map((f) => f.where).join('; ')}
        </Row>
      </dl>
      {plan.warnings.map((w) => (
        <Callout key={w.text} tone={w.tone}>
          {w.text}
        </Callout>
      ))}
    </div>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[7.5rem_1fr] gap-2 py-1.5">
      <dt className="text-ink-muted">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </div>
  );
}

/** The checklist, grouped: parts, cuts, connections. */
export function Checklist({ items, print = false }: { items: readonly ChecklistItem[]; print?: boolean }) {
  const groups = ['Parts', 'Cut', 'Connect'] as const;
  return (
    <div className={print ? 'space-y-3' : 'grid gap-4 md:grid-cols-3'}>
      {groups.map((g) => (
        <div key={g}>
          <p className={print ? 'mb-1 font-semibold' : 'mb-1.5 text-xs font-semibold uppercase tracking-wide text-ink-faint'}>{g === 'Connect' ? 'Connect' : g}</p>
          <ul className="space-y-1 text-sm">
            {items
              .filter((i) => i.group === g)
              .map((i) => (
                <li key={i.text} className="flex gap-2">
                  <span aria-hidden className={print ? '' : 'text-ink-faint'}>
                    ☐
                  </span>
                  <span>{i.text}</span>
                </li>
              ))}
          </ul>
        </div>
      ))}
    </div>
  );
}

/** What "Print the plan" prints: the drawing from behind (where the strip is stuck on) and the checklist. */
function PrintedPlan({ plan }: { plan: StripPlan }) {
  return (
    <div style={{ padding: '12mm', maxWidth: '180mm' }}>
      <h1 style={{ fontSize: '16pt', fontWeight: 600, marginBottom: '4mm' }}>Strip plan: {plan.totalLeds} LEDs</h1>
      <div style={{ maxWidth: '150mm' }}>
        <PlanDrawing plan={plan} fromBehind print />
      </div>
      <p style={{ fontSize: '9pt', margin: '2mm 0 6mm' }}>Seen from behind the screen, where you stick the strip on. Arrows: the direction the data runs. The list names corners as seen from your seat.</p>
      <Checklist items={plan.checklist} print />
    </div>
  );
}
