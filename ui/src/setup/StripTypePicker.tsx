import { useState, type FormEvent } from 'react';
import { Button, ChoiceCard, Segmented } from '../components/ui';
import { Input } from '../components/form';
import { STRIP_CHOICES, choiceOf, guessStripType, parseListing, type StripWires } from './stripTypes';
import type { Volts } from './plan';

/**
 * The strip type (a required answer, nothing preselected): the types in buyers' words with how to recognize each,
 * "Not sure?" (three questions answered by looking at the strip) and the product name from an order.
 */
export function StripTypePicker({
  index,
  value,
  onChange,
  sameAs,
}: {
  index: number;
  value: string | null;
  /** The type, and the LEDs per meter when a product name told them. */
  onChange(id: string, density?: number | null): void;
  /** The first strip's type, offered to the others ("Same as strip 1"). */
  sameAs?: string | null;
}) {
  const [mode, setMode] = useState<'pick' | 'ask' | 'paste'>('pick');
  // the three questions start unanswered, like the cards: no nudge toward the most common type
  const [volts, setVolts] = useState<Volts | null>(null);
  const [wires, setWires] = useState<StripWires | null>(null);
  const [white, setWhite] = useState<boolean | null>(null);
  const [name, setName] = useState('');
  const [found, setFound] = useState<string | null>(null);
  const [changing, setChanging] = useState(false);

  const ask = () => {
    if (volts === null || wires === null || white === null) return;
    const g = guessStripType({ volts, wires, white });
    setChanging(false);
    onChange(g.id);
    setFound(`${choiceOf(g.id)?.title}.${g.note ? ` ${g.note}` : ''}`);
    setMode('pick');
  };
  const paste = (e?: FormEvent) => {
    e?.preventDefault();
    const m = parseListing(name);
    if (!m.id) {
      setFound(m.note ?? 'The name doesn’t say which strip it is. Try the three questions instead.');
      return;
    }
    setChanging(false);
    onChange(m.id, m.density);
    setFound(`${choiceOf(m.id)?.title}${m.density ? `, ${m.density} LEDs/m` : ''}.${m.note ? ` ${m.note}` : ''}`);
    setMode('pick');
  };

  const chosen = choiceOf(value);
  // chosen: one line (several strips stay short), Change opens the list again
  if (chosen && !changing)
    return (
      <div className="space-y-1">
        <p className="text-sm font-medium">Strip type</p>
        <p className="flex flex-wrap items-center gap-x-2 text-sm">
          <span className="font-medium">{chosen.title}</span>
          <span className="text-ink-faint">{chosen.recognize}</span>
          <Button size="sm" variant="ghost" onClick={() => setChanging(true)}>
            Change
          </Button>
        </p>
        {found && <p className="text-xs text-ink-muted">{found}</p>}
      </div>
    );
  const pick = (id: string, density?: number | null) => {
    setChanging(false);
    onChange(id, density);
  };

  return (
    <div className="space-y-2">
      <p className="text-sm font-medium">Strip type</p>
      <div role="radiogroup" aria-label={`Type of strip ${index + 1}`} className="grid gap-2 sm:grid-cols-2">
        {STRIP_CHOICES.map((c) => (
          <ChoiceCard
            key={c.id}
            active={value === c.id}
            title={
              <>
                {c.title}
                {c.mostCommon && <span className="ml-1.5 text-xs font-normal text-ink-faint">most common</span>}
              </>
            }
            description={
              <>
                {c.recognize}
                <span className="block">{c.aliases}</span>
              </>
            }
            onClick={() => {
              setFound(null);
              pick(c.id);
            }}
          />
        ))}
      </div>
      <div className="flex flex-wrap gap-1">
        {index > 0 && sameAs && value !== sameAs && (
          <Button size="sm" variant="ghost" onClick={() => pick(sameAs)}>
            Same as strip 1
          </Button>
        )}
        <Button size="sm" variant="ghost" aria-expanded={mode === 'ask'} onClick={() => setMode(mode === 'ask' ? 'pick' : 'ask')}>
          Not sure? Answer three questions
        </Button>
        <Button size="sm" variant="ghost" aria-expanded={mode === 'paste'} onClick={() => setMode(mode === 'paste' ? 'pick' : 'paste')}>
          Or paste the product name from your order
        </Button>
      </div>
      {mode === 'ask' && (
        <div className="space-y-3 rounded-xl bg-surface-2 p-3">
          <div className="space-y-1">
            <p className="text-sm">The volts printed on the strip</p>
            <Segmented<string> label="Volts on the strip" size="sm" value={volts === null ? '' : String(volts)} options={['5', '12', '24'].map((v) => ({ value: v, label: `${v} V` }))} onChange={(v) => setVolts(Number(v) as Volts)} />
          </div>
          <div className="space-y-1">
            <p className="text-sm">The wires (or copper pads) at its start</p>
            <Segmented<StripWires | ''>
              label="Wires at the start"
              size="sm"
              value={wires ?? ''}
              options={[
                { value: '3', label: '3' },
                { value: '4-clock', label: '4, one marked CLK or CI' },
                { value: '4-backup', label: '4, one marked BI or BO' },
              ]}
              onChange={(v) => setWires(v || null)}
            />
          </div>
          <div className="space-y-1">
            <p className="text-sm">Each LED has a yellow-white half</p>
            <Segmented<'no' | 'yes' | ''>
              label="White half"
              size="sm"
              value={white === null ? '' : white ? 'yes' : 'no'}
              options={[
                { value: 'no', label: 'No' },
                { value: 'yes', label: 'Yes' },
              ]}
              onChange={(v) => setWhite(v === 'yes')}
            />
          </div>
          <Button size="sm" disabled={volts === null || wires === null || white === null} onClick={ask}>
            Use this type
          </Button>
        </div>
      )}
      {mode === 'paste' && (
        <form onSubmit={paste} className="rounded-xl bg-surface-2 p-3">
          <Input
            label="Product name"
            placeholder="For example BTF-LIGHTING WS2812B 5V 60LED/m IP30"
            value={name}
            onChange={setName}
            trailing={
              <Button type="submit" size="sm" disabled={!name.trim()} className="shrink-0">
                Find the type
              </Button>
            }
          />
        </form>
      )}
      {found && <p className="text-xs text-ink-muted">{found}</p>}
    </div>
  );
}
