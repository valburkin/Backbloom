// The smoothing spring: the Natural styles (HybridRgbInterpolator, HybridInterpolator) take their speed from
// `stiffness` and `damping`, not from `time_ms`. The UI offers one transition time and sets the matching spring.

type Obj = Record<string, unknown>;

/** The smoothing styles whose speed comes from the spring. */
export const SPRING_TYPES: readonly string[] = ['HybridRgbInterpolator', 'HybridInterpolator'];

/** The spring tested on real LEDs (stiffness 150, damping 26) takes about this long to 90% of a change. */
const REFERENCE_MS = 350;
const REFERENCE_STIFFNESS = 150;
/** damping / (2 * sqrt(stiffness)) of the reference spring: slightly over-damped, no overshoot. */
const DAMPING_RATIO = 26 / (2 * Math.sqrt(REFERENCE_STIFFNESS));

/**
 * The shortest transition the spring is set up for. A faster one would need a stiffness above the setting's maximum
 * (1000); slower settings than this stay calm, faster ones keep this spring.
 */
export const MIN_SPRING_MS = 150;

/**
 * The spring that reaches 90% of a change in about `ms` milliseconds, at any update frequency (the backend splits long
 * ticks into steps the spring allows). Exact enough up to about 1 s; rounding to whole numbers makes much longer
 * times faster than asked.
 */
export function springFor(ms: number): { stiffness: number; damping: number } {
  const omega = (Math.sqrt(REFERENCE_STIFFNESS) * REFERENCE_MS) / Math.max(ms, MIN_SPRING_MS);
  return { stiffness: Math.round(omega * omega), damping: Math.round(2 * DAMPING_RATIO * omega) };
}

/**
 * The `smoothing` section with a new transition time and its spring. The same object when the time doesn't change, so
 * re-confirming the shown value keeps a spring set by hand.
 */
export function withTransition(smoothing: Obj, ms: number): Obj {
  return smoothing.time_ms === ms ? smoothing : { ...smoothing, time_ms: ms, ...springFor(ms) };
}
