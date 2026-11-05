import { useCallback } from 'react';
import type { ServerInfo } from '../api/types';
import { useConfig } from '../app/config';
import { useConnection } from '../app/connection';
import { useToast } from '../app/toast';

/** Music effects can run now (sound input is on). Older backends don't say; they run them without it. */
export function musicAvailable(serverInfo: ServerInfo | null | undefined): boolean {
  return serverInfo?.sound ? serverInfo.sound.available !== false : true;
}

/**
 * Makes music effects work: when sound input is off, it is turned on (the `soundEffect.enable` setting, stored), with a
 * "Sound input turned on" toast: picking music means the user wants it to listen.
 */
export function useSoundForMusic(): () => Promise<void> {
  const { api, serverInfo } = useConnection();
  const { config } = useConfig();
  const toast = useToast();
  return useCallback(async () => {
    if (musicAvailable(serverInfo)) return;
    await api.setConfig({ soundEffect: { ...(config?.soundEffect as object | undefined), enable: true } });
    toast('ok', 'Sound input turned on');
  }, [api, serverInfo, config?.soundEffect, toast]);
}

/** Starts a music effect (sound input turned on first, useSoundForMusic). */
export function useStartMusic(): (name: string) => Promise<void> {
  const { api } = useConnection();
  const sound = useSoundForMusic();
  return useCallback(
    async (name: string) => {
      await sound();
      await api.runEffect(name);
    },
    [api, sound],
  );
}
