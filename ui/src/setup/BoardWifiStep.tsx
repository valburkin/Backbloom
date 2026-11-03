import { useEffect, useState, type FormEvent } from 'react';
import { Button } from '../components/ui';
import { Input } from '../components/form';
import { useConnection } from '../app/connection';
import type { FoundBoard, HardwareProgressStep } from '../api/types';
import { Busy, ErrorText, StepTitle, errorMessage, useStepLife, type StepFrame } from './parts';

/** What a hotspot join is doing, in words. */
export const PROGRESS_TEXT: Record<HardwareProgressStep, string> = {
  'joining-hotspot': 'Switching this PC to the board’s Wi-Fi…',
  sending: 'Giving the board your Wi-Fi…',
  rejoining: 'Switching this PC back to your Wi-Fi…',
  waiting: 'Waiting for the board on your Wi-Fi…',
};

/**
 * Wi-Fi, for a new board: the network this PC is on (prefilled) and its password; `connect-wifi` gives them to the
 * board over USB, or from its hotspot (this PC joins the board's Wi-Fi for about a minute). Resolves with the board's
 * host on the home Wi-Fi.
 */
export function BoardWifiStep({
  frame,
  board,
  currentSsid,
  savedPassword = false,
  onJoined,
}: {
  frame: StepFrame;
  board: FoundBoard;
  currentSsid: string | null;
  /** Windows has the password of `currentSsid` saved: offered instead of typing it (Backbloom never sees it). */
  savedPassword?: boolean;
  onJoined(host: string): void;
}) {
  const { api, backend } = useConnection();
  const life = useStepLife();
  const [ssid, setSsid] = useState(currentSsid ?? '');
  const [password, setPassword] = useState('');
  const [useSaved, setUseSaved] = useState(savedPassword);
  const saved = useSaved && savedPassword && ssid.trim() === (currentSsid ?? '');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(
    () =>
      backend.onEvent('hardware-progress-update', (e) => {
        const d = e.data as { id?: string; step?: HardwareProgressStep };
        if (life.alive() && d.step && (!d.id || d.id === board.id) && PROGRESS_TEXT[d.step]) setBusy(PROGRESS_TEXT[d.step]);
      }),
    [backend, board.id],
  );

  const connect = async (e?: FormEvent) => {
    e?.preventDefault();
    if (busy || !ssid.trim()) return;
    setBusy('Giving the board your Wi-Fi…');
    setError(null);
    try {
      const joined = await api.connectWifi(board.id, ssid.trim(), saved ? { useSavedPassword: true } : password);
      if (life.alive()) onJoined(joined.host);
    } catch (err) {
      if (life.alive()) setError(errorMessage(err));
    } finally {
      if (life.alive()) setBusy(null);
    }
  };

  return frame({
    backDisabled: !!busy,
    actions: (
      <Button variant="primary" disabled={!!busy || !ssid.trim()} onClick={() => void connect()}>
        {busy ? 'Connecting…' : 'Connect'}
      </Button>
    ),
    children: (
      <>
        <StepTitle title="Put the board on your Wi-Fi">
          {board.via === 'hotspot'
            ? 'This PC switches to the board’s Wi-Fi for about a minute to give it yours, then comes back.'
            : 'Backbloom gives the board your Wi-Fi over the USB cable.'}
        </StepTitle>
        {board.via === 'hotspot' && (
          <p className="text-xs text-ink-faint">
            While this PC is on the board’s Wi-Fi, Windows may open a browser page (msftconnecttest.com) because that network has no internet. You can
            close it.
          </p>
        )}
        <form onSubmit={connect} className="max-w-md space-y-4">
          <Input label="Your Wi-Fi" value={ssid} onChange={setSsid} autoComplete="off" hint="The board works with 2.4 GHz Wi-Fi: a name with 5G is usually 5 GHz only." />
          {savedPassword && ssid.trim() === (currentSsid ?? '') && (
            <label className="flex cursor-pointer items-start gap-3 text-sm">
              <input type="checkbox" className="mt-0.5 h-4 w-4 accent-[var(--color-accent)]" checked={useSaved} onChange={(e) => setUseSaved(e.target.checked)} />
              <span>
                Use the password saved on this PC
                <span className="block text-xs text-ink-faint">Backbloom hands it to the board without showing or keeping it.</span>
              </span>
            </label>
          )}
          {!saved && (
            <Input label="Wi-Fi password" type="password" value={password} onChange={setPassword} autoComplete="off" hint="Sent to the board only: Backbloom doesn’t keep it." />
          )}
          <button type="submit" hidden />
        </form>
        {busy && <Busy>{busy}</Busy>}
        {error && <ErrorText>{error}</ErrorText>}
      </>
    ),
  });
}
