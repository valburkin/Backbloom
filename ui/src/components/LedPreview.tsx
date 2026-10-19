import { useId, useMemo } from 'react';
import type { ServerInfo } from '../api/types';
import { ledColor, PREVIEW_FRAME, previewPoints } from '../live/ledPreview';

const W = 160;
const H = 90;

/** Live LED colors drawn around a screen outline, with a soft glow like the real thing. */
export function LedPreview({ rects, frame, label = 'Live LED preview' }: { rects: ServerInfo['leds']; frame: number[] | null; label?: string }) {
  const id = useId();
  const points = useMemo(() => previewPoints(rects ?? []), [rects]);
  const f = PREVIEW_FRAME;
  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="block h-auto w-full" role="img" aria-label={`${label}: ${points.length} LEDs`}>
      <defs>
        <filter id={`${id}glow`} x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="3.2" />
        </filter>
      </defs>
      <rect x={0} y={0} width={W} height={H} rx={6} fill="#07080c" />
      <g filter={`url(#${id}glow)`} opacity={0.95}>
        {points.map((p) => (
          <circle key={p.index} cx={p.x * W} cy={p.y * H} r={3.6} fill={ledColor(frame, p.index)} />
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
