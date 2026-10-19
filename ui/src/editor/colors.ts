/** Distinct, calm-on-dark strip colors. Strip i keeps its color for as long as its position in the list. */
export const STRIP_COLORS = ['#22d3ee', '#f5a524', '#f472b6', '#a3e635', '#a78bfa', '#fb7185', '#2dd4bf', '#fdba74'];

export function stripColor(index: number): string {
  return STRIP_COLORS[((index % STRIP_COLORS.length) + STRIP_COLORS.length) % STRIP_COLORS.length];
}
