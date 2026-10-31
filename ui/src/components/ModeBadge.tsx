import { Gamepad2, Sofa } from 'lucide-react';
import { useConnection } from '../app/connection';
import { Badge } from './ui';

/** The mode next to the title of a page that differs between Ambient and Game (Home, Colors). */
export function ModeBadge() {
  const mode = useConnection().serverInfo?.mode?.mode;
  if (!mode) return null;
  return (
    <Badge tone={mode === 'game' ? 'accent' : 'neutral'} className="gap-1 text-xs font-medium tracking-normal">
      {mode === 'game' ? <Gamepad2 size={13} aria-hidden /> : <Sofa size={13} aria-hidden />}
      {mode === 'game' ? 'Game mode' : 'Ambient mode'}
    </Badge>
  );
}
