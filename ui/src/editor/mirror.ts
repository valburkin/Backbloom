import type { LayoutSpec, Profile, Section } from '../layout';

// Changes saved by themselves (a pick, a Look) are made to the layout saved last and saved at once, and mirrored into
// the edited layout so both show them. What a mirror wrote is kept value by value (touches): while its save waits it
// doesn't count as an unsaved edit, and when the save fails it goes back to the layout saved last, where it is still
// what the mirror wrote.

/** A value a mirrored change wrote into the edited layout, and what it was then (`before`). */
export type Touch =
  | { kind: 'sections'; presetId: string; stripId: string; value: Array<Section | null> | undefined; before: Array<Section | null> | undefined }
  | { kind: 'look'; presetId: string; value: Profile['look']; before: Profile['look'] }
  | { kind: 'part'; stripId: string; part: number; value: Section | undefined; before: Section | undefined };

/** A mirrored change whose save hasn't ended yet. */
export interface Mirror {
  id: string;
  touches: Touch[];
}

let seq = 0;

/** A new mirror id. */
export function mirrorId(): string {
  return `mirror-${++seq}`;
}

/** Deep equality of plain JSON values; key order and keys set to undefined don't matter (as in JSON). */
export function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const other = b as unknown[];
    return a.length === other.length && a.every((v, i) => sameValue(v, other[i]));
  }
  const ao = a as Record<string, unknown>;
  const bo = b as Record<string, unknown>;
  const keys = (o: Record<string, unknown>) => Object.keys(o).filter((k) => o[k] !== undefined);
  const ak = keys(ao);
  return ak.length === keys(bo).length && ak.every((k) => sameValue(ao[k], bo[k]));
}

const keyOf = (t: Touch) => (t.kind === 'part' ? `part:${t.stripId}:${t.part}` : t.kind === 'look' ? `look:${t.presetId}` : `sections:${t.presetId}:${t.stripId}`);

/**
 * What a change wrote (`before` → `after`): the presets' lists per strip and Looks, and what the parts of strips play
 * (presets and strips that both have, strips of the same shape).
 */
export function touchesOf(before: LayoutSpec, after: LayoutSpec): Touch[] {
  const out: Touch[] = [];
  for (const p of after.profiles ?? []) {
    const old = before.profiles?.find((q) => q.id === p.id);
    if (!old || old === p) continue;
    if (!sameValue(old.look, p.look)) out.push({ kind: 'look', presetId: p.id, value: p.look, before: old.look });
    if (old.sections === p.sections) continue;
    for (const stripId of new Set([...Object.keys(old.sections), ...Object.keys(p.sections)]))
      if (!sameValue(old.sections[stripId], p.sections[stripId]))
        out.push({ kind: 'sections', presetId: p.id, stripId, value: p.sections[stripId], before: old.sections[stripId] });
  }
  for (const s of after.strips) {
    const old = before.strips.find((q) => q.id === s.id);
    if (!old || old === s || old.path.length !== s.path.length) continue;
    for (let part = 0; part < s.path.length - 1; part++)
      if (!sameValue(old.path[part].next, s.path[part].next))
        out.push({ kind: 'part', stripId: s.id, part, value: s.path[part].next, before: old.path[part].next });
  }
  return out;
}

/** Touches with newer ones added: a value written again keeps what it was first (`before`) and takes the newer value. */
export function mergeTouches(touches: readonly Touch[], newer: readonly Touch[]): Touch[] {
  const out = [...touches];
  for (const t of newer) {
    const i = out.findIndex((o) => keyOf(o) === keyOf(t));
    if (i < 0) out.push(t);
    else out[i] = { ...t, before: out[i].before } as Touch;
  }
  return out;
}

function withPreset(spec: LayoutSpec, preset: Profile): LayoutSpec {
  return { ...spec, profiles: spec.profiles?.map((p) => (p.id === preset.id ? preset : p)) };
}

/**
 * The edited layout with what `touches` wrote taken back: each value that is still what it wrote goes back to the
 * layout saved last (`saved`), or to what it was before the change where that layout doesn't have it (a preset or a
 * strip that isn't saved, a strip of another shape). Values changed since stay; nothing else is touched. The same
 * layout when there is nothing to take back.
 */
export function revertTouches(spec: LayoutSpec, saved: LayoutSpec | null, touches: readonly Touch[]): LayoutSpec {
  let out = spec;
  for (const t of touches) {
    if (t.kind === 'part') {
      const strip = out.strips.find((s) => s.id === t.stripId);
      const point = strip?.path[t.part];
      if (!strip || !point || t.part >= strip.path.length - 1 || !sameValue(point.next, t.value)) continue;
      const was = saved?.strips.find((s) => s.id === t.stripId);
      const back = was && was.path.length === strip.path.length ? was.path[t.part].next : t.before;
      if (sameValue(back, point.next)) continue;
      const { next: _written, ...rest } = point;
      const path = strip.path.map((p, i) => (i === t.part ? (back ? { ...rest, next: back } : rest) : p));
      out = { ...out, strips: out.strips.map((s) => (s.id === t.stripId ? { ...s, path } : s)) };
      continue;
    }
    const preset = out.profiles?.find((p) => p.id === t.presetId);
    if (!preset) continue;
    const was = saved?.profiles?.find((p) => p.id === t.presetId);
    if (t.kind === 'look') {
      if (!sameValue(preset.look, t.value)) continue;
      const back = was ? was.look : t.before;
      if (sameValue(back, preset.look)) continue;
      const { look: _written, ...rest } = preset;
      out = withPreset(out, back !== undefined ? { ...preset, look: back } : rest);
    } else {
      if (!sameValue(preset.sections[t.stripId], t.value)) continue;
      const back = was ? was.sections[t.stripId] : t.before;
      if (sameValue(back, preset.sections[t.stripId])) continue;
      const { [t.stripId]: _written, ...others } = preset.sections;
      out = withPreset(out, { ...preset, sections: back !== undefined ? { ...preset.sections, [t.stripId]: back } : others });
    }
  }
  return out;
}
