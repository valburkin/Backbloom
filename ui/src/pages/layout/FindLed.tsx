import { useEffect, useRef, useState } from 'react';
import { Crosshair, X } from 'lucide-react';
import type { LayoutSpec } from '../../layout';
import { IconButton, Stepper } from '../../components/ui';
import { Select } from '../../components/form';
import { useConnection } from '../../app/connection';
import { locateLed } from './model';

export const BLINK_DEBOUNCE_MS = 350;

/**
 * "Find an LED" (an expert tool): pick a device and an LED number on it (from 1), and that LED blinks red → green →
 * blue, so users can match physical positions ("LED 17 is the bottom-right corner"). Blink commands are debounced
 * while dragging; the LED goes off when the panel closes.
 */
export function FindLed({
  spec,
  controllerId,
  onLocate,
  onClose,
}: {
  spec: LayoutSpec;
  controllerId: string | null;
  onLocate: (loc: { stripId: string; local: number } | null) => void;
  onClose(): void;
}) {
  const { api } = useConnection();
  // Follows the selected strip's device until the user picks one here.
  const [picked, setPicked] = useState<string | null>(null);
  const controller =
    spec.controllers.find((c) => c.id === picked) ?? spec.controllers.find((c) => c.id === controllerId) ?? spec.controllers[0];
  const total = controller ? controller.outputs.reduce((m, o) => Math.max(m, o.start + o.length), 0) : 0;
  /** The LED, counted from 0 as the device does. */
  const [index, setIndex] = useState(0);
  const [active, setActive] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const loc = controller && active ? locateLed(spec, controller.id, index) : null;
  const locKey = loc ? `${loc.strip.id}:${loc.local}` : '';
  useEffect(() => {
    onLocate(loc ? { stripId: loc.strip.id, local: loc.local } : null);
  }, [locKey]);

  // The LED stays lit until another one is picked or the panel closes.
  const activeRef = useRef(false);
  activeRef.current = active;
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      if (activeRef.current) api.stopBlink().catch(() => undefined);
      onLocate(null);
    },
    [api],
  );

  // Another device (picked here, or the selected strip's): the LED lit on the previous one goes off.
  const controllerKey = controller?.id;
  const litOn = useRef(controllerKey);
  useEffect(() => {
    if (litOn.current === controllerKey) return;
    litOn.current = controllerKey;
    if (timer.current) clearTimeout(timer.current);
    if (activeRef.current) api.stopBlink().catch(() => undefined);
    setActive(false);
    setIndex(0);
  }, [controllerKey, api]);

  const blink = (i: number) => {
    if (timer.current) clearTimeout(timer.current);
    const id = controller?.id;
    timer.current = setTimeout(() => {
      setError(null);
      api.blinkLed(i, id, true).catch((e) => setError(e instanceof Error ? e.message : String(e)));
    }, BLINK_DEBOUNCE_MS);
  };

  const change = (i: number) => {
    const clamped = Math.max(0, Math.min(total - 1, i));
    setIndex(clamped);
    setActive(true);
    blink(clamped);
  };

  const shown = Math.min(index, Math.max(0, total - 1));
  return (
    <section aria-label="Find an LED" className="space-y-3 rounded-2xl border border-line-strong bg-surface-2 p-4">
      <div className="flex items-center gap-2">
        <Crosshair size={17} className="shrink-0 text-ink-faint" aria-hidden />
        <h2 className="flex-1 text-sm font-semibold">Find an LED</h2>
        <IconButton label="Close Find an LED" onClick={onClose}>
          <X size={16} />
        </IconButton>
      </div>
      {!controller || total === 0 ? (
        <p className="text-[13px] text-ink-muted">There are no LEDs to light up yet.</p>
      ) : (
        <>
          {spec.controllers.length > 1 && (
            <Select
              label="Device"
              value={controller.id}
              options={spec.controllers.map((c) => ({ value: c.id, label: c.name || c.id }))}
              onChange={setPicked}
            />
          )}
          <Stepper label="LED number" value={shown + 1} min={1} max={total} onChange={(n) => change(n - 1)} />
          <input
            type="range"
            aria-label="LED number slider"
            className="w-full"
            min={1}
            max={total}
            value={shown + 1}
            onChange={(e) => change(Number(e.target.value) - 1)}
          />
          <p className="text-xs text-ink-faint" aria-live="polite">
            {error ? (
              <span className="text-bad">Couldn’t light the LED: {error}</span>
            ) : active ? (
              loc ? (
                <>
                  LED {shown + 1}
                  {spec.controllers.length > 1 ? ` of ${controller.name}` : ''} is lit (red → green → blue). It’s LED {loc.local + 1} of “{loc.strip.name}”
                  {loc.local >= loc.strip.ledCount ? ', past the end of the strip' : ', circled in the room'}.
                </>
              ) : (
                <>LED {shown + 1} is lit (red → green → blue). It isn’t part of any strip.</>
              )
            ) : (
              <>Pick a number to light up that LED, handy for finding corners.</>
            )}
          </p>
        </>
      )}
    </section>
  );
}
