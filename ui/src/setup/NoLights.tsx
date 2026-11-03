import { Plus, Sparkles } from 'lucide-react';
import { Button, Card } from '../components/ui';
import type { Controller } from '../layout';
import { isTestDevice } from '../pages/devices/drivers';

/** The layout has a device that shows the colors somewhere (not just the test device of a new install). */
export function hasRealDevice(controllers: readonly Controller[]): boolean {
  return controllers.some((c) => !isTestDevice(c));
}

/** The devices to list: without expert tools, the test device (which writes the colors nowhere) stays hidden. */
export function shownDevices(controllers: readonly Controller[], expert: boolean): Controller[] {
  return expert ? [...controllers] : controllers.filter((c) => !isTestDevice(c));
}

/** Home and Devices before any real device: one way on, Add your lights. */
export function NoLights({ onAdd }: { onAdd(): void }) {
  return (
    <Card>
      <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
        <Sparkles size={28} className="text-ink-faint" aria-hidden />
        <div>
          <p className="text-base font-semibold">No lights yet</p>
          <p className="mt-1 max-w-md text-sm text-ink-muted">
            Add the LED strips and lights Backbloom colors: strips on a WLED board, room lights such as Hue or Govee, or another device.
          </p>
        </div>
        <Button variant="primary" icon={<Plus size={16} />} onClick={onAdd}>
          Add your lights
        </Button>
      </div>
    </Card>
  );
}
