import type { ConnectionStatus, RpcEvent } from './types';

/** Shared listener bookkeeping for Backend implementations. */
export class BackendEvents {
  private statusListeners = new Set<(s: ConnectionStatus) => void>();
  private eventListeners = new Map<string, Set<(e: RpcEvent) => void>>();

  onStatus(listener: (s: ConnectionStatus) => void): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }

  onEvent(command: string, listener: (e: RpcEvent) => void): () => void {
    let set = this.eventListeners.get(command);
    if (!set) this.eventListeners.set(command, (set = new Set()));
    set.add(listener);
    return () => set.delete(listener);
  }

  emitStatus(status: ConnectionStatus): void {
    for (const l of [...this.statusListeners]) l(status);
  }

  emitEvent(event: RpcEvent): void {
    for (const key of [event.command, '*']) {
      const set = this.eventListeners.get(key);
      if (set) for (const l of [...set]) l(event);
    }
  }
}
