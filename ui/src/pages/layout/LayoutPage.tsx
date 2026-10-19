import { useCallback, useState } from 'react';
import { Eye, Info, LayoutTemplate, Loader2, MonitorPlay, RotateCcw } from 'lucide-react';
import type { LayoutIssue } from '../../layout';
import { Button, Card, Segmented } from '../../components/ui';
import { PageHeader } from '../../components/PageHeader';
import { SaveBar } from '../../components/SaveBar';
import { useConnection } from '../../app/connection';
import { useEditor } from '../../app/editor';
import { ASPECTS, aspectRatio, aspectSize, type AspectId } from '../../editor/geometry';
import { selectedStrip } from '../../editor/state';
import { LayoutCanvas, type FoundLed } from './LayoutCanvas';
import { StripPanel } from './StripPanel';
import { SegmentPanel } from './SegmentPanel';
import { TemplateDialog } from './TemplateDialog';
import { FindLed } from './FindLed';
import { TestPattern } from './TestPattern';
import { IssueSummary } from './IssueSummary';

export function LayoutPage() {
  const { state, dispatch, load, reload, issues, validatorError, blockers, aspect, setAspect } = useEditor();
  const { auth, requestLogin, backend } = useConnection();
  const [templateOpen, setTemplateOpen] = useState(false);
  const [testOpen, setTestOpen] = useState(false);
  const [found, setFound] = useState<FoundLed | null>(null);
  const { spec, selection } = state;
  const strip = selectedStrip(state);
  const output = strip
    ? spec.controllers.find((c) => c.id === strip.controllerId)?.outputs.find((o) => o.index === strip.output)
    : undefined;

  const onSelect = useCallback(
    (stripId: string, segmentIndex: number | null) => {
      dispatch({ type: 'selectStrip', stripId });
      if (segmentIndex !== null) dispatch({ type: 'selectSegment', index: segmentIndex });
    },
    [dispatch],
  );
  const onPickIssue = (issue: LayoutIssue) => {
    if (issue.stripId) onSelect(issue.stripId, issue.segmentIndex ?? null);
  };

  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title="Layout"
        subtitle="Show us where your LED strips are, so every LED matches the picture next to it."
        actions={<SaveBar />}
      />

      <div className="scroll-thin min-h-0 flex-1 overflow-y-auto lg:overflow-hidden">
        <div className="grid h-full grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(340px,380px)]">
          {/* Left: canvas + tools */}
          <div className="scroll-thin min-w-0 space-y-4 p-6 lg:overflow-y-auto">
            {auth === 'readOnly' && (
              <div className="flex flex-wrap items-center gap-3 rounded-xl border border-line bg-surface-2 p-3 text-[13px] text-ink-muted">
                <Eye size={16} className="text-accent-strong" />
                You’re looking around without signing in. You can try things out, but you’ll need to sign in to save.
                <Button size="sm" variant="secondary" className="ml-auto" onClick={requestLogin}>
                  Sign in
                </Button>
              </div>
            )}
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
              <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-2.5">
                <Segmented<AspectId>
                  label="Screen shape"
                  size="sm"
                  value={aspect}
                  onChange={setAspect}
                  options={ASPECTS.map((a) => ({ value: a.id, label: a.label, title: `${a.label} screen` }))}
                />
                <div className="ml-auto flex items-center gap-2">
                  <Button size="sm" variant="ghost" icon={<LayoutTemplate size={15} />} onClick={() => setTemplateOpen(true)}>
                    Start from template
                  </Button>
                  <Button size="sm" variant="ghost" icon={<MonitorPlay size={15} />} onClick={() => setTestOpen(true)}>
                    Test pattern
                  </Button>
                </div>
              </div>
              <div className="relative bg-[radial-gradient(ellipse_at_center,#141826_0%,#0b0d13_70%)] p-4">
                <div className="mx-auto aspect-[16/10] max-h-[62vh] w-full">
                  <LayoutCanvas spec={spec} ratio={aspectRatio(aspect)} selection={selection} found={found} onSelect={onSelect} />
                </div>
                {load.status === 'loading' && (
                  <div className="absolute inset-0 flex items-center justify-center bg-bg/60 text-sm text-ink-muted">
                    <Loader2 size={18} className="mr-2 animate-spin" /> Loading your layout…
                  </div>
                )}
              </div>
              <p className="border-t border-line px-4 py-2 text-xs text-ink-faint">
                Numbered circles mark where each strip starts; arrows show the direction the LEDs run. Click an LED to edit its part.
              </p>
            </Card>

            <FindLed spec={spec} controllerId={strip?.controllerId ?? null} onLocate={setFound} />
            <IssueSummary spec={spec} issues={issues} validatorError={validatorError} blockers={blockers} onPick={onPickIssue} />
            {backend.mode === 'mock' && <p className="text-xs text-ink-faint">Demo mode: blinking and saving only affect the built-in demo data.</p>}
          </div>

          {/* Right: strips + parts */}
          <aside aria-label="Strip editor" className="scroll-thin space-y-6 border-t border-line bg-surface/40 p-5 lg:overflow-y-auto lg:border-l lg:border-t-0">
            <StripPanel spec={spec} selectedId={selection.stripId} issues={issues} dispatch={dispatch} />
            {strip && <SegmentPanel strip={strip} selectedIndex={selection.segmentIndex} issues={issues} dispatch={dispatch} />}
          </aside>
        </div>
      </div>

      <TemplateDialog
        open={templateOpen}
        onClose={() => setTemplateOpen(false)}
        stripName={strip?.name ?? null}
        outputLength={output?.length}
        aspect={aspectSize(aspect)}
        onApply={(segments) => strip && dispatch({ type: 'setSegments', stripId: strip.id, segments })}
      />
      {testOpen && <TestPattern onClose={() => setTestOpen(false)} />}
    </div>
  );
}
