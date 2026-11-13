// The Wind card's live readout: a simulated lap where the speed climbs and brakes, and the wind follows it with a
// fan's lag, the way Backbloom will drive fans from a game's speed. The driver's hair flutters (a moving distortion on
// a copy of the picture, limited to the hair) and soft wisps of air drift past the head, both stronger with more wind.

import { coverMap } from './cover';
import { runWhileVisible } from './loop';

// Target speeds (km/h) and how long the car takes to reach each: straights, braking zones, corner exits.
const LAP: [number, number][] = [
  [292, 4.5],
  [96, 1.1],
  [214, 2.6],
  [128, 0.9],
  [268, 3.4],
  [74, 1.3],
  [188, 2.2],
];
const MAX_KMH = 300;

// The air in wind.webp comes from the screen side (left), rushes past the driver's face and leaves up and to the right,
// the way the hair is blown. Fractions of the picture.
const SPAWN = { x0: 0.3, x1: 0.55, y0: 0.32, y1: 0.58 };
const HEAD = { x: 0.69, y: 0.42, r: 0.1 };
const TRAIL = 12;
// Center of the driver's hair, where the flutter is shown (see .hair-flutter).
const HAIR = { x: 0.73, y: 0.28 };

interface Streak { pts: { x: number; y: number }[]; x: number; y: number; life: number; width: number; seed: number }

/** Air direction at a point: rightward, turning up past the head like the hair, flowing around the face. */
function flow(x: number, y: number, t: number, seed: number): [number, number] {
  const turn = Math.min(1, Math.max(0, (x - 0.5) / 0.4));
  let vx = 1;
  let vy = -0.55 * turn * turn - 0.08;
  vy += Math.sin(t * 3 + seed * 7 + x * 9) * 0.12; // a little turbulence
  const dx = x - HEAD.x, dy = y - HEAD.y, d = Math.hypot(dx, dy);
  if (d < HEAD.r * 1.6) {
    const push = (1 - d / (HEAD.r * 1.6)) * 1.4;
    vx += (dx / d) * push;
    vy += (dy / d) * push;
  }
  const n = Math.hypot(vx, vy);
  return [vx / n, vy / n];
}

export function startWind(card: HTMLElement): void {
  const img = card.querySelector<HTMLImageElement>('img')!;
  const speedEl = card.querySelector<HTMLElement>('[data-speed]')!;
  const windEl = card.querySelector<HTMLElement>('[data-wind]')!;
  const bar = card.querySelector<HTMLElement>('[data-windbar]')!;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  // The hair: a copy of the picture under an SVG turbulence displacement, masked to the hair (see .hair-flutter).
  const svgNS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNS, 'svg');
  svg.setAttribute('class', 'fx-defs');
  svg.setAttribute('aria-hidden', 'true');
  // The filter area is larger than the picture, so the swaying noise never leaves a gap (which would cut the hair).
  svg.innerHTML = `<filter id="hair-flutter" x="-40%" y="-40%" width="180%" height="180%">
      <feTurbulence type="fractalNoise" baseFrequency="0.035 0.09" numOctaves="2" seed="3" result="noise" />
      <feOffset in="noise" dx="0" dy="0" result="moving" />
      <feDisplacementMap in="SourceGraphic" in2="moving" scale="0" xChannelSelector="R" yChannelSelector="G" />
    </filter>`;
  card.appendChild(svg);
  const offset = svg.querySelector('feOffset')!;
  const displace = svg.querySelector('feDisplacementMap')!;
  const hair = document.createElement('div');
  hair.className = 'hair-flutter';
  hair.setAttribute('aria-hidden', 'true');
  const hairImg = img.cloneNode() as HTMLImageElement;
  hair.appendChild(hairImg);
  img.after(hair);
  let flutter = 0;

  const canvas = document.createElement('canvas');
  canvas.className = 'card-fx';
  canvas.setAttribute('aria-hidden', 'true');
  img.after(canvas);
  const ctx = canvas.getContext('2d');
  const streaks: Streak[] = [];
  let spawn = 0;
  const fit = () => {
    canvas.width = Math.round(canvas.clientWidth * Math.min(2, devicePixelRatio));
    canvas.height = Math.round(canvas.clientHeight * Math.min(2, devicePixelRatio));
    // The hair mask follows the picture's crop like the other effects.
    const [hx, hy] = coverMap(img, canvas.clientWidth, canvas.clientHeight)(HAIR.x, HAIR.y);
    hair.style.setProperty('--hx', `${hx.toFixed(0)}px`);
    hair.style.setProperty('--hy', `${hy.toFixed(0)}px`);
  };
  new ResizeObserver(fit).observe(canvas);
  img.addEventListener('load', fit);

  let speed = 120;
  let wind = 0.35;
  let leg = 0;
  let from = speed;
  let legT = 0;
  let textTick = 0;

  const show = () => {
    speedEl.textContent = String(Math.round(speed));
    windEl.textContent = `${Math.round(wind * 100)}%`;
    bar.style.transform = `scaleX(${wind.toFixed(3)})`;
    img.style.filter = hairImg.style.filter = `saturate(0.95) brightness(${(0.8 + wind * 0.35).toFixed(3)})`;
  };

  const tick = (dt: number) => {
    const [to, dur] = LAP[leg]!;
    legT += dt;
    const k = Math.min(1, legT / dur);
    // Braking is sharp, accelerating eases out.
    const e = to < from ? 1 - (1 - k) ** 3 : k * k * (3 - 2 * k);
    speed = from + (to - from) * e;
    if (k >= 1) {
      leg = (leg + 1) % LAP.length;
      from = to;
      legT = 0;
    }
    // Fans spin up and down with a lag.
    const target = Math.max(0, (speed - 40) / (MAX_KMH - 40));
    wind += (target - wind) * (1 - Math.exp(-dt / 0.45));
    drawAir(dt);
    // The noise sways back and forth across the hair, faster with more wind, so strands seem to whip about.
    flutter += dt * (2 + wind * 9);
    offset.setAttribute('dx', (Math.sin(flutter) * 40 + Math.sin(flutter * 2.3) * 12).toFixed(1));
    offset.setAttribute('dy', (Math.sin(flutter * 1.7) * 10).toFixed(1));
    displace.setAttribute('scale', (1.5 + wind * 6).toFixed(1));
    textTick += dt;
    bar.style.transform = `scaleX(${wind.toFixed(3)})`;
    img.style.filter = hairImg.style.filter = `saturate(0.95) brightness(${(0.8 + wind * 0.35).toFixed(3)})`;
    if (textTick > 0.08) {
      textTick = 0;
      speedEl.textContent = String(Math.round(speed));
      windEl.textContent = `${Math.round(wind * 100)}%`;
    }
  };

  let clock = 0;
  function drawAir(dt: number) {
    if (!ctx || !canvas.width) return;
    const W = canvas.width, H = canvas.height;
    clock += dt;
    spawn += dt * (2 + wind * 9);
    while (spawn >= 1) {
      spawn -= 1;
      const x = SPAWN.x0 + Math.random() * (SPAWN.x1 - SPAWN.x0);
      const y = SPAWN.y0 + Math.random() * (SPAWN.y1 - SPAWN.y0);
      streaks.push({ pts: [], x, y, life: 1, width: 4 + Math.random() * 8, seed: Math.random() });
    }
    const map = coverMap(img, W, H);
    ctx.clearRect(0, 0, W, H);
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.filter = `blur(${(W / 160).toFixed(1)}px)`;
    const v = 0.22 + wind * 0.75; // picture widths per second
    for (let i = streaks.length - 1; i >= 0; i--) {
      const s = streaks[i]!;
      const [fx, fy] = flow(s.x, s.y, clock, s.seed);
      s.x += fx * v * dt;
      s.y += fy * v * dt;
      s.pts.push({ x: s.x, y: s.y });
      if (s.pts.length > TRAIL) s.pts.shift();
      s.life -= dt * 0.9;
      if (s.life <= 0 || s.x > 1.05 || s.y < -0.05) { streaks.splice(i, 1); continue; }
      if (s.pts.length < 3) continue;
      // A soft trail: transparent at its tail, brightest at its head, fading in and out over its life.
      const head = s.pts[s.pts.length - 1]!, tail = s.pts[0]!;
      const a = Math.sin(Math.min(1, s.life) * Math.PI) * (0.05 + wind * 0.12);
      const [tx, ty] = map(tail.x, tail.y), [hx, hy] = map(head.x, head.y);
      const g = ctx.createLinearGradient(tx, ty, hx, hy);
      g.addColorStop(0, 'rgba(190,225,255,0)');
      g.addColorStop(1, `rgba(220,240,255,${a.toFixed(3)})`);
      ctx.strokeStyle = g;
      ctx.lineWidth = s.width * (W / 600);
      ctx.beginPath();
      ctx.moveTo(tx, ty);
      for (let k = 1; k < s.pts.length - 1; k++) {
        const p = s.pts[k]!, q = s.pts[k + 1]!;
        const [px, py] = map(p.x, p.y), [mx, my] = map((p.x + q.x) / 2, (p.y + q.y) / 2);
        ctx.quadraticCurveTo(px, py, mx, my);
      }
      ctx.stroke();
    }
    ctx.filter = 'none';
  }

  if (reduced) {
    speed = 248;
    wind = (speed - 40) / (MAX_KMH - 40);
    show();
  } else {
    show();
    runWhileVisible(card, tick);
  }
}
