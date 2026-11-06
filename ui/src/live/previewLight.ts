// The previews (Home, Effects, Layout's live colors, the first-run setup) show what the LEDs light right now, so they
// follow Home's Brightness and keep every color as it is:
// - An LED's light is proportional to the value it gets (the backend already encoded the colors as sRGB and applied the
//   contrast; strips show the values as they are), while a screen shows its values on the sRGB curve. So each channel
//   is drawn at the sRGB encoding of its light: the color the LED really gives, a dim LED still visible.
// - Most people run their lights well below full, where the LEDs get only a few of 255 steps and a faithful preview
//   would be nearly black. Like eyes in a dim room, the preview adapts: it brightens the frame by part of what the
//   brightness took away (`share`, sent by the backend with each frame: the share of the full output it left), so it
//   still gets darker along the slider but stays readable. A color that would pass full keeps its hue (all channels
//   scaled together), so nothing turns white. LEDs that get 0 are off and stay off.

function encode(linear: number): number {
  return linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.pow(linear, 1 / 2.4) - 0.055;
}

/** How much of the brightness the preview makes up for: 1 would always look like full brightness, 0 not at all. */
const ADAPT = 0.6;

/** Screen values 0..255 for each light level 0..1024 (finer than the LEDs' 256 steps, so brightened colors stay smooth). */
const STEPS = 1024;
const SCREEN = Array.from({ length: STEPS + 1 }, (_, i) => Math.round(255 * encode(i / STEPS)));

/**
 * The share the preview uses `elapsedMs` after it used `shown`, on its way to the frame's `target`: the backend sends
 * the new share at once when the brightness changes, while the transitions (smoothing) still fade the colors there,
 * so the preview eases toward it (in steps of light, i.e. on a log scale) over about the same time instead of dipping
 * dark or flashing bright. Close enough, it is the target.
 */
export function easeShare(shown: number, target: number, elapsedMs: number, timeMs = EASE_MS): number {
  if (!(shown > 0) || !(target > 0) || !Number.isFinite(shown) || !Number.isFinite(target)) return target;
  const k = 1 - Math.exp(-Math.max(0, elapsedMs) / timeMs);
  const next = Math.exp(Math.log(shown) + (Math.log(target) - Math.log(shown)) * k);
  return Math.abs(Math.log(next / target)) < 0.01 ? target : next;
}

/** Time constant of easeShare: about the transitions of the default Looks. */
export const EASE_MS = 200;

/** How much the preview brightens a frame whose brightness left `share` of the full output. */
export function exposure(share: number | undefined): number {
  return share !== undefined && Number.isFinite(share) && share > 0 && share < 1 ? Math.pow(share, -ADAPT) : 1;
}

/** The frame as the screen should draw it (see above). */
export function previewColors(frame: readonly number[] | null, share?: number): number[] | null {
  if (!frame) return null;
  const gain = exposure(share) / 255;
  const out = new Array<number>(frame.length);
  for (let i = 0; i + 2 < frame.length; i += 3) {
    const peak = Math.max(frame[i], frame[i + 1], frame[i + 2]) * gain;
    const k = peak > 1 ? gain / peak : gain;
    for (let c = 0; c < 3; c++) out[i + c] = SCREEN[Math.min(STEPS, Math.max(0, Math.round(frame[i + c] * k * STEPS)))];
  }
  for (let i = frame.length - (frame.length % 3); i < frame.length; i++) out[i] = frame[i];
  return out;
}
