import { useCallback, useState } from 'react';
import { saveErrorText } from './config';
import { useToast } from './toast';

/**
 * Runs a one-off backend action (start an effect, toggle a component...) and reports failures as a toast.
 * `busy` holds the key of the action in flight so buttons can show progress.
 */
export function useAction() {
  const toast = useToast();
  const [busy, setBusy] = useState<string | null>(null);
  const run = useCallback(
    async (key: string, failTitle: string, fn: () => Promise<unknown>, okTitle?: string): Promise<boolean> => {
      setBusy(key);
      try {
        await fn();
        if (okTitle) toast('ok', okTitle);
        return true;
      } catch (e) {
        toast('bad', failTitle, saveErrorText(e));
        return false;
      } finally {
        setBusy((b) => (b === key ? null : b));
      }
    },
    [toast],
  );
  return { run, busy };
}
