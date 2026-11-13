// The music feature as a live showcase: a simulated beat drives the room photo's light, an EQ, and the LED preview
// drawn over the app's Effects & Music screenshot (like the app's own preview of a music effect).

import { runWhileVisible } from './loop';

const BPM = 120;
const BANDS = 16;

// LED layout of the screenshot's preview, in pixels of the preview area (1220 x 351 in playing-music.webp, measured
// from the same card with every LED lit white). The photo has the same strips (ceiling, behind the monitor, along the
// desk), all lit in one color that its filter shifts, so the preview keeps every strip in that color's family too.
const PREVIEW_W = 1220;
const PREVIEW_H = 351;
const CEILING = { x0: 34, x1: 1185, y: 34, n: 120 };
const MONITOR = { x0: 512, y0: 151, x1: 707, y1: 261, nh: 32, nv: 18 };
const DESK = { x0: 397, x1: 822, y: 315, n: 60 };
const ROOM_BASE_HUE = 305; // the photo's own magenta

export function startMusic(frame: HTMLElement): void {
  const room = frame.querySelector<HTMLImageElement>('.music-room')!;
  const eq = frame.querySelector<HTMLElement>('.eq')!;
  const canvas = frame.querySelector<HTMLCanvasElement>('.live-preview')!;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  canvas.width = PREVIEW_W;
  canvas.height = PREVIEW_H;

  const bars = Array.from({ length: BANDS }, () => {
    const b = document.createElement('span');
    eq.appendChild(b);
    return b;
  });
  const bands = new Float32Array(BANDS);
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

  let t = 0;
  let level = 0;

  const draw = () => {
    const beat = (t * BPM) / 60;
    const phase = beat % 1;
    const kick = Math.exp(-phase * 7);
    // A snare on beats 2 and 4, hi-hats on the off-beats.
    const bar = Math.floor(beat / 4);
    const inBar = beat % 4;
    const snare = Math.floor(inBar) % 2 === 1 ? Math.exp(-phase * 9) : 0;
    const hat = Math.exp(-((beat + 0.5) % 1) * 14);

    for (let i = 0; i < BANDS; i++) {
      const k = i / (BANDS - 1);
      const target = Math.min(
        1,
        (k < 0.25 ? kick * (1 - k * 2) : 0) +
          (k > 0.3 && k < 0.7 ? snare * 0.75 : 0) +
          (k > 0.75 ? hat * 0.6 : 0) +
          (0.22 + 0.16 * Math.sin(t * (2.1 + i * 0.63) + i * 1.7)) * (1 - k * 0.5),
      );
      // Fast attack, slower release, like a real meter.
      bands[i] = target > bands[i]! ? target : bands[i]! * 0.86 + target * 0.14;
      bars[i]!.style.transform = `scaleY(${(0.06 + bands[i]! * 0.94).toFixed(3)})`;
    }
    const bass = (bands[0]! + bands[1]! + bands[2]!) / 3;
    level = Math.max(bass, level * 0.9);

    // Hue drifts and jumps a step each bar.
    const hue = (t * 18 + bar * 47) % 360;
    room.style.filter = `hue-rotate(${(hue - ROOM_BASE_HUE).toFixed(1)}deg) brightness(${(0.72 + kick * 0.5).toFixed(3)}) saturate(1.25)`;
    eq.style.setProperty('--h', hue.toFixed(0));

    ctx.globalCompositeOperation = 'source-over';
    ctx.fillStyle = 'rgb(8 9 12)';
    ctx.fillRect(0, 0, PREVIEW_W, PREVIEW_H);
    ctx.globalCompositeOperation = 'lighter';

    // Ceiling: a level meter from the middle out.
    const mid = (CEILING.n - 1) / 2;
    for (let i = 0; i < CEILING.n; i++) {
      const d = Math.abs(i - mid) / mid;
      const on = d <= level * 1.05 ? 1 : 0.06;
      led(CEILING.x0 + ((CEILING.x1 - CEILING.x0) * i) / (CEILING.n - 1), CEILING.y, hue + d * 25, on * (0.55 + kick * 0.45));
    }
    // Monitor: the beat around the screen.
    const mb = 0.35 + kick * 0.65;
    const { x0, y0, x1, y1, nh, nv } = MONITOR;
    for (let i = 0; i <= nh; i++) {
      const x = x0 + ((x1 - x0) * i) / nh;
      led(x, y0, hue, mb);
      led(x, y1, hue, mb);
    }
    for (let i = 1; i < nv; i++) {
      const y = y0 + ((y1 - y0) * i) / nv;
      led(x0, y, hue, mb);
      led(x1, y, hue, mb);
    }
    // Desk: the spectrum, bass on the left.
    for (let i = 0; i < DESK.n; i++) {
      const b = bands[Math.min(BANDS - 1, Math.floor((i / DESK.n) * BANDS))]!;
      led(DESK.x0 + ((DESK.x1 - DESK.x0) * i) / (DESK.n - 1), DESK.y, hue - 15 + (i / DESK.n) * 30, b);
    }
  };

  function led(x: number, y: number, hue: number, v: number) {
    if (v < 0.02) return;
    const c = `hsl(${hue % 360} 100% 55% / ${Math.min(1, v * 0.8).toFixed(3)})`;
    // light that adds up along the strip and falls off like the app's preview, then the LED itself
    const g = ctx!.createRadialGradient(x, y, 0, x, y, 26);
    g.addColorStop(0, c);
    g.addColorStop(0.3, `hsl(${hue % 360} 100% 55% / ${Math.min(1, v * 0.25).toFixed(3)})`);
    g.addColorStop(1, 'transparent');
    ctx!.fillStyle = g;
    ctx!.fillRect(x - 26, y - 26, 52, 52);
    ctx!.fillStyle = `hsl(${hue % 360} 100% ${(55 + v * 20).toFixed(0)}% / ${Math.min(1, v + 0.1).toFixed(3)})`;
    ctx!.beginPath();
    ctx!.arc(x, y, 3.5, 0, Math.PI * 2);
    ctx!.fill();
  }

  const tick = (dt: number) => {
    if (!frame.classList.contains('on')) return;
    t += dt;
    draw();
  };

  if (reduced) {
    t = 0.05; // just after a beat: a lively still
    draw();
  } else {
    runWhileVisible(frame, tick);
  }
}
