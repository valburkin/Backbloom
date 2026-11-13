// The hero's live demo: game scenes play on the monitor, the picture's edges are sampled into "LEDs" and drawn as glow
// on the wall behind it, smoothed and color-boosted like Backbloom does.

interface Scene {
  src: string;
  /** Color of the bursts ("game events") and particles drawn over the picture. */
  burst: [number, number, number];
  particle: [number, number, number];
  img?: HTMLImageElement;
}

const SCENES: Scene[] = [
  { src: '/img/scene-space.webp', burst: [120, 255, 240], particle: [220, 200, 255] },
  { src: '/img/scene-lava.webp', burst: [255, 120, 20], particle: [255, 170, 60] },
  { src: '/img/scene-city.webp', burst: [255, 60, 200], particle: [140, 220, 255] },
  { src: '/img/scene-forest.webp', burst: [255, 210, 90], particle: [140, 255, 170] },
];

const SCENE_TIME = 7;
const FADE_TIME = 0.9; // dip to black and back between scenes

// LEDs per side, like a strip around a 16:9 monitor.
const LEDS_H = 20;
const LEDS_V = 11;
const SAMPLE_W = 64;
const SAMPLE_H = 36;
const BAND = 5; // sample pixels deep from each edge

// Smoothing time constant (s), saturation boost, gamma (keeps dark scenes dark) and gain.
const LOOK = { tau: 0.18, sat: 1.35, gamma: 1.15, gain: 0.95 };

interface Led {
  side: 0 | 1 | 2 | 3; // top, right, bottom, left
  t: number; // 0..1 along the side
  x0: number; y0: number; x1: number; y1: number; // sample rect
  cur: [number, number, number];
}

interface Particle { x: number; y: number; vx: number; vy: number; life: number; size: number }
interface Burst { x: number; y: number; age: number; dur: number; color: [number, number, number] }

export function startHero(root: HTMLElement): void {
  const screen = root.querySelector<HTMLCanvasElement>('.screen')!;
  const glow = root.querySelector<HTMLCanvasElement>('.glow')!;
  const monitor = root.querySelector<HTMLElement>('.monitor')!;
  const sticky = root.querySelector<HTMLElement>('.hero-sticky')!;
  const sctx = screen.getContext('2d')!;
  const gctx = glow.getContext('2d')!;
  const sample = document.createElement('canvas');
  sample.width = SAMPLE_W;
  sample.height = SAMPLE_H;
  const pctx = sample.getContext('2d', { willReadFrequently: true })!;
  // Other elements tinted by the live colors (the nav, the cursor glow).
  const liveTargets = [...document.querySelectorAll<HTMLElement>('[data-live]')];

  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  // Paused (and from the start with reduced motion): the picture holds still; the glow still follows the lights
  // switch and the monitor's position for a moment after each change.
  let paused = reduced;
  let wakeUntil = 0;
  const wake = () => { wakeUntil = performance.now() + 700; };
  let lightsOn = true;
  let lightLevel = 1; // fades on/off
  let visible = true;

  const leds = buildLeds();
  const particles: Particle[] = [];
  const bursts: Burst[] = [];
  let nextBurst = 1.5;

  let loaded = 0;
  for (const s of SCENES) {
    const img = new Image();
    img.decoding = 'async';
    img.src = s.src;
    img.onload = () => { s.img = img; loaded++; wake(); };
  }

  const lightsBtn = root.querySelector<HTMLButtonElement>('[data-demo-lights]')!;
  lightsBtn.addEventListener('click', () => {
    lightsOn = !lightsOn;
    lightsBtn.classList.toggle('on', lightsOn);
    lightsBtn.setAttribute('aria-checked', String(lightsOn));
    root.classList.toggle('lights-off', !lightsOn);
    wake();
  });
  const pauseBtn = root.querySelector<HTMLButtonElement>('[data-demo-pause]')!;
  const showPaused = () => {
    pauseBtn.setAttribute('aria-pressed', String(paused));
    pauseBtn.classList.toggle('is-paused', paused);
  };
  showPaused();
  pauseBtn.addEventListener('click', () => { paused = !paused; showPaused(); wake(); });
  addEventListener('scroll', wake, { passive: true });

  new IntersectionObserver(([e]) => { visible = !!e?.isIntersecting; }, { threshold: 0 }).observe(root);

  // Glow canvas at a third of the resolution: it's all soft light.
  const GLOW_SCALE = 0.35;
  const resize = () => {
    glow.width = Math.max(1, Math.round(sticky.clientWidth * GLOW_SCALE));
    glow.height = Math.max(1, Math.round(sticky.clientHeight * GLOW_SCALE));
    wake();
  };
  resize();
  addEventListener('resize', resize);

  // 30 updates a second is plenty for light and halves the cost.
  const STEP = 1 / 30;
  let last = performance.now();
  let acc = 0;
  let time = FADE_TIME / 2;
  let cssTick = 0;

  const frame = (now: number) => {
    requestAnimationFrame(frame);
    const elapsed = Math.min(0.1, (now - last) / 1000);
    last = now;
    if (!visible || document.hidden || loaded === 0) return;
    if (paused && now > wakeUntil) return;
    acc += elapsed;
    if (acc < STEP) return;
    const dt = Math.min(0.1, acc);
    acc = 0;
    if (!paused) time += dt;

    drawScreen(time, paused ? 0 : dt);
    sampleLeds(dt);
    drawGlow();

    cssTick += dt;
    if (cssTick > 0.1) {
      cssTick = 0;
      publishColors();
    }
  };
  requestAnimationFrame(frame);

  function sceneAt(i: number): Scene {
    const ready = SCENES.filter((s) => s.img);
    return ready[((i % ready.length) + ready.length) % ready.length]!;
  }

  function drawScreen(t: number, dt: number) {
    const W = screen.width, H = screen.height;
    const idx = Math.floor(t / SCENE_TIME);
    const local = t - idx * SCENE_TIME;
    const cur = sceneAt(idx);
    sctx.globalAlpha = 1;
    sctx.globalCompositeOperation = 'source-over';
    drawKenBurns(cur, idx, local / SCENE_TIME, 1);
    // Fade out at the end of a scene and in at the start of the next (no double exposure).
    const edge = Math.min(local, SCENE_TIME - local) / (FADE_TIME / 2);
    if (edge < 1) {
      sctx.fillStyle = `rgba(0,0,0,${1 - edge * edge * (3 - 2 * edge)})`;
      sctx.fillRect(0, 0, W, H);
    }

    // Particles drifting through the picture.
    if (dt > 0 && particles.length < 70) {
      particles.push({ x: Math.random() * W, y: H + 10, vx: (Math.random() - 0.5) * 30, vy: -40 - Math.random() * 90, life: 1, size: 1 + Math.random() * 2.5 });
    }
    const [pr, pg, pb] = cur.particle;
    sctx.globalCompositeOperation = 'lighter';
    for (let i = particles.length - 1; i >= 0; i--) {
      const p = particles[i]!;
      p.x += p.vx * dt; p.y += p.vy * dt; p.life -= dt * 0.25;
      if (p.life <= 0 || p.y < -10) { particles.splice(i, 1); continue; }
      sctx.fillStyle = `rgba(${pr},${pg},${pb},${0.7 * p.life})`;
      sctx.beginPath();
      sctx.arc(p.x, p.y, p.size, 0, Math.PI * 2);
      sctx.fill();
    }

    // Bursts near an edge: the moments the Gaming preset is made for.
    nextBurst -= dt;
    if (nextBurst <= 0) {
      nextBurst = 2 + Math.random() * 2;
      const side = Math.floor(Math.random() * 4);
      const along = 0.15 + Math.random() * 0.7;
      const pos: [number, number] =
        side === 0 ? [along * W, H * 0.12] : side === 1 ? [W * 0.88, along * H] : side === 2 ? [along * W, H * 0.88] : [W * 0.12, along * H];
      bursts.push({ x: pos[0], y: pos[1], age: 0, dur: 0.9, color: cur.burst });
    }
    for (let i = bursts.length - 1; i >= 0; i--) {
      const b = bursts[i]!;
      b.age += dt;
      const k = b.age / b.dur;
      if (k >= 1) { bursts.splice(i, 1); continue; }
      const r = W * (0.08 + 0.35 * Math.sqrt(k));
      const a = (1 - k) ** 2;
      const g = sctx.createRadialGradient(b.x, b.y, 0, b.x, b.y, r);
      const [cr, cg, cb] = b.color;
      g.addColorStop(0, `rgba(255,255,255,${0.9 * a})`);
      g.addColorStop(0.2, `rgba(${cr},${cg},${cb},${0.85 * a})`);
      g.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
      sctx.fillStyle = g;
      sctx.fillRect(b.x - r, b.y - r, r * 2, r * 2);
    }
    sctx.globalCompositeOperation = 'source-over';
  }

  function drawKenBurns(s: Scene, idx: number, k: number, alpha: number) {
    const img = s.img!;
    const W = screen.width, H = screen.height;
    const dir = idx % 2 === 0 ? 1 : -1;
    const scale = 1.08 + 0.12 * k;
    const iw = W * scale, ih = (img.height / img.width) * iw;
    const x = (W - iw) / 2 + dir * (k - 0.5) * W * 0.08;
    const y = (H - ih) / 2 + (k - 0.5) * H * 0.03;
    sctx.globalAlpha = alpha;
    sctx.drawImage(img, x, y, iw, ih);
    sctx.globalAlpha = 1;
  }

  function buildLeds(): Led[] {
    const out: Led[] = [];
    const add = (side: Led['side'], n: number) => {
      for (let i = 0; i < n; i++) {
        const t = (i + 0.5) / n;
        const horiz = side === 0 || side === 2;
        const len = horiz ? SAMPLE_W : SAMPLE_H;
        const a = Math.floor((i / n) * len), b = Math.max(a + 1, Math.floor(((i + 1) / n) * len));
        let x0, y0, x1, y1;
        if (side === 0) { x0 = a; x1 = b; y0 = 0; y1 = BAND; }
        else if (side === 2) { x0 = a; x1 = b; y0 = SAMPLE_H - BAND; y1 = SAMPLE_H; }
        else if (side === 1) { y0 = a; y1 = b; x0 = SAMPLE_W - BAND; x1 = SAMPLE_W; }
        else { y0 = a; y1 = b; x0 = 0; x1 = BAND; }
        out.push({ side, t, x0, y0, x1, y1, cur: [0, 0, 0] });
      }
    };
    add(0, LEDS_H); add(1, LEDS_V); add(2, LEDS_H); add(3, LEDS_V);
    return out;
  }

  function sampleLeds(dt: number) {
    pctx.drawImage(screen, 0, 0, SAMPLE_W, SAMPLE_H);
    const data = pctx.getImageData(0, 0, SAMPLE_W, SAMPLE_H).data;
    const m = LOOK;
    const alpha = 1 - Math.exp(-dt / m.tau);
    lightLevel += ((lightsOn ? 1 : 0) - lightLevel) * (1 - Math.exp(-dt / 0.25));
    for (const led of leds) {
      let r = 0, g = 0, b = 0, n = 0;
      for (let y = led.y0; y < led.y1; y++) {
        for (let x = led.x0; x < led.x1; x++) {
          const o = (y * SAMPLE_W + x) * 4;
          r += data[o]!; g += data[o + 1]!; b += data[o + 2]!; n++;
        }
      }
      r /= n * 255; g /= n * 255; b /= n * 255;
      // Saturation around the luma, then gamma (Game keeps darks dark).
      const l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
      r = l + (r - l) * m.sat; g = l + (g - l) * m.sat; b = l + (b - l) * m.sat;
      r = Math.pow(clamp01(r), m.gamma) * m.gain;
      g = Math.pow(clamp01(g), m.gamma) * m.gain;
      b = Math.pow(clamp01(b), m.gamma) * m.gain;
      const c = led.cur;
      c[0] += (r * lightLevel - c[0]) * alpha;
      c[1] += (g * lightLevel - c[1]) * alpha;
      c[2] += (b * lightLevel - c[2]) * alpha;
    }
  }

  function drawGlow() {
    const W = glow.width, H = glow.height;
    gctx.globalCompositeOperation = 'source-over';
    gctx.clearRect(0, 0, W, H);
    if (lightLevel < 0.01) return;
    const host = sticky.getBoundingClientRect();
    const r = monitor.getBoundingClientRect();
    const s = GLOW_SCALE;
    const left = (r.left - host.left) * s, top = (r.top - host.top) * s;
    const w = r.width * s, h = r.height * s;
    const out = w * 0.03;
    const radius = w * 0.24;
    gctx.globalCompositeOperation = 'lighter';
    for (const led of leds) {
      let x: number, y: number;
      switch (led.side) {
        case 0: x = left + led.t * w; y = top - out; break;
        case 1: x = left + w + out; y = top + led.t * h; break;
        case 2: x = left + led.t * w; y = top + h + out; break;
        default: x = left - out; y = top + led.t * h;
      }
      const [cr, cg, cb] = led.cur;
      const R = Math.round(Math.min(1, cr) * 255), G = Math.round(Math.min(1, cg) * 255), B = Math.round(Math.min(1, cb) * 255);
      const grad = gctx.createRadialGradient(x, y, 0, x, y, radius);
      grad.addColorStop(0, `rgba(${R},${G},${B},0.42)`);
      grad.addColorStop(0.35, `rgba(${R},${G},${B},0.16)`);
      grad.addColorStop(1, `rgba(${R},${G},${B},0)`);
      gctx.fillStyle = grad;
      gctx.fillRect(x - radius, y - radius, radius * 2, radius * 2);
    }
  }

  function publishColors() {
    const avg = (side: number): [number, number, number] => {
      let r = 0, g = 0, b = 0, n = 0;
      for (const l of leds) if (l.side === side) { r += l.cur[0]; g += l.cur[1]; b += l.cur[2]; n++; }
      return [Math.min(1, r / n), Math.min(1, g / n), Math.min(1, b / n)];
    };
    const css = ([r, g, b]: [number, number, number]) => `rgb(${Math.round(r * 255)} ${Math.round(g * 255)} ${Math.round(b * 255)})`;
    // The nav and the cursor glow take the hue at full brightness, and keep their last color while the picture is
    // dark (a scene change, Backbloom switched off): a dark glow would simply vanish.
    const vivid = (c: [number, number, number]): [number, number, number] | null => {
      const m = Math.max(...c);
      return m < 0.12 ? null : [c[0] / m, c[1] / m, c[2] / m];
    };
    const l = avg(3), r = avg(1);
    root.style.setProperty('--live-b', css(avg(2)));
    root.style.setProperty('--live-l', css(l));
    root.style.setProperty('--live-r', css(r));
    const vl = vivid(l), vr = vivid(r);
    if (!vl || !vr) return;
    for (const el of liveTargets) {
      el.style.setProperty('--live-l', css(vl));
      el.style.setProperty('--live-r', css(vr));
    }
  }
}

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}
