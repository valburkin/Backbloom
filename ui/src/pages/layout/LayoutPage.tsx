import { useCallback, useEffect, useMemo, useState } from 'react';
import { Info, Loader2, MonitorPlay, RotateCcw, X } from 'lucide-react';
import { applyProfile, withStripSections, type LayoutIssue, type PathPoint, type Room, type ShapeId } from '../../layout';
import { Button, Card, Segmented } from '../../components/ui';
import { PageHeader } from '../../components/PageHeader';
import { SaveBar } from '../../components/SaveBar';
import { useConnection } from '../../app/connection';
import { useEditor } from '../../app/editor';
import { lastStepAddedStrip, nextStripId, selectedStrip } from '../../editor/state';
import { RoomCanvas, type FoundLed } from './RoomCanvas';
import { StripPanel } from './StripPanel';
import { StripEditor } from './StripEditor';
import { MatchLights } from './MatchLights';
import { FindLed } from './FindLed';
import { TestPattern } from './TestPattern';
import { IssueSummary } from './IssueSummary';
import { ProfileBar } from './ProfileBar';

export function LayoutPage() {
  const { state, dispatch, load, reload, issues, validatorError, blockers, dirty } = useEditor();
  const { backend, serverInfo, api } = useConnection();
  const activeProfile = serverInfo?.profile ?? null;
  const loadPrograms = useCallback(() => api.runningPrograms(), [api]);
  const [tryError, setTryError] = useState<string | null>(null);
  const effects = useMemo(() => (serverInfo?.effects ?? []).map((e) => e.name), [serverInfo?.effects]);
  /** The profile being edited (only what strips show), or null for the everyday layout. */
  const [editing, setEditing] = useState<string | null>(null);
  const [testOpen, setTestOpen] = useState(false);
  const [found, setFound] = useState<FoundLed | null>(null);
  /** The strip whose path is being drawn; `added` when it was added for drawing (canceling removes it again). */
  const [drawing, setDrawing] = useState<{ stripId: string; added: boolean } | null>(null);
  const [matching, setMatching] = useState<string | null>(null);
  const [matchPoint, setMatchPoint] = useState<number | null>(null);
  const { spec: layout, selection } = state;
  const profile = editing ? layout.profiles?.find((p) => p.id === editing) : undefined;
  // What the canvas and the strip panel show: the layout as the profile being edited has it.
  const spec = useMemo(() => (profile ? applyProfile(layout, profile.id) : layout), [layout, profile]);
  const strip = selectedStrip(state) && spec.strips.find((s) => s.id === selection.stripId);
  const editProfile = (id: string | null) => {
    setDrawing(null);
    setMatching(null);
    setEditing(id);
  };
  /** What a strip's sections do: into the layout, or into the profile being edited. */
  const onRoles = useCallback(
    (stripId: string, path: PathPoint[], coalesce?: string) => {
      if (!profile) {
        dispatch({ type: 'updateStrip', stripId, patch: { path }, coalesce });
        return;
      }
      dispatch({ type: 'updateProfile', profileId: profile.id, patch: { sections: withStripSections(profile, stripId, path).sections }, coalesce });
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
      if (path) dispatch({ type: 'updateStrip', stripId: drawing.stripId, patch: { path } });
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

  const hint = profile
    ? `Choosing what strips show in ${profile.name || 'this profile'}: click a strip or a part of it. Where strips run is the same in every profile.`
    : drawing
    ? 'Click where the strip starts (where its cable comes in), then at every bend. Double-click or press Enter to finish, Esc to undo a point.'
    : strip
      ? 'Colors show which LED repeats which part of the screen; drag the dots on the screen’s edge to change it. Drag the round handles to move the strip, click a part of it for more.'
      : 'Select a strip to change it, or add one. Scroll to zoom, drag the background to look around; the walls, ceiling and floor are folded out around the front wall.';

  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title="Layout"
        subtitle="Put your LED strips where they are in your room, so every LED shows the part of the picture next to it."
        actions={<SaveBar />}
      />

      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto lg:overflow-hidden">
        <div className="grid h-full grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(340px,380px)]">
          {/* Left: the room + tools */}
          <div className="scroll-thin min-w-0 space-y-4 p-6 lg:overflow-y-auto">
            {load.note && (
              <div className="flex gap-2 rounded-xl border border-line bg-surface-2 p-3 text-[13px] text-ink-muted">
                <Info size={16} className="mt-px shrink-0 text-accent-strong" />
                {load.note}
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
              {!drawing && (
                <div className="border-b border-line px-4 py-2.5">
                  <ProfileBar
                    spec={layout}
                    editing={profile?.id ?? null}
                    active={serverInfo ? (activeProfile?.id ?? null) : undefined}
                    dispatch={dispatch}
                    onEdit={editProfile}
                    loadPrograms={loadPrograms}
                    extra={
                      profile && (
                        <>
                          {tryError && <span className="text-xs text-bad">{tryError}</span>}
                          {activeProfile?.manual && activeProfile.id === profile.id ? (
                            <Button size="sm" variant="ghost" onClick={() => api.setProfile(null).then(() => setTryError(null)).catch((e) => setTryError(String(e?.message ?? e)))}>
                              Stop trying
                            </Button>
                          ) : (
                            <Button
                              size="sm"
                              variant="ghost"
                              disabled={dirty}
                              title={dirty ? 'Save first: the lights use the saved layout' : 'Use this profile now, without starting its programs'}
                              onClick={() => api.setProfile(profile.id).then(() => setTryError(null)).catch((e) => setTryError(String(e?.message ?? e)))}
                            >
                              Try it now
                            </Button>
                          )}
                        </>
                      )
                    }
                  />
                </div>
              )}
              <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-2.5">
                {drawing ? (
                  <>
                    <span className="text-[13px] font-medium text-accent-strong">Drawing {spec.strips.find((s) => s.id === drawing.stripId)?.name}</span>
                    <div className="ml-auto flex items-center gap-2">
                      <Button size="sm" variant="ghost" icon={<X size={15} />} onClick={() => onDrawn(null)}>
                        Cancel
                      </Button>
                    </div>
                  </>
                ) : (
                  <>
                    <Segmented<Room['furniture']>
                      label="What the screen stands on"
                      size="sm"
                      value={spec.room.furniture}
                      options={[
                        { value: 'desk', label: 'Desk' },
                        { value: 'tv', label: 'TV stand' },
                        { value: 'none', label: 'Wall' },
                      ]}
                      onChange={(furniture) => !profile && dispatch({ type: 'updateRoom', patch: { furniture } })}
                    />
                    <div className="ml-auto flex items-center gap-2">
                      <Button size="sm" variant="ghost" icon={<MonitorPlay size={15} />} onClick={() => setTestOpen(true)}>
                        Test pattern
                      </Button>
                    </div>
                  </>
                )}
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
                  rolesOnly={!!profile}
                  onRoles={onRoles}
                />
                {load.status === 'loading' && (
                  <div className="absolute inset-0 flex items-center justify-center bg-bg/60 text-sm text-ink-muted">
                    <Loader2 size={18} className="mr-2 animate-spin" /> Loading your layout…
                  </div>
                )}
              </div>
              <div className="flex items-center gap-3 border-t border-line px-4 py-2">
                <p className="flex-1 text-xs text-ink-faint">{hint}</p>
              </div>
            </Card>

            <FindLed spec={spec} controllerId={strip?.controllerId ?? null} onLocate={setFound} />
            <IssueSummary spec={spec} issues={issues} validatorError={validatorError} blockers={blockers} onPick={onPickIssue} />
            {backend.mode === 'mock' && <p className="text-xs text-ink-faint">Demo mode: blinking and saving only affect the built-in demo data.</p>}
          </div>

          {/* Right: strips and the selected strip */}
          <aside aria-label="Strip editor" className="scroll-thin space-y-6 border-t border-line bg-surface/40 p-5 lg:overflow-y-auto lg:border-l lg:border-t-0">
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
            <StripPanel spec={spec} selectedId={selection.stripId} issues={issues} dispatch={dispatch} onAdd={addShape} onAddDrawn={addDrawn} rolesOnly={!!profile} />
            {strip && !matchStrip && (
              <StripEditor
                spec={spec}
                strip={strip}
                issues={issues}
                dispatch={dispatch}
                effects={effects}
                profileName={profile ? profile.name || 'this profile' : undefined}
                onRoles={(path) => onRoles(strip.id, path)}
                onDraw={() => setDrawing({ stripId: strip.id, added: false })}
                onMatch={() => {
                  setDrawing(null);
                  setMatching(strip.id);
                }}
              />
            )}
          </aside>
        </div>
      </div>

      {testOpen && <TestPattern onClose={() => setTestOpen(false)} />}
    </div>
  );
}
