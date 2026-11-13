// The Haptics card's live readout: simulated game events (explosions, hits, landings) over an engine rumble drive the
// bass level a shaker would get. On each impact the chair lights up as the source: echoes of its backrest pulse
// outward from it, with a violet glow, and the picture jolts a little.

import { coverMap } from './cover';
import { runWhileVisible } from './loop';

// Events in a loop: name, strength (0-1), seconds until the next one.
const EVENTS: [string, number, number][] = [
  ['Explosion', 1, 2.2],
  ['Hit', 0.62, 1.1],
  ['Hit', 0.58, 1.6],
  ['Landing', 0.85, 2.4],
  ['Gunfire', 0.45, 0.35],
  ['Gunfire', 0.45, 0.35],
  ['Gunfire', 0.45, 1.8],
];
const IDLE = 'Engine rumble';

// The chair's outline in haptics-live.webp, as fractions of the picture: up the left edge, over the rounded top-left
// corner and the top, then down the right side where the gamer's back meets the side bolster. Echoes grow from the
// center point.
const CHAIR_EDGE: [number, number][] = [
  [0.201, 1.0], [0.172, 0.961], [0.138, 0.924], [0.112, 0.87], [0.099, 0.812], [0.088, 0.747], [0.082, 0.689],
  [0.084, 0.66], [0.093, 0.638], [0.112, 0.63], [0.149, 0.627], [0.224, 0.623], [0.257, 0.613], [0.299, 0.619],
  [0.351, 0.66], [0.407, 0.715], [0.455, 0.747], [0.5, 0.775], [0.549, 0.812], [0.597, 0.845], [0.649, 0.882],
  [0.699, 0.915], [0.739, 0.99], [0.75, 1.02],
];
const CHAIR_CENTER = { x: 0.33, y: 0.9 };

interface Echo { age: number; strength: number }

export function startHaptics(card: HTMLElement): void {
  const img = card.querySelector<HTMLImageElement>('img')!;
  const eventEl = card.querySelector<HTMLElement>('[data-event]')!;
  const bassEl = card.querySelector<HTMLElement>('[data-bass]')!;
  const bar = card.querySelector<HTMLElement>('[data-bassbar]')!;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const canvas = document.createElement('canvas');
  canvas.className = 'card-fx';
  canvas.setAttribute('aria-hidden', 'true');
  img.after(canvas);
  const ctx = canvas.getContext('2d');
  const fit = () => {
    canvas.width = Math.round(canvas.clientWidth * Math.min(2, devicePixelRatio));
    canvas.height = Math.round(canvas.clientHeight * Math.min(2, devicePixelRatio));
  };
  new ResizeObserver(fit).observe(canvas);
  const echoes: Echo[] = [];

  let i = 0;
  let next = 0.8;
  let impact = 0; // envelope of the last event
  let flash = 0; // the chair's glow: a quicker flare than the bass
  let label = IDLE;
  let t = 0;
  let textTick = 0;

  const render = (bass: number, jolt: number) => {
    bar.style.transform = `scaleX(${bass.toFixed(3)})`;
    const x = (Math.random() - 0.5) * 3 * jolt, y = (Math.random() - 0.5) * 3 * jolt;
    // The glow layer moves with the picture, or it would drift off the chair.
    img.style.transform = canvas.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) scale(1.04)`;
    img.style.filter = `saturate(0.95) brightness(${(0.82 + bass * 0.35).toFixed(3)})`;
  };

  const tick = (dt: number) => {
    t += dt;
    next -= dt;
    if (next <= 0) {
      const [name, strength, gap] = EVENTS[i]!;
      impact = Math.max(impact, strength);
      flash = Math.max(flash, strength);
      label = name;
      // Stronger events send more echoes.
      for (let k = 0; k < (strength > 0.8 ? 3 : strength > 0.55 ? 2 : 1); k++) echoes.push({ age: -k * 0.12, strength });
      next = gap;
      i = (i + 1) % EVENTS.length;
    }
    impact *= Math.exp(-dt / 0.55);
    flash *= Math.exp(-dt / 0.2);
    // The event shows while its impact is felt.
    if (impact < 0.28) label = IDLE;
    // The engine rumbles underneath everything.
    const rumble = 0.2 + 0.06 * Math.sin(t * 9) + 0.04 * Math.sin(t * 23);
    const bass = Math.min(1, Math.max(rumble, impact));
    render(bass, impact > 0.3 ? impact : 0);
    // Squared, so the glow eases out into a soft tail instead of stopping at a threshold.
    drawChair(dt, flash * flash);
    textTick += dt;
    if (textTick > 0.08) {
      textTick = 0;
      eventEl.textContent = label;
      bassEl.textContent = `${Math.round(bass * 100)}%`;
    }
  };

  // The chair's outline, scaled up from its center: s = 1 is the chair itself.
  function edgePath(s: number, map: (fx: number, fy: number) => [number, number]) {
    ctx!.beginPath();
    CHAIR_EDGE.forEach(([x, y], k) => {
      const [px, py] = map(CHAIR_CENTER.x + (x - CHAIR_CENTER.x) * s, CHAIR_CENTER.y + (y - CHAIR_CENTER.y) * s);
      if (k === 0) ctx!.moveTo(px, py);
      else ctx!.lineTo(px, py);
    });
  }

  function drawChair(dt: number, glow: number) {
    if (!ctx || !canvas.width) return;
    const W = canvas.width, H = canvas.height;
    const map = coverMap(img, W, H);
    const [cx, cy] = map(CHAIR_CENTER.x, CHAIR_CENTER.y);
    ctx.clearRect(0, 0, W, H);
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    // A violet glow on the chair while the impact is felt, kept inside its outline so it hugs the backrest instead
    // of spilling onto the gamer.
    if (glow > 0.004) {
      ctx.save();
      edgePath(1, map);
      const [rx, ry] = map(CHAIR_EDGE[CHAIR_EDGE.length - 1]![0], 1.2), [lx, ly] = map(CHAIR_EDGE[0]![0] - 0.1, 1.2);
      ctx.lineTo(rx, ry);
      ctx.lineTo(lx, ly);
      ctx.closePath();
      ctx.clip();
      const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, 0.6 * W);
      g.addColorStop(0, `rgba(139,92,246,${(glow * 0.55).toFixed(3)})`);
      g.addColorStop(1, 'rgba(139,92,246,0)');
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, W, H);
      // The backrest's edge lights up like a rim light: a wide soft glow under a thinner bright line.
      ctx.filter = `blur(${(W / 70).toFixed(1)}px)`;
      edgePath(1, map);
      ctx.strokeStyle = `rgba(167,139,250,${Math.min(1, glow * 1.3).toFixed(3)})`;
      ctx.lineWidth = W / 9;
      ctx.stroke();
      ctx.filter = `blur(${(W / 400).toFixed(1)}px)`;
      edgePath(1, map);
      ctx.strokeStyle = `rgba(230,215,255,${Math.min(1, glow * 1.1).toFixed(3)})`;
      ctx.lineWidth = W / 90;
      ctx.stroke();
      ctx.filter = 'none';
      ctx.restore();
    }
    // Echoes of the backrest travel outward and fade.
    for (let k = echoes.length - 1; k >= 0; k--) {
      const e = echoes[k]!;
      e.age += dt;
      if (e.age < 0) continue;
      const life = e.age / 0.7;
      if (life >= 1) { echoes.splice(k, 1); continue; }
      // Soft bands: wide and blurred, widening and fading as they travel.
      ctx.filter = `blur(${((W / 120) * (1 + life * 2)).toFixed(1)}px)`;
      edgePath(1 + life * 0.5, map);
      ctx.strokeStyle = `rgba(167,139,250,${((1 - life) ** 1.6 * 0.6 * e.strength).toFixed(3)})`;
      ctx.lineWidth = (W / 40) * (1 + life);
      ctx.stroke();
      ctx.filter = 'none';
    }
  }

  if (reduced) {
    eventEl.textContent = 'Explosion';
    bassEl.textContent = '92%';
    bar.style.transform = 'scaleX(0.92)';
  } else {
    runWhileVisible(card, tick);
  }
}
