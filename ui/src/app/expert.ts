import { useSyncExternalStore } from 'react';

// "Show expert and diagnostic tools" (Settings → General). A preference of this browser, not a setting of the
// backend: it only decides what the UI shows. Off by default; see docs/ui-guidelines.md for what it covers.

const KEY = 'backbloom.expertTools';
const listeners = new Set<() => void>();

function read(): boolean {
  try {
    return window.localStorage.getItem(KEY) === '1';
  } catch {
    return false;
  }
}

export function setExpertTools(on: boolean): void {
  try {
    if (on) window.localStorage.setItem(KEY, '1');
    else window.localStorage.removeItem(KEY);
  } catch {
    // Storage blocked: the switch still works for this page view.
  }
  current = on;
  listeners.forEach((l) => l());
}

let current = typeof window === 'undefined' ? false : read();

function subscribe(l: () => void): () => void {
  listeners.add(l);
  const onStorage = (e: StorageEvent) => {
    if (e.key === KEY) {
      current = read();
      l();
    }
  };
  window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(l);
    window.removeEventListener('storage', onStorage);
  };
}

/** Whether expert and diagnostic tools are shown. */
export function useExpertTools(): boolean {
  return useSyncExternalStore(subscribe, () => current, () => false);
}
