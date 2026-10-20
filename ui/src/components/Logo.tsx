import { useId } from 'react';

/** Backbloom mark: a screen whose glow spills past its edges. Mirrors branding/backbloom-mark.svg. */
export function Logo({ size = 28 }: { size?: number }) {
  const id = useId();
  return (
    <svg width={size} height={size} viewBox="0 0 512 512" aria-hidden>
      <defs>
        <linearGradient id={`${id}g`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#FF3CAC" />
          <stop offset="0.5" stopColor="#8B5CF6" />
          <stop offset="1" stopColor="#22D3EE" />
        </linearGradient>
        <filter id={`${id}f`} x="-50%" y="-50%" width="200%" height="200%">
          <feGaussianBlur stdDeviation="26" />
        </filter>
      </defs>
      <rect width="512" height="512" rx="112" fill="#0D0E1A" />
      <rect x="116" y="156" width="280" height="200" rx="30" fill="none" stroke={`url(#${id}g)`} strokeWidth="80" filter={`url(#${id}f)`} />
      <rect x="116" y="156" width="280" height="200" rx="30" fill="#0D0E1A" stroke={`url(#${id}g)`} strokeWidth="20" />
    </svg>
  );
}
