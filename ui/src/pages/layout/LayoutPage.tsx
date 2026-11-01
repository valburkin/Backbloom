import { useCallback, useEffect, useMemo, useState } from 'react';
import { Activity, Crosshair, Info, Loader2, MonitorPlay, RotateCcw, X } from 'lucide-react';
import { applyProfile, withStripSections, type LayoutIssue, type PathPoint, type Room, type ShapeId } from '../../layout';
import { Button, Card, IconButton, Segmented } from '../../components/ui';
import { Expert } from '../../components/form';
import { PageHeader } from '../../components/PageHeader';
import { SaveBar } from '../../components/SaveBar';
import { useConnection } from '../../app/connection';
import { useEditor } from '../../app/editor';
import { IMPORTED_NOTE } from '../../api/layoutService';
import { useExpertTools } from '../../app/expert';
import { lastStepAddedStrip, nextStripId, selectedStrip } from '../../editor/state';
import { mappingJoints } from '../../editor/mapping';
import { RoomCanvas, type FoundLed } from './RoomCanvas';
import { useLedFrame } from '../../live/hooks';
import { StripPanel } from './StripPanel';
import { CustomizeEditor, PlaceEditor } from './StripEditor';
import { MatchLights } from './MatchLights';
import { FindLed } from './FindLed';
import { TestPattern } from './TestPattern';
import { IssueSummary } from './IssueSummary';
import { ProfileIntro, ProfileSettings, ProfileTabs } from './ProfileBar';
import { PlaceGuide, type GuideState } from './PlaceGuide';

/** Live colors before the first frame: every LED unlit. */
const NO_COLORS: readonly number[] = [];

/** Place: where strips really are. Customize: what each part shows. */
export type LayoutMode = 'place' | 'customize';

export const HINTS = {
  idle: 'Select a strip to edit it. Scroll to zoom, drag to look around.',
  place: 'Drag the round handles to move the strip. Click a part of it to change what it shows.',
  draw: 'Click where the strip starts (where the cable comes in), then at each bend. Double-click or press Enter to finish.',
  mapping:
    'Each LED follows the part of the screen next to it (matching colors). Drag the dots on the screen’s edge to adjust. Click a part of the strip to change just that part.',
  parts: 'Click a part of the strip to change just that part.',
} as const;

const FURNITURE: Array<{ value: Room['furniture']; label: string }> = [
  { value: 'desk', label: 'Desk' },
  { value: 'tv', label: 'TV stand' },
  { value: 'none', label: 'Wall mount' },
];

// What the page remembers while the app is open (it unmounts on the Devices tab): the mode, and the guided steps.
const memo: {
  mode: LayoutMode;
  guide: GuideState | null;
  guideStarted: boolean;
} = { mode: 'place', guide: null, guideStarted: false };

/** @param openProfile A profile to open for editing (the Games page links to a game's), from `#/layout/profile:<id>`. */
export function LayoutPage({ openProfile }: { openProfile?: string | null } = {}) {
  const { state, dispatch, load, reload, issues, validatorError, blockers, dirty } = useEditor();
  const { backend, serverInfo, api, setup } = useConnection();
  const expert = useExpertTools();
  const activeProfile = serverInfo?.profile ?? null;
  const loadPrograms = useCallback(() => api.runningPrograms(), [api]);
  const effects = useMemo(() => (serverInfo?.effects ?? []).map((e) => e.name), [serverInfo?.effects]);
  const [mode, setModeState] = useState<LayoutMode>(memo.mode);
  /** The profile being edited (only what strips show), or null for the everyday layout. */
  const [editing, setEditing] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [testOpen, setTestOpen] = useState(false);
  const [findOpen, setFindOpen] = useState(false);
  const [found, setFound] = useState<FoundLed | null>(null);
  /** The strip whose path is being drawn; `added` when it was added for drawing (canceling removes it again). */
  const [drawing, setDrawing] = useState<{
    stripId: string;
    added: boolean;
  } | null>(null);
  const [matching, setMatching] = useState<string | null>(null);
  const [matchPoint, setMatchPoint] = useState<number | null>(null);
  const [guide, setGuideState] = useState<GuideState | null>(memo.guide);
  const setGuide = (g: GuideState | null) => {
    memo.guide = g;
    setGuideState(g);
  };
  const { spec: layout, selection } = state;
  const profile = mode === 'customize' && editing ? layout.profiles?.find((p) => p.id === editing) : undefined;
  // What the canvas and the strip panel show: the layout as the profile being edited has it.
  const spec = useMemo(() => (profile ? applyProfile(layout, profile.id) : layout), [layout, profile]);
  const strip = selectedStrip(state) && spec.strips.find((s) => s.id === selection.stripId);
  const place = mode === 'place';
  // Customize can show the LEDs' live colors instead of what each one follows (the rainbow).
  const [liveOn, setLiveOn] = useState(false);
  const showLive = liveOn && !place;
  const liveFrame = useLedFrame(showLive);

  // No layout yet, or one made from LED positions alone: Place starts as a short guided sequence.
  const wantsGuide = load.status === 'ready' && layout.controllers.length > 0 && (load.source !== 'saved' || layout.strips.length === 0);
  useEffect(() => {
    if (!wantsGuide || memo.guideStarted) return;
    memo.guideStarted = true;
    setGuide({ step: 0, tested: false });
  }, [wantsGuide]);
  const guideShown = !!guide && place && !drawing;
  // LED positions without a saved layout come from a HyperHDR import, or are the defaults of a new install: only the
  // first is announced as imported.
  const imported = load.source === 'legacy' && !!setup?.importedFrom;
  const note = imported ? load.note : load.note?.replace(IMPORTED_NOTE, '').trim() || undefined;

  /** What a strip's sections do: into the layout, or into the profile being edited. */
  const onRoles = useCallback(
    (stripId: string, path: PathPoint[], coalesce?: string) => {
      if (!profile) {
        dispatch({ type: 'updateStrip', stripId, patch: { path }, coalesce });
        return;
      }
      dispatch({
        type: 'updateProfile',
        profileId: profile.id,
        patch: { sections: withStripSections(profile, stripId, path).sections },
        coalesce,
      });
    },
    [profile, dispatch],
  );
  const matchStrip = matching ? spec.strips.find((s) => s.id === matching) : undefined;

  const onPickIssue = (issue: LayoutIssue) => {
    if (issue.stripId) dispatch({ type: 'selectStrip', stripId: issue.stripId });
    if (issue.pointIndex !== undefined) dispatch({ type: 'selectPoint', index: issue.pointIndex });
  };

  const addShape = (shape: ShapeId) => {
    setMatching(null);
    dispatch({ type: 'addStrip', shape });
  };
  const addDrawn = () => {
    setMatching(null);
    const stripId = nextStripId(spec);
    dispatch({ type: 'addStrip', shape: 'line' });
    setDrawing({ stripId, added: true });
  };
  const onDrawn = useCallback(
    (path: PathPoint[] | null) => {
      if (!drawing) return;
      if (path)
        dispatch({
          type: 'updateStrip',
          stripId: drawing.stripId,
          patch: { path },
        });
      else if (drawing.added && lastStepAddedStrip(state, drawing.stripId)) dispatch({ type: 'undo' });
      else if (drawing.added) dispatch({ type: 'removeStrip', stripId: drawing.stripId });
      setDrawing(null);
    },
    [drawing, dispatch, state],
  );
  // The strip being drawn went away (Ctrl+Z took back adding it): drawing ends with it.
  const drawnGone = !!drawing && !layout.strips.some((s) => s.id === drawing.stripId);
  useEffect(() => {
    if (drawnGone) setDrawing(null);
  }, [drawnGone]);
  const onLocateMatch = useCallback((led: number | null) => setFound(led === null || !matching ? null : { stripId: matching, local: led }), [matching]);
  const startMatching = (stripId: string) => {
    setDrawing(null);
    dispatch({ type: 'selectStrip', stripId });
    setMatching(stripId);
  };

  const setMode = (next: LayoutMode) => {
    if (next === mode) return;
    if (drawing) onDrawn(null);
    setMatching(null);
    dispatch({ type: 'selectSection', index: null });
    memo.mode = next;
    setModeState(next);
  };
  const editProfile = (id: string | null) => {
    setPreviewError(null);
    setEditing(id);
  };
  // a link to a profile (the Games page's) opens it once it is loaded
  const openable = !!openProfile && (layout.profiles ?? []).some((p) => p.id === openProfile);
  useEffect(() => {
    if (!openable || !openProfile) return;
    memo.mode = 'customize';
    setModeState('customize');
    setEditing(openProfile);
  }, [openProfile, openable]);
  const onPreview = (on: boolean) => {
    if (!profile) return;
    api
      .setProfile(on ? profile.id : null)
      .then(() => setPreviewError(null))
      .catch((e) => setPreviewError(String(e?.message ?? e)));
  };

  const hint = drawing
    ? HINTS.draw
    : showLive
      ? dirty
        ? 'Live colors follow the saved layout: save to see these changes lit.'
        : 'Live colors: each LED as it lights your room right now. Turn them off to see and adjust what each LED follows.'
      : !strip
      ? profile
        ? `Select a strip to choose what it shows in ${profile.name || 'this profile'}.`
        : HINTS.idle
      : place
        ? HINTS.place
        : mappingJoints(spec.room, strip).length > 0
          ? HINTS.mapping
          : HINTS.parts;

  const furniture = (
    <Segmented<Room['furniture']>
      label="Your screen is on a"
      size="sm"
      value={layout.room.furniture}
      options={FURNITURE}
      onChange={(furniture) => dispatch({ type: 'updateRoom', patch: { furniture } })}
    />
  );
  const matchTarget = strip ?? spec.strips[0];
  const profiles = layout.profiles ?? [];

  return (
    <div className="flex h-full flex-col">
      <PageHeader title="Layout" subtitle="Put your LED strips where they are, and choose what each part of them shows." actions={<SaveBar />} />

      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto lg:overflow-hidden">
        <div className="grid h-full grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(340px,380px)]">
          {/* Left: the room */}
          <div className="scroll-thin min-w-0 space-y-4 p-5 lg:overflow-y-auto">
            {note && !guideShown && (
              <div className="flex gap-2 rounded-xl border border-line bg-surface-2 p-3 text-[13px] text-ink-muted">
                <Info size={16} className="mt-px shrink-0 text-ink-faint" />
                {note}
              </div>
            )}
            {load.status === 'error' && (
              <div className="flex flex-wrap items-center gap-3 rounded-xl border border-bad/30 bg-bad/10 p-3 text-[13px] text-bad">
                Couldn’t load your layout: {load.error}
                <Button size="sm" icon={<RotateCcw size={14} />} className="ml-auto" onClick={reload}>
                  Try again
                </Button>
              </div>
            )}

            <Card className="overflow-hidden">
              {/* The one toolbar: mode, then what belongs to it, then tools. */}
              <div className="@container border-b border-line">
                <div className="flex min-h-[3.25rem] flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2.5">
                  {drawing ? (
                    <>
                      <span className="text-[13px] font-medium text-ink">Drawing {spec.strips.find((s) => s.id === drawing.stripId)?.name}</span>
                      <Button size="sm" variant="ghost" className="ml-auto" icon={<X size={15} />} onClick={() => onDrawn(null)}>
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <>
                      <Segmented<LayoutMode>
                        label="What to do"
                        value={mode}
                        options={[
                          { value: 'place', label: 'Place' },
                          { value: 'customize', label: 'Customize' },
                        ]}
                        onChange={setMode}
                      />
                      {place && !guideShown && (
                        <div className="flex items-center gap-2">
                          <span className="hidden text-xs text-ink-muted @min-[36rem]:inline" aria-hidden>
                            Your screen is on a
                          </span>
                          <div className="hidden @min-[30rem]:block">{furniture}</div>
                          {/* Narrow: the same choice as a list. */}
                          <select
                            aria-label="Your screen is on a"
                            value={layout.room.furniture}
                            onChange={(e) =>
                              dispatch({
                                type: 'updateRoom',
                                patch: {
                                  furniture: e.target.value as Room['furniture'],
                                },
                              })
                            }
                            className="h-8 rounded-lg border border-line-input bg-bg/60 px-2 text-[13px] text-ink @min-[30rem]:hidden"
                          >
                            {FURNITURE.map((f) => (
                              <option key={f.value} value={f.value}>
                                {f.label}
                              </option>
                            ))}
                          </select>
                        </div>
                      )}
                      {!place && profiles.length > 0 && (
                        <ProfileTabs
                          spec={layout}
                          editing={profile?.id ?? null}
                          active={serverInfo ? (activeProfile?.id ?? null) : undefined}
                          dispatch={dispatch}
                          onEdit={editProfile}
                        />
                      )}
                      <div className="ml-auto flex items-center gap-1">
                        {!place && (
                          <Button
                            size="sm"
                            variant="ghost"
                            icon={<Activity size={15} />}
                            aria-pressed={liveOn}
                            className={liveOn ? 'text-ink' : undefined}
                            title="Show the colors your LEDs have right now"
                            onClick={() => setLiveOn((on) => !on)}
                          >
                            <span className="hidden @min-[42rem]:inline">Live colors</span>
                          </Button>
                        )}
                        {place && (
                          <Button
                            size="sm"
                            variant="ghost"
                            icon={<MonitorPlay size={15} />}
                            title="Test pattern"
                            aria-label="Test pattern"
                            onClick={() => setTestOpen(true)}
                          >
                            {/* Only the icon while the furniture choice needs the room. */}
                            <span className={guideShown ? undefined : 'hidden @min-[42rem]:inline'}>Test pattern</span>
                          </Button>
                        )}
                        <Expert>
                          <IconButton
                            label="Find an LED"
                            aria-pressed={findOpen}
                            className={findOpen ? 'text-ink' : undefined}
                            onClick={() => setFindOpen((o) => !o)}
                          >
                            <Crosshair size={16} />
                          </IconButton>
                        </Expert>
                      </div>
                    </>
                  )}
                </div>
              </div>
              <div className="relative h-[62vh] min-h-[380px]">
                <RoomCanvas
                  spec={spec}
                  selection={selection}
                  dispatch={dispatch}
                  drawing={drawing?.stripId ?? null}
                  onDrawn={onDrawn}
                  found={found}
                  matching={matching && matchPoint !== null ? { stripId: matching, pointIndex: matchPoint } : null}
                  effects={effects}
                  rolesOnly={!place}
                  showMapping={!place && !showLive}
                  allowGaps={!profile}
                  onRoles={onRoles}
                  // Until the first colors arrive, every LED is shown unlit (not the rainbow).
                  liveColors={showLive ? (liveFrame ?? NO_COLORS) : null}
                />
                {load.status === 'loading' && (
                  <div className="absolute inset-0 flex items-center justify-center bg-bg/60 text-sm text-ink-muted">
                    <Loader2 size={18} className="mr-2 animate-spin" /> Loading your layout…
                  </div>
                )}
              </div>
              <p className="border-t border-line px-4 py-2 text-xs text-ink-faint">{hint}</p>
            </Card>

            <IssueSummary spec={spec} issues={issues} validatorError={validatorError} blockers={blockers} onPick={onPickIssue} />
            {backend.mode === 'mock' && <p className="text-xs text-ink-faint">Demo mode: lighting LEDs and saving only affect the built-in demo data.</p>}
          </div>

          {/* Right: what the mode is about, for the selected strip */}
          <aside
            aria-label={place ? 'Place strips' : 'Customize strips'}
            className="scroll-thin divide-y divide-line border-t border-line bg-surface/40 p-5 lg:overflow-y-auto lg:border-l lg:border-t-0 [&>*:not(:first-child)]:pt-5 [&>*:not(:last-child)]:pb-5"
          >
            {expert && findOpen && <FindLed spec={spec} controllerId={strip?.controllerId ?? null} onLocate={setFound} onClose={() => setFindOpen(false)} />}
            {matchStrip && (
              <MatchLights
                key={matchStrip.id}
                spec={spec}
                strip={matchStrip}
                dispatch={dispatch}
                onLocate={onLocateMatch}
                onPoint={setMatchPoint}
                onClose={() => {
                  setMatching(null);
                  setFound(null);
                }}
              />
            )}
            {guideShown && guide && !matchStrip && (
              <PlaceGuide
                imported={imported}
                note={note}
                state={guide}
                onState={setGuide}
                onHide={() => setGuide(null)}
                furniture={furniture}
                furnitureLabel={{ desk: 'desk', tv: 'TV stand', none: 'wall mount' }[layout.room.furniture]}
                matchLabel={matchTarget ? matchTarget.name : null}
                onMatch={() => matchTarget && startMatching(matchTarget.id)}
                onTest={() => {
                  setGuide({ ...guide, tested: true });
                  setTestOpen(true);
                }}
              />
            )}
            {profile && (
              <ProfileSettings
                spec={layout}
                profileId={profile.id}
                dispatch={dispatch}
                onEdit={editProfile}
                loadPrograms={loadPrograms}
                previewing={!!activeProfile?.manual && activeProfile.id === profile.id}
                previewBlocked={dirty ? 'Save first: your lights use the saved layout' : undefined}
                previewError={previewError}
                onPreview={onPreview}
              />
            )}
            <StripPanel
              spec={spec}
              selectedId={selection.stripId}
              issues={issues}
              dispatch={dispatch}
              onAdd={addShape}
              onAddDrawn={addDrawn}
              customize={!place}
              rolesOnly={!!profile}
            />
            {strip && !matchStrip && place && (
              <PlaceEditor
                spec={spec}
                strip={strip}
                issues={issues}
                dispatch={dispatch}
                onDraw={() => setDrawing({ stripId: strip.id, added: false })}
                onMatch={() => startMatching(strip.id)}
              />
            )}
            {strip && !place && (
              <CustomizeEditor
                spec={spec}
                strip={strip}
                issues={issues}
                dispatch={dispatch}
                effects={effects}
                profileName={profile ? profile.name || 'this profile' : undefined}
                onRoles={(path) => onRoles(strip.id, path)}
              />
            )}
            {!place && profiles.length === 0 && layout.strips.length > 0 && <ProfileIntro spec={layout} dispatch={dispatch} onEdit={editProfile} />}
          </aside>
        </div>
      </div>

      {testOpen && <TestPattern onClose={() => setTestOpen(false)} />}
    </div>
  );
}
