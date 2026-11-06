import { useId, useMemo } from 'react';
import type { ServerInfo } from '../api/types';
import { ledColor, PREVIEW_FRAME, previewPoints } from '../live/ledPreview';

const W = 160;
const H = 90;

/**
 * Live LED colors drawn around a screen outline (the preview before a layout is saved), with their light like
 * LayoutLights' (a glow close to each LED and a faint wide wash, screen-blended over the background), drawn simpler.
 */
export function LedPreview({ rects, frame, label = 'Live LED preview' }: { rects: ServerInfo['leds']; frame: number[] | null; label?: string }) {
  const id = useId();
  const points = useMemo(() => previewPoints(rects ?? []), [rects]);
  const f = PREVIEW_FRAME;
  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="block h-auto w-full" role="img" aria-label={`${label}: ${points.length} LEDs`}>
      <defs>
        <filter id={`${id}glow`} x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="2.4" />
        </filter>
        <filter id={`${id}wash`} x="-40%" y="-40%" width="180%" height="180%">
          <feGaussianBlur stdDeviation="8" />
        </filter>
      </defs>
      <rect x={0} y={0} width={W} height={H} rx={6} fill="#07080c" />
      <g filter={`url(#${id}wash)`} opacity={0.35} style={{ mixBlendMode: 'screen' }}>
        {points.map((p) => (
          <circle key={p.index} cx={p.x * W} cy={p.y * H} r={3} fill={ledColor(frame, p.index)} />
        ))}
      </g>
      <g filter={`url(#${id}glow)`} opacity={0.7} style={{ mixBlendMode: 'screen' }}>
        {points.map((p) => (
          <circle key={p.index} cx={p.x * W} cy={p.y * H} r={2.2} fill={ledColor(frame, p.index)} />
        ))}
      </g>
      <rect
        x={f * W}
        y={f * H}
        width={(1 - 2 * f) * W}
        height={(1 - 2 * f) * H}
        rx={2.5}
        fill="#0d1017"
        stroke="#343c4f"
        strokeWidth={0.6}
      />
      {points.map((p) => (
        <circle key={p.index} cx={p.x * W} cy={p.y * H} r={0.9} fill={ledColor(frame, p.index)} />
      ))}
    </svg>
  );
}
