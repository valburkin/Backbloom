import { encode } from 'uqr';

/** The QR code of `text` as one SVG path (one square per dark module) and its size in modules, border included. */
export function qrPath(text: string, border = 2): { size: number; path: string } {
  const { data, size } = encode(text, { ecc: 'M', border });
  let path = '';
  data.forEach((row, y) =>
    row.forEach((dark, x) => {
      if (dark) path += `M${x} ${y}h1v1h-1z`;
    }),
  );
  return { size, path };
}

/** A scannable QR code (always dark on white, whatever the theme). */
export function QrCode({ text, label, pixels = 168 }: { text: string; label: string; pixels?: number }) {
  const { size, path } = qrPath(text);
  return (
    <svg
      role="img"
      aria-label={label}
      viewBox={`0 0 ${size} ${size}`}
      width={pixels}
      height={pixels}
      shapeRendering="crispEdges"
      className="shrink-0 rounded-lg"
    >
      <rect width={size} height={size} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}
