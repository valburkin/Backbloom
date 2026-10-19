import { memo, useMemo } from 'react';
import type { LayoutSpec, Strip } from '../../layout';
import { stripColor } from '../../editor/colors';
import { LANE_BASE, LANE_STEP, stripDots, typicalSpacing, type LedDot, type ScreenBox } from '../../editor/geometry';
import type { Selection } from '../../editor/state';
import { controllerIndex, safeCompileStrip, segmentRanges } from './model';

const W = 1000;

export interface FoundLed {
  stripId: string;
  local: number;
}

interface Props {
  spec: LayoutSpec;
  ratio: number;
  selection: Selection;
  found: FoundLed | null;
  onSelect(stripId: string, segmentIndex: number | null): void;
}

function Arrow({ a, b, color, size }: { a: LedDot; b: LedDot; color: string; size: number }) {
  const ang = (Math.atan2(b.y - a.y, b.x - a.x) * 180) / Math.PI;
  const mx = (a.x + b.x) / 2;
  const my = (a.y + b.y) / 2;
  return (
    <path
      d={`M ${-size} ${-size * 0.8} L ${size * 0.9} 0 L ${-size} ${size * 0.8} Z`}
      transform={`translate(${mx} ${my}) rotate(${ang})`}
      fill={color}
      opacity={0.9}
    />
  );
}

const StripGraphic = memo(function StripGraphic({
  strip,
  index,
  box,
  selected,
  dimmed,
  selectedSegment,
  spec,
  found,
  onSelect,
}: {
  strip: Strip;
  index: number;
  box: ScreenBox;
  selected: boolean;
  dimmed: boolean;
  selectedSegment: number | null;
  spec: LayoutSpec;
  found: number | null;
  onSelect(stripId: string, segmentIndex: number | null): void;
}) {
  const color = stripColor(index);
  const dots = useMemo(() => stripDots(strip, box, index), [strip, box, index]);
  if (!dots.length) return null;
  const spacing = typicalSpacing(dots);
  const r = Math.max(2.2, Math.min(6, spacing * 0.34));
  const arrowEvery = Math.max(3, Math.round(dots.length / 10));
  const path = dots.map((d, i) => `${i ? 'L' : 'M'} ${d.x.toFixed(1)} ${d.y.toFixed(1)}`).join(' ');
  const first = dots[0];

  return (
    <g opacity={dimmed ? 0.35 : 1} style={{ transition: 'opacity 150ms' }}>
      <path d={path} fill="none" stroke={color} strokeOpacity={selected ? 0.55 : 0.3} strokeWidth={selected ? 2 : 1.5} strokeLinejoin="round" />
      {dots.map((d, i) =>
        i % arrowEvery === Math.floor(arrowEvery / 2) && i + 1 < dots.length ? (
          <Arrow key={`a${i}`} a={d} b={dots[i + 1]} color={color} size={Math.max(3, r * 0.75)} />
        ) : null,
      )}
      {dots.map((d) => {
        const inSel = selected && selectedSegment !== null && d.segmentIndex === selectedSegment;
        const ci = controllerIndex(spec, strip, d.index);
        return (
          <circle
            key={d.index}
            cx={d.x}
            cy={d.y}
            r={inSel ? r * 1.25 : r}
            fill={d.kind === 'off' ? 'var(--color-bg)' : color}
            stroke={d.kind === 'off' ? '#6a7285' : inSel ? '#fff' : 'none'}
            strokeWidth={d.kind === 'off' ? 1.2 : 1.5}
            className="cursor-pointer"
            onClick={() => onSelect(strip.id, d.segmentIndex)}
          >
            <title>
              {`${strip.name} · LED ${d.index + 1}${ci !== null ? ` (controller #${ci})` : ''}${d.kind === 'off' ? ' · dark' : ''}`}
            </title>
          </circle>
        );
      })}
      {found !== null && dots[found] && (
        <g pointerEvents="none">
          <circle cx={dots[found].x} cy={dots[found].y} r={r * 1.6} fill="none" stroke="#fff" strokeWidth={2} />
          <circle cx={dots[found].x} cy={dots[found].y} r={r * 1.6} fill="none" stroke="#fff" strokeWidth={2} className="led-ping" />
        </g>
      )}
      {/* Start marker: strip number at LED 1 */}
      <g transform={`translate(${first.x} ${first.y})`} className="cursor-pointer" onClick={() => onSelect(strip.id, 0)}>
        <circle r={10} fill={color} stroke="var(--color-bg)" strokeWidth={2.5} />
        <text textAnchor="middle" dominantBaseline="central" fontSize={11} fontWeight={700} fill="#0b0d12">
          {index + 1}
        </text>
        <title>{`${strip.name} starts here`}</title>
      </g>
    </g>
  );
});

export function LayoutCanvas({ spec, ratio, selection, found, onSelect }: Props) {
  const H = W / ratio;
  const lanes = Math.max(1, spec.strips.length);
  const M = LANE_BASE + lanes * LANE_STEP + 26;
  const box: ScreenBox = useMemo(() => ({ x: M, y: M, w: W, h: H }), [M, H]);

  const selectedIdx = spec.strips.findIndex((s) => s.id === selection.stripId);
  const selected = selectedIdx >= 0 ? spec.strips[selectedIdx] : undefined;
  const overlay = useMemo(() => {
    if (!selected) return null;
    const { rects, error } = safeCompileStrip(selected);
    return { rects, error, ranges: segmentRanges(selected.segments) };
  }, [selected]);
  const color = selectedIdx >= 0 ? stripColor(selectedIdx) : '#fff';
  const foundOnSelected = found && selected && found.stripId === selected.id ? found.local : null;

  return (
    <svg
      viewBox={`0 0 ${W + 2 * M} ${H + 2 * M}`}
      className="h-full w-full select-none"
      role="img"
      aria-label={`Screen with ${spec.strips.length} LED strip${spec.strips.length === 1 ? '' : 's'}${selected ? `; ${selected.name} selected` : ''}`}
    >
      <defs>
        <linearGradient id="screen-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#171b26" />
          <stop offset="1" stopColor="#0e1118" />
        </linearGradient>
        <clipPath id="screen-clip">
          <rect x={box.x} y={box.y} width={box.w} height={box.h} rx={16} />
        </clipPath>
      </defs>

      {/* Screen */}
      <rect x={box.x} y={box.y} width={box.w} height={box.h} rx={16} fill="url(#screen-fill)" stroke="#343c4f" strokeWidth={2} />
      <g fill="#6a7285" fontSize={13} fontWeight={600} letterSpacing={2} opacity={0.55} pointerEvents="none">
        <text x={box.x + box.w / 2} y={box.y + 22} textAnchor="middle">TOP</text>
        <text x={box.x + box.w / 2} y={box.y + box.h - 14} textAnchor="middle">BOTTOM</text>
        <text transform={`translate(${box.x + 20} ${box.y + box.h / 2}) rotate(-90)`} textAnchor="middle">LEFT</text>
        <text transform={`translate(${box.x + box.w - 20} ${box.y + box.h / 2}) rotate(90)`} textAnchor="middle">RIGHT</text>
      </g>

      {/* Sampling areas of the selected strip / segment */}
      {overlay?.rects && (
        <g clipPath="url(#screen-clip)" pointerEvents="none">
          {overlay.rects.map((r, i) => {
            if (r.disabled) return null;
            const segIdx = overlay.ranges.findIndex(([a, b]) => i >= a && i < b);
            const focus = selection.segmentIndex === null || selection.segmentIndex === segIdx;
            const isFound = foundOnSelected === i;
            return (
              <rect
                key={i}
                x={box.x + r.hmin * box.w}
                y={box.y + r.vmin * box.h}
                width={Math.max(0, (r.hmax - r.hmin) * box.w)}
                height={Math.max(0, (r.vmax - r.vmin) * box.h)}
                fill={isFound ? '#ffffff' : color}
                fillOpacity={isFound ? 0.45 : focus ? 0.14 : 0.04}
                stroke={color}
                strokeOpacity={focus ? 0.55 : 0.12}
                strokeWidth={1}
              />
            );
          })}
        </g>
      )}
      {overlay?.error && (
        <text x={box.x + box.w / 2} y={box.y + box.h / 2} textAnchor="middle" fill="#6a7285" fontSize={14}>
          Sampling preview unavailable ({overlay.error})
        </text>
      )}

      {/* Strips (selected one on top) */}
      {spec.strips
        .map((s, i) => ({ s, i }))
        .sort((a, b) => Number(a.s.id === selection.stripId) - Number(b.s.id === selection.stripId))
        .map(({ s, i }) => (
          <StripGraphic
            key={s.id}
            strip={s}
            index={i}
            box={box}
            spec={spec}
            selected={s.id === selection.stripId}
            dimmed={!!selection.stripId && s.id !== selection.stripId}
            selectedSegment={s.id === selection.stripId ? selection.segmentIndex : null}
            found={found && found.stripId === s.id ? found.local : null}
            onSelect={onSelect}
          />
        ))}
    </svg>
  );
}
