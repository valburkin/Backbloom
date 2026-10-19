export type Rgb = readonly [number, number, number];

export interface Hsv {
  /** 0..360 */
  h: number;
  /** 0..1 */
  s: number;
  /** 0..1 */
  v: number;
}

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));

export function hsvToRgb({ h, s, v }: Hsv): Rgb {
  const hh = (((h % 360) + 360) % 360) / 60;
  const c = v * s;
  const x = c * (1 - Math.abs((hh % 2) - 1));
  const m = v - c;
  const [r, g, b] =
    hh < 1 ? [c, x, 0] : hh < 2 ? [x, c, 0] : hh < 3 ? [0, c, x] : hh < 4 ? [0, x, c] : hh < 5 ? [x, 0, c] : [c, 0, x];
  return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

export function rgbToHsv([r8, g8, b8]: Rgb): Hsv {
  const r = r8 / 255;
  const g = g8 / 255;
  const b = b8 / 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const d = max - min;
  let h = 0;
  if (d > 0) {
    if (max === r) h = 60 * (((g - b) / d) % 6);
    else if (max === g) h = 60 * ((b - r) / d + 2);
    else h = 60 * ((r - g) / d + 4);
  }
  return { h: (h + 360) % 360, s: max === 0 ? 0 : d / max, v: max };
}

export function rgbToHex([r, g, b]: Rgb): string {
  return '#' + [r, g, b].map((n) => clamp(Math.round(n), 0, 255).toString(16).padStart(2, '0')).join('');
}

/** Parses "#rgb", "#rrggbb" (with or without #); null when it isn't a color. */
export function hexToRgb(hex: string): Rgb | null {
  const m = hex.trim().replace(/^#/, '');
  if (/^[0-9a-f]{3}$/i.test(m)) return [0, 1, 2].map((i) => parseInt(m[i] + m[i], 16)) as unknown as Rgb;
  if (/^[0-9a-f]{6}$/i.test(m)) return [0, 2, 4].map((i) => parseInt(m.slice(i, i + 2), 16)) as unknown as Rgb;
  return null;
}

export function isRgb(v: unknown): v is Rgb {
  return Array.isArray(v) && v.length === 3 && v.every((n) => typeof n === 'number' && n >= 0 && n <= 255);
}

/** Readable text color on top of a color swatch. */
export function contrastInk([r, g, b]: Rgb): string {
  return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? '#0b0d12' : '#ffffff';
}
