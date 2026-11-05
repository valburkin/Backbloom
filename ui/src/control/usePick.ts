import { useCallback, useEffect, useRef } from 'react';
import { useConnection } from '../app/connection';
import { useEditor } from '../app/editor';
import { useToast } from '../app/toast';
import { saveErrorText, useConfig } from '../app/config';
import type { EditorAction } from '../editor/state';
import { mirrorId } from '../editor/mirror';
import type { LayoutSpec, Profile } from '../layout';
import type { Rgb } from '../color/color';
import { applyLook, LOOKS, profileLook, type LookId } from '../settings/intensity';
import type { WhatPlays } from './presets';
import { useSoundForMusic } from './music';
import { pickNeeds, presetInUse, setupById, setupName, setupPlays, undoPick, withPresetPatch, writePick, type PickWrite } from './setup';
import type { MainPick } from './show';

/** The setup in use: the preset (undefined: Everyday), its name, and what it plays. */
export function useSetupInUse(): { preset: Profile | undefined; name: string; plays: WhatPlays } {
  const { serverInfo } = useConnection();
  const { state } = useEditor();
  const preset = presetInUse(state.spec, serverInfo?.profile);
  return { preset, name: setupName(preset), plays: setupPlays(state.spec, preset) };
}

/** A change to a setup: the edits for the edited layout, and the same change to the layout saved last. */
export interface SetupChange {
  /** The edits that make it in the edited layout (one undo step; a function: made from the edited layout as it is then). */
  actions: EditorAction[] | ((edited: LayoutSpec) => EditorAction[]);
  /** The same change made to the layout saved last (may throw: not saved, reported). */
  change(saved: LayoutSpec): LayoutSpec;
}

/**
 * Makes a change to a setup in the layout saved last and saves it at once (so edits on Layout that aren't saved, such
 * as a strip being drawn, neither block it nor get saved with it), and the same way in the edited layout, so both show
 * it (a mirror: not counted as an unsaved edit while it is saved). When the save fails, what it wrote in the edited
 * layout goes back to the layout saved last (mirrorDone). Resolves to whether it was saved.
 */
export function useSetupChange(): (c: SetupChange) => Promise<boolean> {
  const { dispatch, quietSave } = useEditor();
  return useCallback(
    async ({ actions, change }: SetupChange) => {
      const id = mirrorId();
      dispatch({ type: 'mirror', id, actions, coalesce: id });
      let ok = false;
      try {
        ok = await quietSave(change);
      } finally {
        dispatch({ type: 'mirrorDone', id, saved: ok });
      }
      return ok;
    },
    [dispatch, quietSave],
  );
}

/** Which setup a pick goes to: a preset (its id), or Everyday (null). */
export interface PickTarget {
  preset: string | null;
}

/**
 * Picking a main effect (Effects; the Presets page names the setup, `target`): it is written into the setup (writePick:
 * a preset's sections, or Everyday's strips) of the layout saved last and saved at once, and into the edited layout
 * (useSetupChange); nothing is started for a while and no other setup comes into use. Screen edges and Screen average
 * turn screen sync on when it is off; music turns sound input on (pickNeeds). When the pick replaced strips customized
 * in Layout, a toast says so with Undo (the setup's sections as they were). Resolves to whether it was saved (a
 * failure is reported as a toast, and the edited layout goes back).
 */
export function usePick(): (pick: MainPick, target?: PickTarget) => Promise<boolean> {
  const { api, serverInfo, auth } = useConnection();
  const { state, load } = useEditor();
  const change = useSetupChange();
  const toast = useToast();
  const soundForMusic = useSoundForMusic();
  const latest = useRef(state.spec);
  latest.current = state.spec;
  const info = useRef(serverInfo);
  info.current = serverInfo;

  return useCallback(
    async (pick: MainPick, target?: PickTarget) => {
      if (auth !== 'admin' || load.status !== 'ready') {
        toast('bad', 'Can’t change what plays yet', auth !== 'admin' ? 'Sign in on this PC first.' : 'Your layout is still loading.');
        return false;
      }
      try {
        const presetId = target ? target.preset : (info.current?.profile?.id ?? null);
        const edited = setupById(latest.current, presetId, false);
        const name = setupName(edited);
        const mirror = writePick(latest.current, edited, pick);
        let saved: PickWrite | null = null;
        const ok = await change({
          actions: mirror.actions,
          change: (s) => {
            saved = writePick(s, setupById(s, presetId, true), pick);
            return saved.spec;
          },
        });
        if (!ok) return false;
        const needs = pickNeeds(pick, info.current?.components);
        if (needs.screen) await api.setComponent('SYSTEMGRABBER', true);
        if (needs.sound) await soundForMusic();
        const done = saved as PickWrite | null;
        if (done?.replaced)
          toast('info', `Saved to ${name}`, 'Strips customized in Layout play your pick now; Off strips stay off.', {
            label: 'Undo',
            run: () => {
              void change({
                actions: (edited) => undoPick(edited, mirror.undo).actions,
                change: (s) => undoPick(s, done.undo).spec,
              });
            },
          });
        return true;
      } catch (e) {
        toast('bad', 'Couldn’t change what plays', e instanceof Error ? e.message : String(e));
        return false;
      }
    },
    [api, auth, load.status, change, soundForMusic, toast],
  );
}

/**
 * The color picker: while it is dragged (or a hex code typed) the color is sent live as it comes (throttled, a
 * temporary color over the setup), and when the drag ends (or the hex code is committed) it is picked (usePick: saved
 * to the setup in use); then the temporary color is cleared, so the setup shows it.
 */
export function useColorPick(): { live(rgb: Rgb): void; commit(rgb: Rgb): Promise<void> } {
  const { api } = useConnection();
  const pick = usePick();
  const s = useRef<{ last: number; timer: ReturnType<typeof setTimeout> | null; next: Rgb | null; sent: boolean }>({ last: 0, timer: null, next: null, sent: false });
  useEffect(
    () => () => {
      if (s.current.timer) clearTimeout(s.current.timer);
    },
    [],
  );
  const send = useCallback(
    (rgb: Rgb) => {
      s.current.sent = true;
      s.current.last = Date.now();
      void api.setColor(rgb).catch(() => undefined);
    },
    [api],
  );
  const live = useCallback(
    (rgb: Rgb) => {
      const st = s.current;
      st.next = rgb;
      const wait = st.last + 120 - Date.now();
      if (wait <= 0 && !st.timer) {
        st.next = null;
        send(rgb);
        return;
      }
      if (!st.timer)
        st.timer = setTimeout(() => {
          st.timer = null;
          if (st.next) send(st.next);
          st.next = null;
        }, Math.max(0, wait));
    },
    [send],
  );
  const commit = useCallback(
    async (rgb: Rgb) => {
      const st = s.current;
      if (st.timer) clearTimeout(st.timer);
      st.timer = null;
      st.next = null;
      await pick({ kind: 'color', color: [rgb[0], rgb[1], rgb[2]] });
      if (st.sent) {
        st.sent = false;
        await api.clear().catch(() => undefined);
      }
    },
    [api, pick],
  );
  return { live, commit };
}

/**
 * Picking one of the five Looks for the setup in use: Everyday's is the saved `smoothing` and `color` (setconfig), a
 * preset's is its own `look`, saved with the layout (useSetupChange). Resolves to whether it was saved.
 */
export function useLookPick(): (id: LookId) => Promise<boolean> {
  const { api } = useConnection();
  const { config } = useConfig();
  const { preset } = useSetupInUse();
  const change = useSetupChange();
  const toast = useToast();
  return useCallback(
    async (id: LookId) => {
      const label = LOOKS.find((l) => l.id === id)?.label;
      if (preset) {
        const look = profileLook(id);
        const ok = await change({
          actions: [{ type: 'updateProfile', profileId: preset.id, patch: { look } }],
          change: (s) => withPresetPatch(s, preset.id, { look }),
        });
        if (ok) toast('ok', `Look: ${label}`, `Saved to ${setupName(preset)}.`);
        return ok;
      }
      if (!config) return false;
      try {
        await api.setConfig(applyLook(config, id));
        toast('ok', `Look: ${label}`);
        return true;
      } catch (e) {
        toast('bad', 'Couldn’t change the Look', saveErrorText(e));
        return false;
      }
    },
    [api, config, preset, change, toast],
  );
}
