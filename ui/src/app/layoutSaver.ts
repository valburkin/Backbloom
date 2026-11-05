import type { LayoutSpec } from '../layout';

/**
 * A change to save: the layout to save (an edited layout as a whole), or what to do to the layout saved last (a pick or
 * a Look written into the setup in use, made while the editor may hold other edits that aren't saved). It may throw
 * when it can't be made (the message is shown).
 */
export type LayoutChange = LayoutSpec | ((saved: LayoutSpec) => LayoutSpec);

export interface SaveQueueDeps<R extends { spec: LayoutSpec }> {
  /** The layout saved last, what a change given as a function starts from. */
  saved(): LayoutSpec;
  /** Why the layout can't be saved (not signed in, problems), or null. */
  blocked(spec: LayoutSpec): string | null;
  /** Saves it (and makes the result's `spec` the layout saved last). */
  save(spec: LayoutSpec): Promise<R>;
  /** Tells the user what failed. */
  report(title: string, message: string): void;
}

export interface SaveQueue<R> {
  /**
   * Saves a change after the one being saved: changes that come while one is saved are saved together once it is done,
   * made one after the other, newest last (one that throws is left out and reported; the others are saved). Resolves
   * with the result of the save that had it, or null when it wasn't saved (reported, unless `quietIfBlocked` and the
   * layout just can't be saved as it is).
   */
  save(change: LayoutChange, options?: { quietIfBlocked?: boolean }): Promise<R | null>;
  /** Resolves once nothing is waiting to be saved. */
  idle(): Promise<void>;
}

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

interface Waiting<R> {
  change: (saved: LayoutSpec) => LayoutSpec;
  reportBlocked: boolean;
  resolve: (result: R | null) => void;
}

/** One queue for every layout save of the app (the editor has it), so saves never overlap or land out of order. */
export function createSaveQueue<R extends { spec: LayoutSpec }>(deps: SaveQueueDeps<R>): SaveQueue<R> {
  let waiting: Array<Waiting<R>> = [];
  let running: Promise<void> | null = null;

  const run = async () => {
    while (waiting.length) {
      const batch = waiting;
      waiting = [];
      // each change on its own: one that can't be made fails alone
      let spec = deps.saved();
      const made: Array<Waiting<R>> = [];
      for (const w of batch) {
        try {
          spec = w.change(spec);
          made.push(w);
        } catch (e) {
          deps.report('Couldn’t save the change', errorText(e));
          w.resolve(null);
        }
      }
      if (!made.length) continue;
      let result: R | null = null;
      const why = deps.blocked(spec);
      if (why) {
        if (made.some((w) => w.reportBlocked)) deps.report('Couldn’t save the change', why);
      } else {
        try {
          result = await deps.save(spec);
        } catch (e) {
          deps.report('Couldn’t save the layout', errorText(e));
        }
      }
      for (const w of made) w.resolve(result);
    }
  };

  return {
    save(change, options) {
      const result = new Promise<R | null>((resolve) =>
        waiting.push({ change: typeof change === 'function' ? change : () => change, reportBlocked: !options?.quietIfBlocked, resolve }),
      );
      if (!running)
        running = run().finally(() => {
          running = null;
        });
      return result;
    },
    idle() {
      return running ?? Promise.resolve();
    },
  };
}
