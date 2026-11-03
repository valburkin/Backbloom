import { useState } from 'react';
import { ArrowRight, CheckCircle2 } from 'lucide-react';
import { Button } from '../components/ui';
import { useConnection } from '../app/connection';
import { Busy, ErrorText, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';
import { SupplyPicker } from './SupplyPicker';
import { supplyLimitMa, type SupplyChoice } from './wled';

/**
 * The power supply, once per board (a required answer), before any strip lights up: the board's current limit is
 * written right away (`wled-write` with only `maxCurrent`), so the strips found next stay within it.
 */
export function PowerStep({
  frame,
  host,
  supply,
  flashed,
  onChange,
  onNext,
}: {
  frame: StepFrame;
  host: string;
  supply: SupplyChoice | null;
  /** The board's lights just flashed (it joined the Wi-Fi from here): say that's the one. */
  flashed: boolean;
  onChange(s: SupplyChoice): void;
  onNext(): void;
}) {
  const { api } = useConnection();
  const life = useStepLife();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ready = supply?.kind === 'usb' || (supply?.kind === 'printed' && supply.amps > 0);

  const next = async () => {
    if (!ready || busy || !supply) return;
    setBusy(true);
    setError(null);
    try {
      await api.wledWrite(host, { maxCurrent: supplyLimitMa(supply) });
      if (life.alive()) onNext();
    } catch (e) {
      if (life.alive()) setError(errorMessage(e));
    } finally {
      if (life.alive()) setBusy(false);
    }
  };

  return frame({
    backDisabled: busy,
    actions: (
      <Button variant="primary" disabled={!ready || busy} onClick={() => void next()}>
        Next <ArrowRight size={16} />
      </Button>
    ),
    children: (
      <>
        {flashed && (
          <p role="status" className="flex items-start gap-2 text-sm">
            <CheckCircle2 size={16} className="mt-0.5 shrink-0 text-ok" aria-hidden /> Your board’s lights just flashed: that’s the one.
          </p>
        )}
        <StepTitle title="The power supply">The strips get their power from it. Backbloom keeps them within what it can give.</StepTitle>
        <SupplyPicker value={supply} onChange={onChange} />
        {busy && <Busy>Saving to the board…</Busy>}
        {error && <ErrorText>{error}</ErrorText>}
      </>
    ),
  });
}
