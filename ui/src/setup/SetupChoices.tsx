import { Cable, Cpu, Lightbulb, RotateCcw } from 'lucide-react';
import { OptionButton } from './parts';
import { boardSetupInProgress } from './SetUpBoard';

/** Where Add device starts: the choice, or one of its flows (`wled-resume`: the board setup left this session). */
export type AddStart = 'choose' | 'wled-new' | 'wled-resume' | 'lights' | 'existing';

/**
 * The three ways to add lights: LED strips on a WLED board, room lights, or another kind of device; first, while a
 * board's setup was left in the middle this session, a way to go on with it.
 */
export function SetupChoices({ onPick }: { onPick(kind: Exclude<AddStart, 'choose'>): void }) {
  return (
    <div className="space-y-2">
      {boardSetupInProgress() && (
        <OptionButton icon={<RotateCcw size={18} />} title="Continue setting up your board" onClick={() => onPick('wled-resume')}>
          Go on where you left off.
        </OptionButton>
      )}
      <OptionButton icon={<Cable size={18} />} title="LED strips on a WLED board" onClick={() => onPick('wled-new')}>
        Backbloom finds the board (new, or on your Wi-Fi already), puts it on your Wi-Fi and finds each strip plugged into it.
      </OptionButton>
      <OptionButton icon={<Lightbulb size={18} />} title="Room lights" onClick={() => onPick('lights')}>
        Hue, Govee, Nanoleaf, LIFX, WiZ, Yeelight or Home Assistant lights on your Wi-Fi.
      </OptionButton>
      <OptionButton icon={<Cpu size={18} />} title="Another kind of device" onClick={() => onPick('existing')}>
        DDP, Art-Net, E1.31, a USB device such as Adalight, or WLED by its address.
      </OptionButton>
    </div>
  );
}
