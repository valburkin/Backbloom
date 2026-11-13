import '@fontsource-variable/inter';
import '@fontsource-variable/space-grotesk';
import './style.css';
import Lenis from 'lenis';
import { gear, roadmap, statusLabel } from './data';
import { startHero } from './hero';
import { startMusic } from './music';
import { startHaptics } from './haptics';
import { startWind } from './wind';

const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
const finePointer = matchMedia('(pointer: fine)').matches;
const $ = <T extends Element = HTMLElement>(s: string, r: ParentNode = document) => r.querySelector<T>(s)!;
const $$ = <T extends Element = HTMLElement>(s: string, r: ParentNode = document) => [...r.querySelectorAll<T>(s)];
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

renderGear();
renderRoadmap();
splitHeadings();

// Smooth scrolling (off for reduced motion); anchor links go through it too, clear of the fixed nav
// (html's scroll-padding-top does the same for native scrolling).
const lenis = reduced ? null : new Lenis({ lerp: 0.1, anchors: { offset: -96, duration: 1.2 } });

// One loop drives every scroll-linked effect.
const hero = $('.hero');
const progressBar = $('.progress span');
const showcase = $('.window-stage'); // the app window and its tabs
const timeline = $('.timeline');
const chapters = $$('.tl-chapter');
const finale = $('.finale');

function onScroll() {
  const vh = innerHeight;
  const doc = document.documentElement;
  progressBar.style.transform = `scaleX(${clamp01(scrollY / (doc.scrollHeight - vh))})`;
  if (reduced) return; // scroll-driven motion stays at rest (see the stylesheet)

  // Hero: 0 at the top, 1 when its sticky stage is about to leave.
  const hr = hero.getBoundingClientRect();
  hero.style.setProperty('--p', clamp01(-hr.top / Math.max(1, hr.height - vh)).toFixed(4));

  // App window: tilted while it comes in, flat at the center of the screen.
  const sr = showcase.getBoundingClientRect();
  showcase.style.setProperty('--p', clamp01((vh - sr.top) / (vh * 0.9)).toFixed(4));

  // Roadmap: the line fills down to 60% of the screen's height; chapters above that point light up.
  const line = vh * 0.6;
  const tr = timeline.getBoundingClientRect();
  timeline.style.setProperty('--p', clamp01((line - tr.top) / tr.height).toFixed(4));
  for (const c of chapters) c.classList.toggle('reached', c.getBoundingClientRect().top < line);

  const fr = finale.getBoundingClientRect();
  finale.style.setProperty('--p', clamp01((vh - fr.top) / vh).toFixed(4));
}

if (lenis) {
  const raf = (t: number) => {
    lenis.raf(t);
    requestAnimationFrame(raf);
  };
  requestAnimationFrame(raf);
}
if (reduced) {
  showcase.style.setProperty('--p', '1');
  finale.style.setProperty('--p', '1');
  timeline.style.setProperty('--p', '1');
  chapters.forEach((c) => c.classList.add('reached'));
}
if (lenis) lenis.on('scroll', onScroll);
else addEventListener('scroll', onScroll, { passive: true });
addEventListener('resize', onScroll);
onScroll();
document.fonts?.ready.then(onScroll);

// The demo is decoration: if the canvas isn't available (some privacy add-ons block it), the page still works.
try {
  startHero(hero);
} catch (err) {
  console.warn('Hero demo unavailable:', err);
}

// Each showcase starts on its own: one failing (say, a blocked canvas) doesn't stop the others.
const showcases: [string, (el: HTMLElement) => void][] = [
  ['[data-music]', startMusic],
  ['[data-live-wind]', startWind],
  ['[data-live-haptics]', startHaptics],
];
for (const [selector, start] of showcases) {
  const el = document.querySelector<HTMLElement>(selector);
  try {
    if (el) start(el);
  } catch (err) {
    console.warn(`Showcase ${selector} unavailable:`, err);
  }
}

// Monitor tilt follows the pointer a little.
if (finePointer && !reduced) {
  const rig = $('.rig');
  hero.addEventListener('pointermove', (e) => {
    const x = e.clientX / innerWidth - 0.5, y = e.clientY / innerHeight - 0.5;
    rig.style.setProperty('--rx', `${(-y * 5).toFixed(2)}deg`);
    rig.style.setProperty('--ry', `${(x * 7).toFixed(2)}deg`);
  });
  hero.addEventListener('pointerleave', () => { rig.style.setProperty('--rx', '0deg'); rig.style.setProperty('--ry', '0deg'); });
}

// Reveals
const revealer = new IntersectionObserver(
  (entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      e.target.classList.add('in');
      revealer.unobserve(e.target);
    }
  },
  { rootMargin: '0px 0px -12% 0px' },
);
$$('[data-reveal], [data-split]').forEach((el) => revealer.observe(el));

// Before/after: the range input (invisible, over the picture) sets where Backbloom-on begins. Left of the handle is
// off, right of it on. When it first comes into view it sweeps once to show it can be dragged.
const ba = $('[data-ba]');
const baRange = $<HTMLInputElement>('.ba-range', ba);
const setBa = (v: number) => {
  ba.style.setProperty('--x', `${v}%`);
  baRange.setAttribute('aria-valuetext', `Backbloom on for the right ${Math.round(100 - v)}% of the picture`);
};
setBa(50);
let baTouched = false;
baRange.addEventListener('input', () => { baTouched = true; setBa(Number(baRange.value)); });
const baHint = new IntersectionObserver(([e]) => {
  if (!e?.isIntersecting) return;
  baHint.disconnect();
  if (reduced) return;
  const keys = [50, 22, 74, 50];
  const t0 = performance.now() + 500;
  const step = (t: number) => {
    if (baTouched) return;
    const k = clamp01((t - t0) / 2600) * (keys.length - 1);
    const i = Math.min(keys.length - 2, Math.floor(k));
    const f = k - i;
    const eased = f * f * (3 - 2 * f);
    const v = keys[i]! + (keys[i + 1]! - keys[i]!) * eased;
    setBa(v);
    baRange.value = String(Math.round(v));
    if (k < keys.length - 1) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}, { threshold: 0.6 });
baHint.observe(ba);

// Modes section switch
const modes = $('#modes');
$$<HTMLButtonElement>('[data-modes-switch] button').forEach((b) =>
  b.addEventListener('click', () => {
    setSeg(b);
    const m = b.dataset.mode!;
    modes.dataset.mode = m;
    $$('[data-panel]', modes).forEach((p) => { p.hidden = p.dataset.panel !== m; });
  }),
);

// Feature story: the step in the middle of the screen picks the picture.
const frames = $$('.story-frame');
const stepper = new IntersectionObserver(
  (entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      const i = (e.target as HTMLElement).dataset.step;
      frames.forEach((f) => f.classList.toggle('on', f.dataset.step === i));
      $$('.step').forEach((s) => s.classList.toggle('on', s === e.target));
    }
  },
  { rootMargin: '-45% 0px -45% 0px' },
);
$$('.step').forEach((s) => stepper.observe(s));

// Nav: highlight the section in view.
const navLinks = $$<HTMLAnchorElement>('.nav-links a');
const navSpy = new IntersectionObserver(
  (entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      navLinks.forEach((l) => l.classList.toggle('active', l.hash === `#${e.target.id}`));
    }
  },
  { rootMargin: '-50% 0px -50% 0px' },
);
navLinks.forEach((l) => { const t = document.querySelector(l.hash); if (t) navSpy.observe(t); });

// App window tabs: auto-advance until the visitor picks one, paused while pointer or focus is on them.
const tabs = $$<HTMLButtonElement>('[data-tab]');
const shots = $$('[data-shot]');
let autoTab = !reduced;
let tabHold = false;
const showTab = (name: string) => {
  for (const t of tabs) {
    const on = t.dataset.tab === name;
    t.classList.toggle('on', on);
    t.setAttribute('aria-selected', String(on));
    t.tabIndex = on ? 0 : -1;
  }
  for (const s of shots) {
    const on = s.dataset.shot === name;
    s.classList.toggle('on', on);
    s.toggleAttribute('aria-hidden', !on);
  }
};
tabs.forEach((t, i) => {
  t.addEventListener('click', () => { autoTab = false; showTab(t.dataset.tab!); });
  t.addEventListener('keydown', (e) => {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const next = tabs[(i + step + tabs.length) % tabs.length]!;
    autoTab = false;
    showTab(next.dataset.tab!);
    next.focus();
  });
});
showcase.addEventListener('pointerenter', () => { tabHold = true; });
showcase.addEventListener('pointerleave', () => { tabHold = false; });
showcase.addEventListener('focusin', () => { tabHold = true; });
showcase.addEventListener('focusout', () => { tabHold = false; });
let tabsShown = 0;
let showcaseVisible = false;
new IntersectionObserver(([e]) => { showcaseVisible = !!e?.isIntersecting; }, { threshold: 0.4 }).observe(showcase);
setInterval(() => {
  if (!autoTab || tabHold || !showcaseVisible) return;
  const i = tabs.findIndex((t) => t.classList.contains('on'));
  showTab(tabs[(i + 1) % tabs.length]!.dataset.tab!);
  // One round through the screens, then it stays put.
  if (++tabsShown >= tabs.length) autoTab = false;
}, 3800);

// Cursor glow and magnetic buttons
if (finePointer && !reduced) {
  const glow = $('.cursor-glow');
  let gx = innerWidth / 2, gy = innerHeight / 2, tx = gx, ty = gy;
  let following = false;
  // Eases toward the pointer and stops once it's there.
  const follow = () => {
    gx += (tx - gx) * 0.12; gy += (ty - gy) * 0.12;
    glow.style.transform = `translate3d(${gx}px, ${gy}px, 0)`;
    if (Math.abs(tx - gx) + Math.abs(ty - gy) > 0.5) requestAnimationFrame(follow);
    else following = false;
  };
  addEventListener('pointermove', (e) => {
    tx = e.clientX; ty = e.clientY;
    glow.classList.add('on');
    if (!following) { following = true; requestAnimationFrame(follow); }
  }, { passive: true });

  $$('.magnetic').forEach((el) => {
    el.addEventListener('pointermove', (e) => {
      const r = el.getBoundingClientRect();
      const x = e.clientX - r.left - r.width / 2, y = e.clientY - r.top - r.height / 2;
      el.style.transform = `translate(${x * 0.18}px, ${y * 0.3}px)`;
    });
    el.addEventListener('pointerleave', () => { el.style.transform = ''; });
  });

  // Gear cards tilt toward the pointer.
  $$('.gear-card').forEach((card) => {
    card.addEventListener('pointermove', (e) => {
      const r = card.getBoundingClientRect();
      const x = (e.clientX - r.left) / r.width, y = (e.clientY - r.top) / r.height;
      card.style.setProperty('--mx', `${x * 100}%`);
      card.style.setProperty('--my', `${y * 100}%`);
      card.style.transform = `perspective(1400px) rotateX(${(0.5 - y) * 3}deg) rotateY(${(x - 0.5) * 4}deg)`;
    });
    card.addEventListener('pointerleave', () => { card.style.transform = ''; });
  });
}

/** Moves a segmented control's highlight to the clicked button. */
function setSeg(btn: HTMLButtonElement): void {
  const seg = btn.parentElement!;
  seg.querySelectorAll('button').forEach((x) => {
    x.classList.toggle('on', x === btn);
    x.setAttribute('aria-pressed', String(x === btn));
  });
  const i = [...seg.querySelectorAll('button')].indexOf(btn);
  seg.style.setProperty('--i', String(i));
}

function splitHeadings() {
  for (const h of $$('[data-split]')) {
    const words = (h.textContent ?? '').trim().split(/\s+/);
    h.setAttribute('aria-label', words.join(' '));
    h.innerHTML = words
      .map((w, i) => `<span class="w" aria-hidden="true"><span style="--i:${i}">${esc(w)}</span></span>`)
      .join(' ');
  }
}

/** Escapes text for use in HTML built from data.ts. */
function esc(text: string): string {
  return text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
}

function renderGear() {
  // What Backbloom supports today: large panels. What a later roadmap chapter brings: slim cards below.
  $('[data-gear]').innerHTML = gear
    .filter((g) => !g.coming)
    .map((g, gi) => {
      const items = g.items.map((it) => `<li><b>${esc(it.name)}</b><small>${esc(it.note)}</small></li>`).join('');
      const status = '<p class="gear-status ok"><span class="dot"></span>Supported today</p>';
      return `<article class="gear-card" data-reveal style="--d:${gi * 90}ms">
        <div class="gear-img"><img src="${esc(g.image)}" srcset="${esc(g.image.replace('.webp', '-sm.webp'))} 800w, ${esc(g.image)} 1600w" sizes="(max-width: 900px) 100vw, 620px" alt="${esc(g.imageAlt)}" loading="lazy" /></div>
        <div class="gear-body">
          <h3>${esc(g.title)}</h3><p>${esc(g.blurb)}</p>
          <ul>${items}</ul>
          ${status}
        </div>
      </article>`;
    })
    .join('');
  $('[data-gear-soon]').innerHTML = gear
    .filter((g) => g.coming)
    .map((g, gi) => {
      const chapter = roadmap.findIndex((c) => c.title === g.coming);
      const tag = chapter >= 0 ? `Comes with chapter ${String(chapter + 1).padStart(2, '0')} &middot; ${esc(g.coming!)}` : 'Coming later';
      // A live readout under the text (wind.ts, haptics.ts): what the game does, and what the device gets.
      const live =
        g.live === 'wind'
          ? `<div class="gear-live" aria-hidden="true">
              <div><span>Speed</span><b data-speed>0</b><small>km/h</small></div>
              <div><span>Wind</span><i class="live-bar"><i data-windbar></i></i><b data-wind>0%</b></div>
            </div>`
          : g.live === 'haptics'
            ? `<div class="gear-live" aria-hidden="true">
                <div><span>Event</span><b data-event>Engine rumble</b></div>
                <div><span>Bass</span><i class="live-bar"><i data-bassbar></i></i><b data-bass>0%</b></div>
              </div>`
            : '';
      return `<a class="gear-mini"${g.live ? ` data-live-${g.live}` : ''} href="#roadmap" data-reveal style="--d:${gi * 90}ms">
        <div class="gear-mini-img"><img src="${esc(g.image)}" alt="${esc(g.imageAlt)}" loading="lazy" /></div>
        <div><span class="gear-tag">${tag}</span><h3>${esc(g.title)}</h3><p>${esc(g.blurb)}</p>
        <p class="gear-items">${g.items.map((it) => esc(it.name)).join(' &middot; ')}</p>${live}</div>
      </a>`;
    })
    .join('');
}

function renderRoadmap() {
  $('[data-roadmap]').innerHTML = roadmap
    .map((c, ci) => {
      const cards = c.items
        .map((r) => {
          return `<article class="rm-card rm-${r.status}">
            <span class="rm-status">${statusLabel[r.status]}</span>
            <h3>${esc(r.title)}</h3><p>${esc(r.text)}</p>
          </article>`;
        })
        .join('');
      const live = c.items.every((r) => r.status === 'live');
      return `<section class="tl-chapter${live ? ' tl-live' : ''}" aria-label="Chapter ${ci + 1}: ${esc(c.title)}">
        <span class="tl-node" aria-hidden="true"></span>
        <header class="tl-head" data-reveal><span class="tl-num">${String(ci + 1).padStart(2, '0')}</span><h3>${esc(c.title)}</h3><p>${esc(c.tagline)}</p></header>
        <div class="tl-cards" data-reveal style="--d: 120ms">${cards}</div>
      </section>`;
    })
    .join('');
}
