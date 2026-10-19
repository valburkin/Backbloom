import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { hexToRgb, hsvToRgb, rgbToHex, rgbToHsv, type Hsv, type Rgb } from '../color/color';
import { cx } from './ui';

export const SWATCHES: ReadonlyArray<{ name: string; rgb: Rgb }> = [
  { name: 'Warm white', rgb: [255, 170, 90] },
  { name: 'White', rgb: [255, 255, 255] },
  { name: 'Red', rgb: [255, 0, 0] },
  { name: 'Orange', rgb: [255, 100, 0] },
  { name: 'Yellow', rgb: [255, 220, 0] },
  { name: 'Green', rgb: [0, 255, 60] },
  { name: 'Cyan', rgb: [0, 220, 255] },
  { name: 'Blue', rgb: [0, 40, 255] },
  { name: 'Purple', rgb: [150, 0, 255] },
  { name: 'Pink', rgb: [255, 0, 140] },
];

const clamp01 = (n: number) => Math.min(1, Math.max(0, n));

/**
 * HSV color picker: saturation/brightness area, hue slider, hex field and swatches. `onChange` fires
 * continuously while dragging; callers throttle what they send to the lights.
 */
export function ColorPicker({
  value,
  onChange,
  label = 'Color',
  swatches = true,
  compact,
}: {
  value: Rgb;
  onChange: (rgb: Rgb) => void;
  label?: string;
  swatches?: boolean;
  compact?: boolean;
}) {
  // Keep hue/saturation while the color is black or gray (they can't be recovered from RGB).
  const [hsv, setHsv] = useState<Hsv>(() => rgbToHsv(value));
  const last = useRef<Rgb>(value);
  useEffect(() => {
    if (rgbToHex(value) === rgbToHex(last.current)) return;
    last.current = value;
    const next = rgbToHsv(value);
    setHsv((cur) => (next.s === 0 || next.v === 0 ? { ...next, h: cur.h, s: next.v === 0 ? cur.s : next.s } : next));
  }, [value]);
  const [hexDraft, setHexDraft] = useState<string | null>(null);
  const area = useRef<HTMLDivElement>(null);

  const emit = (next: Hsv) => {
    setHsv(next);
    const rgb = hsvToRgb(next);
    last.current = rgb;
    onChange(rgb);
  };

  const fromPointer = (e: PointerEvent<HTMLDivElement>) => {
    const r = area.current?.getBoundingClientRect();
    if (!r) return;
    emit({ ...hsv, s: clamp01((e.clientX - r.left) / r.width), v: clamp01(1 - (e.clientY - r.top) / r.height) });
  };

  const onKey = (e: KeyboardEvent) => {
    const step = e.shiftKey ? 0.1 : 0.02;
    const d: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] };
    const m = d[e.key];
    if (!m) return;
    e.preventDefault();
    emit({ ...hsv, s: clamp01(hsv.s + m[0]), v: clamp01(hsv.v + m[1]) });
  };

  const hueColor = rgbToHex(hsvToRgb({ h: hsv.h, s: 1, v: 1 }));
  const hex = rgbToHex(value);

  return (
    <div className="space-y-3" role="group" aria-label={label}>
      <div
        ref={area}
        role="slider"
        tabIndex={0}
        aria-label={`${label}: saturation and brightness`}
        aria-valuetext={`saturation ${Math.round(hsv.s * 100)}%, brightness ${Math.round(hsv.v * 100)}%`}
        aria-valuenow={Math.round(hsv.v * 100)}
        aria-valuemin={0}
        aria-valuemax={100}
        onKeyDown={onKey}
        onPointerDown={(e) => {
          e.currentTarget.setPointerCapture(e.pointerId);
          fromPointer(e);
        }}
        onPointerMove={(e) => {
          if (e.buttons & 1) fromPointer(e);
        }}
        className={cx('relative w-full cursor-crosshair touch-none rounded-xl', compact ? 'h-32' : 'h-44')}
        style={{ background: `linear-gradient(to top, #000, transparent), linear-gradient(to right, #fff, ${hueColor})` }}
      >
        <span
          aria-hidden
          className="pointer-events-none absolute h-4 w-4 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white shadow-[0_0_0_1px_rgba(0,0,0,0.5)]"
          style={{ left: `${hsv.s * 100}%`, top: `${(1 - hsv.v) * 100}%`, background: hex }}
        />
      </div>
      <input
        type="range"
        min={0}
        max={359}
        step={1}
        value={Math.round(hsv.h)}
        aria-label={`${label}: hue`}
        onChange={(e) => emit({ ...hsv, h: Number(e.target.value), s: hsv.s || 1, v: hsv.v || 1 })}
        className="hue-range w-full"
      />
      <div className="flex items-center gap-2">
        <span className="h-9 w-9 shrink-0 rounded-lg border border-line-strong" style={{ background: hex }} aria-hidden />
        <input
          aria-label={`${label}: hex code`}
          value={hexDraft ?? hex}
          spellCheck={false}
          onChange={(e) => {
            setHexDraft(e.target.value);
            const rgb = hexToRgb(e.target.value);
            if (rgb) {
              last.current = rgb;
              setHsv(rgbToHsv(rgb));
              onChange(rgb);
            }
          }}
          onBlur={() => setHexDraft(null)}
          className="h-9 w-28 rounded-lg border border-line bg-bg px-2.5 font-mono text-[13px] uppercase text-ink focus:border-accent"
        />
        <span className="text-xs tabular-nums text-ink-faint">
          {value[0]}, {value[1]}, {value[2]}
        </span>
      </div>
      {swatches && (
        <div className="flex flex-wrap gap-1.5">
          {SWATCHES.map((s) => (
            <button
              key={s.name}
              type="button"
              title={s.name}
              aria-label={s.name}
              onClick={() => {
                last.current = s.rgb;
                setHsv(rgbToHsv(s.rgb));
                onChange(s.rgb);
              }}
              className={cx('h-7 w-7 rounded-full border border-white/10 transition-transform hover:scale-110', rgbToHex(s.rgb) === hex && 'ring-2 ring-white ring-offset-2 ring-offset-surface')}
              style={{ background: rgbToHex(s.rgb) }}
            />
          ))}
        </div>
      )}
    </div>
  );
}
