import type { ReactNode } from 'react';
import { ArrowRight } from 'lucide-react';
import { Dialog } from '../components/ui';
import { hrefFor, openHref } from '../app/router';

/** A link to the control that fixes a symptom. */
export interface FixLink {
  label: string;
  href: string;
}

export interface Symptom {
  id: string;
  title: string;
  /** One line: what to change. */
  fix: ReactNode;
  links: FixLink[];
}

/**
 * The common "my lights look wrong" problems, each with a one-line fix and links to the exact control
 * (`?focus=` opens it, also inside More options). `held`: the lights wait for "Turn on lights"; `expert`: offer the log.
 */
export function symptoms({ held, expert }: { held: boolean; expert: boolean }): Symptom[] {
  return [
    {
      id: 'nothing',
      title: 'Nothing lights up',
      fix: held
        ? 'Your lights are waiting: use Turn on lights at the top. Otherwise check that the device is on and see its status on Devices.'
        : 'Check that the device is on and plugged in. Home warns when a device can’t be reached; Devices shows the status of each one.',
      links: [{ label: 'Devices', href: hrefFor('devices') }, ...(expert ? [{ label: 'Log', href: hrefFor('settings', 'support') }] : [])],
    },
    {
      id: 'color-order',
      title: 'Red shows as green or blue',
      fix: (
        <>
          The device’s color order is wrong. In its connection settings, try “Test: blink the first LED”: if the first blink isn’t red, change Color
          order (WLED: in WLED’s own LED settings).
        </>
      ),
      links: [{ label: 'Connection settings', href: hrefFor('devices', null, 'connection') }],
    },
    {
      id: 'direction',
      title: 'The strip runs the wrong way, or the colors are shifted',
      fix: 'On the Layout page, select the strip and use Reverse direction, or Match with my lights to line its LEDs up with the picture.',
      links: [{ label: 'Layout', href: hrefFor('layout') }],
    },
    {
      id: 'hdr',
      title: 'Washed out or too pale in HDR games and videos',
      fix: 'Set HDR color correction to Auto, or to Always for HDR videos shown without Windows HDR.',
      links: [{ label: 'HDR color correction', href: hrefFor('capture', null, 'hdr') }],
    },
    {
      id: 'crop',
      title: 'The taskbar or a game’s HUD colors the lights',
      fix: 'Crop the edges of the screen by the taskbar’s or the HUD’s size.',
      links: [{ label: 'Crop edges', href: hrefFor('capture', null, 'crop') }],
    },
    {
      id: 'white',
      title: 'White looks blue or yellow',
      fix: 'Adjust the white balance until white on the LEDs matches white on your screen.',
      links: [{ label: 'White balance', href: hrefFor('colors', null, 'white-balance') }],
    },
    {
      id: 'flicker',
      title: 'Flicker or jumpy colors',
      fix: 'Pick a calmer Intensity, or make the transitions gentler.',
      links: [
        { label: 'Intensity', href: hrefFor('home', null, 'intensity') },
        { label: 'Transitions', href: hrefFor('colors', null, 'transitions') },
      ],
    },
  ];
}

/** "Lights look wrong?" on Home: symptoms with their fix, each a link to the control. */
export function TroubleshooterDialog({ open, onClose, held, expert }: { open: boolean; onClose(): void; held: boolean; expert: boolean }) {
  return (
    <Dialog open={open} title="Lights look wrong?" onClose={onClose} wide>
      <ul className="divide-y divide-line">
        {symptoms({ held, expert }).map((s) => (
          <li key={s.id} className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-start sm:gap-4">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{s.title}</p>
              <p className="mt-0.5 text-[13px] text-ink-muted">{s.fix}</p>
            </div>
            <div className="flex shrink-0 flex-wrap gap-2 sm:justify-end">
              {s.links.map((l) => (
                <a
                  key={l.href}
                  href={l.href}
                  onClick={(e) => {
                    e.preventDefault();
                    onClose();
                    openHref(l.href);
                  }}
                  className="inline-flex h-8 items-center gap-1.5 whitespace-nowrap rounded-lg border border-line-strong bg-surface-3 px-3 text-[13px] font-medium text-ink hover:bg-line-strong"
                >
                  {l.label} <ArrowRight size={14} aria-hidden />
                </a>
              ))}
            </div>
          </li>
        ))}
      </ul>
    </Dialog>
  );
}
