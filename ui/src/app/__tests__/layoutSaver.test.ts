import { afterEach, describe, expect, it, vi } from 'vitest';
import { defaultRoom, type LayoutSpec, type PathPoint, type Section, type Strip } from '../../layout';
import { editorReducer, initialEditorState, type EditorAction } from '../../editor/state';
import { undoPick, writePick } from '../../control/setup';
import { whatPlays } from '../../control/presets';
import { createSaveQueue } from '../layoutSaver';
import { OwnEdits } from '../ownEdits';

const at = (x: number, next?: Section): PathPoint => ({ surface: 'front', x, y: 1, ...(next ? { next } : {}) });
const strip = (id: string, sections: Array<Section | undefined>): Strip => ({
  id,
  name: id,
  controllerId: 'c',
  output: 0,
  ledCount: 30,
  path: [...sections.map((s, i) => at(i * 0.3, s)), at(sections.length * 0.3)],
});
const base: LayoutSpec = {
  version: 1,
  room: defaultRoom(),
  controllers: [{ id: 'c', name: 'C', kind: 'ddp', host: '', outputs: [{ index: 0, start: 0, length: 30 }] }],
  strips: [strip('a', [undefined, undefined])],
};
const edits = (s: LayoutSpec, actions: EditorAction[]) => actions.reduce((st, a) => editorReducer(st, a), initialEditorState(s)).spec;

/** A backend that saves when told to (`finish`), and the queue in front of it. */
function harness(blocked: (spec: LayoutSpec) => string | null = () => null) {
  let saved = base;
  const sent: LayoutSpec[] = [];
  const reports: string[] = [];
  let release: Array<(fail?: boolean) => void> = [];
  const queue = createSaveQueue<{ spec: LayoutSpec }>({
    saved: () => saved,
    blocked,
    save: (spec) =>
      new Promise((resolve, reject) => {
        sent.push(spec);
        release.push((fail) => {
          if (fail) return reject(new Error('Backend gone'));
          saved = spec;
          resolve({ spec });
        });
      }),
    report: (title, message) => reports.push(`${title}: ${message}`),
  });
  const finish = async (fail = false) => {
    const r = release;
    release = [];
    for (const f of r) f(fail);
    await new Promise((r) => setTimeout(r, 0));
  };
  return { queue, sent, reports, finish, saved: () => saved };
}

describe('the one layout save queue', () => {
  it('saves one at a time; changes that come meanwhile save together afterwards, the newest last', async () => {
    const h = harness();
    const one = { ...base, strips: [strip('a', [{ kind: 'whole' }, undefined])] };
    const first = h.queue.save(one);
    const second = h.queue.save((s) => ({ ...s, profiles: [] }));
    const third = h.queue.save((s) => ({ ...s, profiles: [...(s.profiles ?? []), { id: 'p', name: 'P', apps: [], sections: {} }] }));
    expect(h.sent).toHaveLength(1);
    await h.finish();
    // the second and the third, made to the layout the first saved
    expect(h.sent).toHaveLength(2);
    expect(h.sent[1].strips).toBe(one.strips);
    expect(h.sent[1].profiles?.map((p) => p.id)).toEqual(['p']);
    await h.finish();
    expect(await first).toEqual({ spec: one });
    expect((await second)?.spec).toBe(h.sent[1]);
    expect(await third).toEqual(await second);
    await h.queue.idle();
  });

  it('reports a failure, and what can’t be saved as it is (quietly when asked)', async () => {
    const h = harness((spec) => (spec.controllers.length ? null : 'Add a device first, then save.'));
    const failing = h.queue.save(base);
    await h.finish(true);
    expect(await failing).toBeNull();
    expect(await h.queue.save({ ...base, controllers: [] }, { quietIfBlocked: true })).toBeNull();
    expect(await h.queue.save({ ...base, controllers: [] })).toBeNull();
    expect(
      await h.queue.save(() => {
        throw new Error('That preset isn’t saved yet.');
      }),
    ).toBeNull();
    expect(h.reports).toEqual([
      'Couldn’t save the layout: Backend gone',
      'Couldn’t save the change: Add a device first, then save.',
      'Couldn’t save the change: That preset isn’t saved yet.',
    ]);
  });

  it('makes the changes saved together one at a time: one that throws fails alone', async () => {
    const h = harness();
    const first = h.queue.save(base);
    const named = h.queue.save((s) => ({ ...s, strips: s.strips.map((x) => ({ ...x, name: 'Desk' })) }));
    const failing = h.queue.save(() => {
      throw new Error('That preset isn’t saved yet.');
    });
    const listed = h.queue.save((s) => ({ ...s, profiles: [] }));
    await h.finish();
    await h.finish();
    expect(await first).not.toBeNull();
    expect(await failing).toBeNull();
    expect(await named).not.toBeNull();
    expect(await listed).toBe(await named);
    expect(h.saved().strips[0].name).toBe('Desk');
    expect(h.saved().profiles).toEqual([]);
    expect(h.reports).toEqual(['Couldn’t save the change: That preset isn’t saved yet.']);
  });

  it('saves a pick to the layout saved last, not the strip being drawn on Layout; a failure takes the edit back', async () => {
    const h = harness();
    // Layout: a strip half drawn, not saved
    const edited = { ...base, strips: [...base.strips, strip('new', [undefined])] };
    const pick = { kind: 'effect', effect: 'Candle' } as const;
    const mirror = writePick(edited, undefined, pick);
    const shown = edits(edited, mirror.actions);
    const done = h.queue.save((s) => writePick(s, undefined, pick).spec);
    await h.finish();
    expect(await done).not.toBeNull();
    expect(h.saved().strips.map((s) => s.id)).toEqual(['a']);
    expect(whatPlays(h.saved())).toEqual({ kind: 'effect', effect: 'Candle' });
    expect(shown.strips.map((s) => s.id)).toEqual(['a', 'new']);
    expect(whatPlays(shown)).toEqual({ kind: 'effect', effect: 'Candle' });
    // the save failed: the edited layout goes back, the strip being drawn stays
    const back = edits(shown, undoPick(shown, mirror.undo).actions);
    expect(back.strips.map((s) => s.path)).toEqual(edited.strips.map((s) => s.path));
  });
});

describe('edits a page saves by itself', () => {
  afterEach(() => vi.useRealTimers());

  it('stays the page’s own while a newer edit came during a save', async () => {
    vi.useFakeTimers();
    const saves: Array<(ok: boolean) => void> = [];
    const own = new OwnEdits(() => new Promise<boolean>((r) => saves.push(r)));
    own.edit();
    own.schedule(base);
    vi.advanceTimersByTime(400);
    expect(saves).toHaveLength(1);
    // renamed again while the first save runs
    own.edit();
    saves[0](true);
    await vi.runAllTimersAsync();
    expect(own.own).toBe(true);
    own.schedule(base);
    vi.advanceTimersByTime(400);
    saves[1](true);
    await vi.runAllTimersAsync();
    expect(own.own).toBe(false);
  });

  it('saves what waits when the page is left, and nothing twice', async () => {
    vi.useFakeTimers();
    const sent: LayoutSpec[] = [];
    const own = new OwnEdits(async (spec) => {
      sent.push(spec);
      return false;
    });
    own.edit();
    own.schedule(base);
    own.flush();
    expect(sent).toEqual([base]);
    await vi.runAllTimersAsync();
    expect(sent).toHaveLength(1);
    // a failed save isn't tried again by itself
    expect(own.failed).toBe(base);
    expect(own.own).toBe(true);
  });
});
