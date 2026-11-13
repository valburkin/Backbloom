/** Runs `tick` once per animation frame while `el` is on screen and the tab is visible, and schedules nothing
 * otherwise (no frames for showcases scrolled away or in a background tab). `dt` is in seconds, capped at 0.1. */
export function runWhileVisible(el: Element, tick: (dt: number) => void): void {
  let onScreen = false;
  let running = false;
  let last = 0;

  const step = (now: number) => {
    if (!onScreen || document.hidden) {
      running = false;
      return;
    }
    tick(Math.min(0.1, (now - last) / 1000));
    last = now;
    requestAnimationFrame(step);
  };
  const start = () => {
    if (running || !onScreen || document.hidden) return;
    running = true;
    last = performance.now();
    requestAnimationFrame(step);
  };

  new IntersectionObserver(([e]) => {
    onScreen = !!e?.isIntersecting;
    start();
  }).observe(el);
  document.addEventListener('visibilitychange', start);
}
