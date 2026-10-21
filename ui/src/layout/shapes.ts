import { surfaceSize } from './room';
import type { PathPoint, Room } from './types';

// Ready-made strip shapes: paths for the usual places a strip goes, fitted to the user's room.

/** The furniture drawn under the screen (front-wall coordinates, meters): a desk top or a TV stand. */
export function furnitureTop(room: Room): { x: number; y: number; width: number } | null {
  if (room.furniture === 'none') return null;
  const desk = room.furniture === 'desk';
  const width = Math.min(room.width, desk ? 1.4 : Math.max(1.8, room.screen.width + 0.4));
  const height = desk ? 0.75 : 0.5;
  const cx = room.screen.x + room.screen.width / 2;
  const x = Math.min(room.width - width, Math.max(0, cx - width / 2));
  return { x, y: room.height - height, width };
}

export type ScreenCorner = 'bottom-right' | 'bottom-left' | 'top-left' | 'top-right' | 'bottom-middle';

export interface AroundScreenOptions {
  start: ScreenCorner;
  /** Direction as seen from the seat. */
  clockwise: boolean;
}

/** Around the screen's border (on the back of a monitor or TV), from `start` all the way round. */
export function aroundScreen(room: Room, { start, clockwise }: AroundScreenOptions): PathPoint[] {
  const { x, y, width: w, height: h } = room.screen;
  // Clockwise from the top-left corner, as seen from the seat.
  const ring: PathPoint[] = [
    { surface: 'front', x, y },
    { surface: 'front', x: x + w, y },
    { surface: 'front', x: x + w, y: y + h },
    { surface: 'front', x, y: y + h },
  ];
  const middle: PathPoint = { surface: 'front', x: x + w / 2, y: y + h };
  let points: PathPoint[];
  switch (start) {
    case 'top-left':
      points = [ring[0], ring[1], ring[2], ring[3]];
      break;
    case 'top-right':
      points = [ring[1], ring[2], ring[3], ring[0]];
      break;
    case 'bottom-right':
      points = [ring[2], ring[3], ring[0], ring[1]];
      break;
    case 'bottom-left':
      points = [ring[3], ring[0], ring[1], ring[2]];
      break;
    case 'bottom-middle':
      points = [middle, ring[3], ring[0], ring[1], ring[2]];
      break;
  }
  if (!clockwise) points = [points[0], ...points.slice(1).reverse()];
  return [...points, { ...points[0] }].map((p) => ({ ...p }));
}

/** Along the wall just above the desk or TV stand (right to left as seen from the seat), or under the screen without furniture. */
export function behindFurniture(room: Room, rightToLeft = true): PathPoint[] {
  const top = furnitureTop(room);
  const y = top ? top.y - 0.03 : room.screen.y + room.screen.height + 0.1;
  const x0 = top ? top.x + 0.03 : room.screen.x;
  const x1 = top ? top.x + top.width - 0.03 : room.screen.x + room.screen.width;
  const left: PathPoint = { surface: 'front', x: x0, y: Math.min(room.height, y) };
  const right: PathPoint = { surface: 'front', x: x1, y: Math.min(room.height, y) };
  return rightToLeft ? [right, left] : [left, right];
}

/** Across the ceiling close to the front wall (right to left as seen from the seat). */
export function ceilingFront(room: Room, rightToLeft = true): PathPoint[] {
  const { w, h } = surfaceSize(room, 'ceiling');
  const inset = Math.min(0.15, w / 4);
  const y = Math.max(0, h - 0.15);
  const left: PathPoint = { surface: 'ceiling', x: inset, y };
  const right: PathPoint = { surface: 'ceiling', x: w - inset, y };
  return rightToLeft ? [right, left] : [left, right];
}

/** Around the edge of the ceiling (a cove), from the front-right corner, right to left along the front wall first. */
export function ceilingAround(room: Room): PathPoint[] {
  const { w, h } = surfaceSize(room, 'ceiling');
  const inset = Math.min(0.15, w / 4, h / 4);
  const frontRight: PathPoint = { surface: 'ceiling', x: w - inset, y: h - inset };
  return [
    frontRight,
    { surface: 'ceiling', x: inset, y: h - inset },
    { surface: 'ceiling', x: inset, y: inset },
    { surface: 'ceiling', x: w - inset, y: inset },
    { ...frontRight },
  ];
}

/** A short straight line on the front wall above the screen, for strips the user draws themselves. */
export function straightLine(room: Room): PathPoint[] {
  const y = Math.max(0.1, room.screen.y - 0.25);
  return [
    { surface: 'front', x: room.screen.x + room.screen.width, y },
    { surface: 'front', x: room.screen.x, y },
  ];
}

export type ShapeId = 'around-screen' | 'behind-furniture' | 'ceiling-front' | 'ceiling-around' | 'line';

export interface ShapeInfo {
  id: ShapeId;
  name: string;
  description: string;
}

export function shapes(room: Room): ShapeInfo[] {
  const furniture = room.furniture === 'tv' ? 'TV stand' : 'desk';
  const screen = room.furniture === 'tv' ? 'TV' : 'monitor';
  return [
    { id: 'around-screen', name: `Around the ${screen}`, description: `On the back of the ${screen}, along its edges.` },
    { id: 'behind-furniture', name: `Behind the ${furniture}`, description: `Along the wall just above the ${furniture}.` },
    { id: 'ceiling-front', name: 'Ceiling, front edge', description: 'Across the ceiling, along the front wall.' },
    { id: 'ceiling-around', name: 'Around the ceiling', description: 'Along all four edges of the ceiling.' },
    { id: 'line', name: 'Straight line', description: 'A line to drag where you need it.' },
  ];
}

export function shapePath(room: Room, id: ShapeId): PathPoint[] {
  switch (id) {
    case 'around-screen':
      return aroundScreen(room, { start: 'bottom-right', clockwise: true });
    case 'behind-furniture':
      return behindFurniture(room);
    case 'ceiling-front':
      return ceilingFront(room);
    case 'ceiling-around':
      return ceilingAround(room);
    case 'line':
      return straightLine(room);
  }
}
