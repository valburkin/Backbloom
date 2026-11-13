/** Maps a point given as fractions of an image to canvas pixels, for an image shown with object-fit: cover in a box
 * the size of the canvas (the image is scaled to fill it and centered, its overflow cut off). */
export function coverMap(img: HTMLImageElement, W: number, H: number): (fx: number, fy: number) => [number, number] {
  const iw = img.naturalWidth || W, ih = img.naturalHeight || H;
  const s = Math.max(W / iw, H / ih);
  const ox = (W - iw * s) / 2, oy = (H - ih * s) / 2;
  return (fx, fy) => [ox + fx * iw * s, oy + fy * ih * s];
}
