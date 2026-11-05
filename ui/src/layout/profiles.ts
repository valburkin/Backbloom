import type { LayoutSpec, PathPoint, Profile, Section, Strip } from './types';

// Profiles: other roles for the strips while a program runs. The backend applies them the same way
// (LayoutSpecification.ApplyProfile) before compiling.

/** A strip with a profile's sections, or the strip itself when the profile doesn't change it (see Profile.sections). */
export function stripInProfile(strip: Strip, profile: Profile | undefined): Strip {
  const sections = profile?.sections[strip.id];
  if (!sections || sections.length !== strip.path.length - 1) return strip;
  const path: PathPoint[] = strip.path.map((p, i) => {
    if (i >= sections.length || p.next?.kind === 'gap') return p;
    const section = sections[i];
    if (section?.kind === 'gap') return p;
    const { next: _own, ...rest } = p;
    return section ? { ...rest, next: section } : rest;
  });
  return { ...strip, path };
}

/** The layout as a profile has it (the profile with that id; the layout itself for null or an unknown id). */
export function applyProfile(spec: LayoutSpec, profileId: string | null): LayoutSpec {
  const profile = profileId === null ? undefined : spec.profiles?.find((p) => p.id === profileId);
  if (!profile) return spec;
  return { ...spec, strips: spec.strips.map((s) => stripInProfile(s, profile)) };
}

/**
 * The profile with a strip's sections taken from `path` (the strip's path as edited in the profile: what each point's
 * `next` says; none = 'auto'). Gaps belong to the strip, so they are stored as null (a profile can't hold a gap, and
 * stripInProfile keeps the strip's gaps anyway).
 */
export function withStripSections(profile: Profile, stripId: string, path: readonly PathPoint[]): Profile {
  const sections = path.slice(0, -1).map((p): Section | null => (p.next && p.next.kind !== 'gap' ? p.next : null));
  return { ...profile, sections: { ...profile.sections, [stripId]: sections } };
}

/** The id the next added profile gets: "profile-1", "profile-2"… (the first one not taken). */
export function nextProfileId(spec: Pick<LayoutSpec, 'profiles'>): string {
  const taken = new Set((spec.profiles ?? []).map((p) => p.id));
  let n = 1;
  while (taken.has(`profile-${n}`)) n++;
  return `profile-${n}`;
}

/** A program name as profiles store it: the executable's file name, lower case, with ".exe" added when missing. */
export function programName(text: string): string {
  const name = text.trim().split(/[\\/]/).pop()?.toLowerCase() ?? '';
  return !name || name.includes('.') ? name : `${name}.exe`;
}

/** The first profile (in list order) one of whose programs is in `running` (executable names, any case). */
export function activeProfile(spec: LayoutSpec, running: readonly string[]): Profile | undefined {
  const set = new Set(running.map((r) => r.toLowerCase()));
  return spec.profiles?.find((p) => p.apps.some((a) => set.has(a.toLowerCase())));
}
