import { useEffect, useRef, useState, type ReactNode } from 'react';
import { PenLine } from 'lucide-react';
import { shapePath, shapes, toUnfolded, type Room, type ShapeId } from '../../layout';
import { cx } from '../../components/ui';
import { frontFocus, screenRect, surfaceRect, wholeRoom } from './canvasMath';

/** A tiny drawing of a shape in the room (front wall and ceiling). */
function ShapeIcon({ room, shape }: { room: Room; shape: ShapeId }) {
  const path = shapePath(room, shape);
  const focus = shape.startsWith('ceiling') ? wholeRoom(room) : frontFocus(room);
  const front = surfaceRect(room, 'front');
  const ceiling = surfaceRect(room, 'ceiling');
  const scr = screenRect(room);
  const pts = path.map((p) => toUnfolded(room, p));
  const box = shape.startsWith('ceiling') ? { x: ceiling.x - 0.2, y: ceiling.y - 0.2, w: ceiling.w + 0.4, h: ceiling.h + front.h + 0.4 } : focus;
  const sw = box.w / 40;
  return (
    <svg viewBox={`${box.x} ${box.y} ${box.w} ${box.h}`} className="h-12 w-16 shrink-0 rounded-md bg-bg" aria-hidden>
      <rect x={front.x} y={front.y} width={front.w} height={front.h} fill="#161b27" />
      <rect x={ceiling.x} y={ceiling.y} width={ceiling.w} height={ceiling.h} fill="#11141d" />
      <rect x={scr.x} y={scr.y} width={scr.w} height={scr.h} fill="#0b0e15" stroke="#4a5470" strokeWidth={sw / 2} />
      <polyline points={pts.map((p) => `${p.x},${p.y}`).join(' ')} fill="none" stroke="#22d3ee" strokeWidth={sw} strokeLinejoin="round" />
      {pts[0] && <circle cx={pts[0].x} cy={pts[0].y} r={sw * 1.6} fill="#22d3ee" />}
    </svg>
  );
}

/** A button that opens a list of ready-made strip shapes, plus "draw it yourself". */
export function ShapeMenu({
  room,
  label,
  icon,
  disabled,
  onShape,
  onDraw,
  align = 'right',
  variant = 'ghost',
}: {
  room: Room;
  label: string;
  icon?: ReactNode;
  disabled?: boolean;
  onShape(shape: ShapeId): void;
  onDraw(): void;
  align?: 'left' | 'right';
  variant?: 'ghost' | 'secondary';
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const pick = (fn: () => void) => () => {
    setOpen(false);
    fn();
  };

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        disabled={disabled}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className={cx(
          'inline-flex h-8 items-center justify-center gap-2 rounded-lg px-3 text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40',
          variant === 'ghost' ? 'text-ink-muted hover:bg-surface-3 hover:text-ink' : 'border border-line-strong bg-surface-3 text-ink hover:bg-line-strong',
        )}
      >
        {icon}
        {label}
      </button>
      {open && (
        <div
          role="menu"
          className={cx('absolute top-9 z-30 w-72 rounded-xl border border-line-strong bg-surface p-1.5 shadow-2xl', align === 'right' ? 'right-0' : 'left-0')}
        >
          {shapes(room).map((s) => (
            <button
              key={s.id}
              type="button"
              role="menuitem"
              onClick={pick(() => onShape(s.id))}
              className="flex w-full items-center gap-3 rounded-lg p-2 text-left hover:bg-surface-3"
            >
              <ShapeIcon room={room} shape={s.id} />
              <span className="min-w-0">
                <span className="block text-[13px] font-medium text-ink">{s.name}</span>
                <span className="block text-xs text-ink-faint">{s.description}</span>
              </span>
            </button>
          ))}
          <button type="button" role="menuitem" onClick={pick(onDraw)} className="flex w-full items-center gap-3 rounded-lg p-2 text-left hover:bg-surface-3">
            <span className="flex h-12 w-16 shrink-0 items-center justify-center rounded-md bg-bg text-accent-strong">
              <PenLine size={20} />
            </span>
            <span className="min-w-0">
              <span className="block text-[13px] font-medium text-ink">Draw it myself</span>
              <span className="block text-xs text-ink-faint">Click where the strip starts and at every bend.</span>
            </span>
          </button>
        </div>
      )}
    </div>
  );
}
