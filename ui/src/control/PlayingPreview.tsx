import { useMemo, type ReactNode } from 'react';
import { ArrowRight, AudioLines, Palette, ScreenShare, SlidersHorizontal, Sparkles, SunMedium } from 'lucide-react';
import { useConnection } from '../app/connection';
import { useAction } from '../app/useAction';
import { useAppGlow } from '../app/appGlow';
import { hrefFor } from '../app/router';
import { Button, Card, cx, StatusDot } from '../components/ui';
import { LedPreview } from '../components/LedPreview';
import { compactLayoutView, LiveLayoutLights } from '../components/LayoutLights';
import { usePreviewFrame } from '../live/hooks';
import { useLayoutView } from '../live/layoutView';
import { glowColors } from '../live/ledPreview';
import { isMusicEffect } from './modes';
import { playLabel, type WhatPlays } from './presets';
import { overlayOf, overlayText } from './setup';
import { useSetupInUse } from './usePick';

const LINK = 'text-accent-strong underline-offset-2 hover:underline';

function playIcon(plays: WhatPlays, size = 18): ReactNode {
  switch (plays.kind) {
    case 'edges':
      return <ScreenShare size={size} />;
    case 'average':
      return <SunMedium size={size} />;
    case 'effect':
      return isMusicEffect(plays.effect) ? <AudioLines size={size} /> : <Sparkles size={size} />;
    case 'color':
      return <Palette size={size} />;
    default:
      return <SlidersHorizontal size={size} />;
  }
}

/**
 * Playing now: what the setup in use plays (from its strips: Screen edges, an effect, music, a color, or customized in
 * Layout) with the setup's name, and the live preview, as the LEDs light the room right now. Nothing to pick here: Change leads to
 * Effects. When another app shows something over the setup, it says so with Back to <setup> (clears that).
 * `inline`: the words beside the preview, which is at most `maxHeight` high (the Effects page's sticky preview).
 */
export function PlayingPreview({
  title,
  change,
  glow,
  inline,
  maxHeight,
  ignoreOwn,
}: {
  title?: string;
  change?: boolean;
  glow?: boolean;
  inline?: boolean;
  maxHeight?: number;
  /** Not a word about a color the UI tries (the Effects page, where its picker is). */
  ignoreOwn?: boolean;
}) {
  const { serverInfo, api, auth } = useConnection();
  const { run, busy } = useAction();
  const { name, plays } = useSetupInUse();
  const full = useLayoutView(api, auth === 'admin', serverInfo?.leds);
  const view = useMemo(() => (full ? compactLayoutView(full) : null), [full]);
  const { frame } = usePreviewFrame();
  const glowing = useMemo(() => (glow ? glowColors(frame, serverInfo?.leds) : null), [glow, frame, serverInfo?.leds]);
  useAppGlow(glow ? glowing : null);
  const overlay = overlayOf(serverInfo?.priorities, ignoreOwn);
  const grabberOn = serverInfo?.components?.find((c) => c.name === 'SYSTEMGRABBER')?.enabled !== false;
  const usesScreen = plays.kind === 'edges' || plays.kind === 'average' || plays.kind === 'customized';
  const rgb = plays.kind === 'color' ? plays.color : null;
  const aspect = view ? view.width / view.height : 160 / 90;

  const label = (
    <div className="min-w-0 flex-1">
      {title && <h2 className="text-xs font-medium uppercase tracking-wide text-ink-faint">{title}</h2>}
      <p className="flex min-w-0 items-center gap-2 text-base font-semibold">
        <span className="truncate">{playLabel(plays)}</span>
        {rgb && <span className="inline-block h-3.5 w-3.5 shrink-0 rounded-full border border-line-strong" style={{ backgroundColor: `rgb(${rgb.join(',')})` }} title={`RGB ${rgb.join(', ')}`} aria-hidden />}
      </p>
      <p className="truncate text-[13px] text-ink-muted">{name}</p>
    </div>
  );
  const live = (
    <span className="flex shrink-0 items-center gap-2 text-xs text-ink-faint">
      <StatusDot tone={frame ? 'ok' : 'neutral'} live />
      {frame ? 'Live' : 'Waiting for colors…'}
    </span>
  );
  const notes = (usesScreen && !grabberOn && !overlay) || plays.kind === 'customized';
  const noteLine = notes && (
    <p className={cx('flex flex-wrap gap-x-3 text-xs text-ink-faint', !inline && 'mt-2 justify-center text-center')}>
      {usesScreen && !grabberOn && !overlay && <span>Screen sync is off.</span>}
      {plays.kind === 'customized' && (
        <span>
          Each strip as{' '}
          <a className={LINK} href={hrefFor('layout')}>
            Layout
          </a>{' '}
          has it.
        </span>
      )}
    </p>
  );
  const preview = (
    <div className="mx-auto w-full" style={maxHeight ? { maxWidth: `${Math.round(maxHeight * aspect)}px` } : undefined}>
      {view ? <LiveLayoutLights view={view} frame={frame} /> : <LedPreview rects={serverInfo?.leds} frame={frame} />}
    </div>
  );
  const back = overlay && (
    <div className="flex flex-wrap items-center gap-3 border-b border-line bg-warn/10 px-5 py-2.5 text-[13px] text-ink">
      <span className="min-w-0 flex-1">{overlayText(overlay, name)}.</span>
      <Button size="sm" disabled={busy === 'back'} onClick={() => void run('back', `Couldn’t switch back to ${name}`, () => api.clear(overlay.priority))}>
        Back to {name}
      </Button>
    </div>
  );

  if (inline)
    return (
      <Card className="overflow-hidden">
        {back}
        <div className="flex flex-col gap-3 p-3 sm:flex-row sm:items-stretch">
          <div className="flex min-w-0 flex-col justify-between gap-2 px-2 py-1 sm:w-60 sm:shrink-0">
            <div className="flex items-center gap-3">
              <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent/15 text-accent-strong" aria-hidden>
                {playIcon(plays)}
              </div>
              {label}
            </div>
            {live}
            {noteLine}
          </div>
          <div className="min-w-0 flex-1 rounded-xl bg-[radial-gradient(ellipse_at_center,#141826_0%,#0b0d13_70%)] p-2">{preview}</div>
        </div>
      </Card>
    );

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-line px-5 py-3">
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent/15 text-accent-strong" aria-hidden>
          {playIcon(plays)}
        </div>
        {label}
        {live}
        {change && (
          <a href={hrefFor('effects')} className={cx('inline-flex h-8 items-center gap-1 text-[13px] font-medium', LINK)}>
            Change <ArrowRight size={14} />
          </a>
        )}
      </div>
      {back}
      <div className="bg-[radial-gradient(ellipse_at_center,#141826_0%,#0b0d13_70%)] p-4">
        {preview}
        {noteLine}
      </div>
    </Card>
  );
}
