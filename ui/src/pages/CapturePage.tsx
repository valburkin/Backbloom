import { Monitor, RectangleHorizontal } from 'lucide-react';
import { Segmented, SliderField, StatusDot } from '../components/ui';
import { Advanced, Expert, Field, NumberField, Section, Select, Toggle } from '../components/form';
import type { CaptureHdr } from '../api/types';
import { PageFrame } from '../components/PageFrame';
import { usePageSettings, type PageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { useExpertTools } from '../app/expert';
import { differsFromDefault, propertyAt, rangeOf, withDefaults } from '../schema/schema';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'systemControl.systemInstancePriority': 'Priority',
  'systemGrabber.device': 'Monitor',
  'systemGrabber.fps': 'Frame rate',
  'systemGrabber.hdrToneMapping': 'HDR color correction',
  'systemGrabber.monitor_nits': 'SDR brightness in HDR mode',
  'systemGrabber.videoMode': 'Picture detail',
  'systemGrabber.cropLeft': 'Crop left',
  'systemGrabber.cropRight': 'Crop right',
  'systemGrabber.cropTop': 'Crop top',
  'systemGrabber.cropBottom': 'Crop bottom',
  'blackborderdetector.threshold': 'How dark a bar must be',
};

/** "\\.\DISPLAY5|NVIDIA GeForce RTX 5080" → "Display 5 · NVIDIA GeForce RTX 5080". */
export function monitorLabel(mode: string): string {
  const [dev, adapter] = mode.split('|');
  const n = /DISPLAY(\d+)/i.exec(dev ?? '')?.[1];
  return [n ? `Display ${n}` : dev, adapter].filter(Boolean).join(' · ');
}

type ToneMappingMode = CaptureHdr['mode'];

const TONE_MAPPING: ReadonlyArray<{ value: ToneMappingMode; label: string; hint: string }> = [
  { value: 'auto', label: 'Auto', hint: 'Corrects colors only while Windows HDR is on for this monitor. Recommended.' },
  { value: 'on', label: 'Always', hint: 'Also corrects in SDR mode: only for HDR videos or games shown without Windows HDR. Normal colors look oversaturated.' },
  { value: 'off', label: 'Never', hint: 'Never corrects HDR colors.' },
];

/** One line on what screen sync does with HDR right now (null without screen capture). */
export function toneMappingStatus(hdr: CaptureHdr | undefined): { text: string; tone: 'ok' | 'neutral' | 'warn' } | null {
  if (!hdr) return null;
  if (!hdr.capturing) return { text: 'Waiting for screen sync to start.', tone: 'neutral' };
  if (hdr.monitorHdr) return hdr.toneMapping ? { text: 'HDR is on — colors are corrected.', tone: 'ok' } : { text: 'HDR is on — colors are not corrected.', tone: 'warn' };
  return hdr.toneMapping
    ? { text: 'HDR is off — colors are corrected anyway (Always).', tone: 'warn' }
    : { text: 'HDR is off — colors are used as they are.', tone: 'ok' };
}

const BORDER_MODES: Record<string, { label: string; hint: string }> = {
  default: { label: 'Standard', hint: 'Finds bars on all sides. Works for most movies and games.' },
  classic: { label: 'Simple', hint: 'An older, simpler way of finding bars.' },
  osd: { label: 'Ignore on-screen menus', hint: 'Finds bars even while a player shows menus or subtitles at the edges.' },
  letterbox: { label: 'Top and bottom only', hint: 'Only bars above and below the picture, as in cinema films.' },
};

const CROP_KEYS = ['cropLeft', 'cropRight', 'cropTop', 'cropBottom'] as const;

/** Keys of `section` that differ from the schema defaults, and a way to put them back. */
function tierState(ps: PageSettings, section: string, keys: readonly string[]) {
  const schema = ps.schemaOf(section);
  const value = ps.value<Obj>(section);
  const changed = keys.filter((k) => schema?.properties?.[k] && differsFromDefault(schema.properties[k], value[k]));
  return {
    changed,
    invalid: ps.issues.some((i) => keys.some((k) => i.path === `${section}.${k}`)),
    reset: () => ps.set(section, { ...value, ...Object.fromEntries(changed.map((k) => [k, withDefaults(schema?.properties?.[k], undefined)])) }),
  };
}

/** The frame rate the screen is read at, at least, while the Look's transitions are quicker than 0.3 s (the backend decides). */
const FAST_LOOK_FPS = 60;

export function CapturePage() {
  const ps = usePageSettings('capture', LABELS, true);
  const { serverInfo } = useConnection();
  const expert = useExpertTools();
  const control = ps.value<Obj>('systemControl');
  const grabber = ps.value<Obj>('systemGrabber');
  const border = ps.value<Obj>('blackborderdetector');
  const err = (path: string) => ps.issues.find((i) => i.path === path)?.message;
  const range = (section: string, key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(ps.schemaOf(section), key), fb);
  const set = (section: string, key: string) => (v: unknown) => ps.setPath(section, [key], v);
  const shownExpert = (section: string, keys: readonly string[]) => expert || tierState(ps, section, keys).changed.length > 0 || tierState(ps, section, keys).invalid;
  const modes = serverInfo?.systemGrabbers?.modes ?? [];
  const hdr = serverInfo?.systemGrabbers?.hdr;
  const toneMapping = (TONE_MAPPING.find((m) => m.value === grabber.hdrToneMapping) ?? TONE_MAPPING[0]).value;
  const hdrStatus = toneMappingStatus(hdr);
  const fps = range('systemGrabber', 'fps', { min: 5, max: 120 });
  const nits = range('systemGrabber', 'monitor_nits', { min: 0, max: 10000 });
  const width = range('systemGrabber', 'videoMode', { min: 196, max: 1280 });
  const threshold = range('blackborderdetector', 'threshold', { min: 0, max: 100 });
  const priority = range('systemControl', 'systemInstancePriority', { min: 100, max: 253 });
  const hardware = grabber.hardware === true;
  const autoNits = grabber.monitor_nits_auto !== false;

  const screenMore = tierState(ps, 'systemGrabber', ['fps', 'hardware', 'hdrToneMapping', 'monitor_nits_auto', 'monitor_nits', ...CROP_KEYS, 'videoMode']);
  const screenExpert = ['reorder_displays'] as const;
  const borderMore = tierState(ps, 'blackborderdetector', ['mode', 'threshold']);
  const borderExpert = ['borderFrameCnt', 'unknownFrameCnt', 'maxInconsistentCnt', 'blurRemoveCnt'] as const;

  return (
    <PageFrame title="Screen" subtitle="How Backbloom reads your screen." settings={ps}>
      <Section title="Screen" icon={<Monitor size={18} />}>
        <Select
          className="max-w-md"
          label="Monitor"
          hint={modes.length ? 'Which monitor your lights follow.' : 'No monitors were found.'}
          value={String(grabber.device ?? 'auto')}
          options={[{ value: 'auto', label: 'Automatic (main monitor)' }, ...modes.map((m) => ({ value: m, label: monitorLabel(m) }))]}
          onChange={set('systemGrabber', 'device')}
        />
        {hdrStatus && (
          <p className="flex items-center gap-2 text-[13px]" role="status">
            <StatusDot tone={hdrStatus.tone} />
            {hdrStatus.text}
          </p>
        )}
        <Advanced changed={screenMore.changed.length} invalid={screenMore.invalid} onReset={screenMore.reset}>
          <SliderField
            label="Frame rate"
            unit="fps"
            min={fps.min}
            max={fps.max}
            step={1}
            value={Number(grabber.fps ?? fps.default ?? 30)}
            onChange={set('systemGrabber', 'fps')}
            hint={`How often the screen is read. 30–60 looks smooth; higher uses more of your PC. While the Look is quick (Vivid, Intense), at least ${FAST_LOOK_FPS}.`}
          />
          <Toggle label="Use the graphics card" hint="Faster, and needed for HDR brightness matching. Recommended." checked={hardware} onChange={set('systemGrabber', 'hardware')} />
          <div id="hdr">
            <Field label="HDR color correction" hint={TONE_MAPPING.find((m) => m.value === toneMapping)?.hint}>
              {() => <Segmented label="HDR color correction" value={toneMapping} options={TONE_MAPPING} onChange={set('systemGrabber', 'hdrToneMapping')} />}
            </Field>
          </div>
          {hardware ? (
            <div className="space-y-4">
              <Toggle
                label="Match Windows’ HDR brightness"
                hint={
                  <>
                    Uses the “SDR content brightness” Windows has for this monitor (Settings → Display → HDR).
                    {autoNits && hdr?.sdrWhiteNits ? ` Now ${hdr.sdrWhiteNits} nits.` : ''}
                  </>
                }
                checked={autoNits}
                onChange={set('systemGrabber', 'monitor_nits_auto')}
              />
              {!autoNits && (
                <SliderField
                  label="SDR brightness in HDR mode"
                  unit="nits"
                  min={nits.min}
                  max={Math.min(nits.max, 1000)}
                  step={10}
                  value={Number(grabber.monitor_nits ?? nits.default ?? 200)}
                  onChange={set('systemGrabber', 'monitor_nits')}
                  hint="Set it to match Windows’ “SDR content brightness”. 0 turns the HDR correction by the graphics card off."
                />
              )}
            </div>
          ) : (
            <p className="text-xs text-ink-faint">Matching the HDR brightness needs “Use the graphics card”.</p>
          )}
          <div id="crop">
            <Field label="Crop edges" hint="Ignore pixels at the edges of the screen, for example a taskbar or a game’s HUD.">
              {() => (
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                  {CROP_KEYS.map((k) => {
                    const r = range('systemGrabber', k, { min: 0, max: 4000 });
                    return (
                      <NumberField
                        key={k}
                        integer
                        label={LABELS[`systemGrabber.${k}`].replace('Crop ', '').replace(/^./, (c) => c.toUpperCase())}
                        unit="px"
                        min={r.min}
                        step={r.step}
                        value={Number(grabber[k] ?? 0)}
                        error={err(`systemGrabber.${k}`)}
                        onChange={set('systemGrabber', k)}
                      />
                    );
                  })}
                </div>
              )}
            </Field>
          </div>
          <SliderField
            label="Picture detail"
            unit="px"
            min={width.min}
            max={width.max}
            step={4}
            value={Number(grabber.videoMode ?? 512)}
            onChange={set('systemGrabber', 'videoMode')}
            hint="Backbloom shrinks the picture to this width before reading colors. Higher is slower and rarely looks different."
          />
          {hardware && shownExpert('systemGrabber', screenExpert) && (
            <NumberField
              integer
              className="max-w-xs"
              label="Display order"
              hint="Change only if the wrong monitor is read with several monitors connected."
              min={0}
              max={255}
              value={Number(grabber.reorder_displays ?? 0)}
              onChange={set('systemGrabber', 'reorder_displays')}
            />
          )}
          <Expert always={tierState(ps, 'systemControl', ['systemInstancePriority']).changed.length > 0 || !!err('systemControl.systemInstancePriority')}>
            <NumberField
              integer
              className="max-w-xs"
              label="Priority"
              hint="Lower numbers win over higher ones. Effects and colors you start use priority 1."
              min={priority.min}
              max={priority.max}
              value={Number(control.systemInstancePriority ?? 245)}
              error={err('systemControl.systemInstancePriority')}
              onChange={set('systemControl', 'systemInstancePriority')}
            />
          </Expert>
        </Advanced>
      </Section>

      <Section title="Black bars" icon={<RectangleHorizontal size={18} />}>
        <Toggle
          label="Ignore black bars"
          hint="Ignores black bars around movies so your lights show the picture, not the bars."
          checked={border.enable === true}
          onChange={set('blackborderdetector', 'enable')}
        />
        {border.enable === true && (
          <Advanced changed={borderMore.changed.length} invalid={borderMore.invalid} onReset={borderMore.reset}>
            <Select
              className="max-w-xs"
              label="Detection"
              value={String(border.mode ?? 'default')}
              hint={BORDER_MODES[String(border.mode)]?.hint}
              options={Object.entries(BORDER_MODES).map(([value, m]) => ({ value, label: m.label }))}
              onChange={set('blackborderdetector', 'mode')}
            />
            <SliderField
              label="How dark a bar must be"
              unit="%"
              min={threshold.min}
              max={Math.min(threshold.max, 50)}
              step={1}
              value={Number(border.threshold ?? 5)}
              onChange={set('blackborderdetector', 'threshold')}
              hint="Raise it if dark gray bars aren’t ignored."
            />
            {shownExpert('blackborderdetector', borderExpert) && (
              <div className="grid gap-4 sm:grid-cols-2">
                <NumberField integer label="Frames to confirm a bar" min={0} value={Number(border.borderFrameCnt)} onChange={set('blackborderdetector', 'borderFrameCnt')} />
                <NumberField integer label="Frames before resetting" min={0} step={50} value={Number(border.unknownFrameCnt)} onChange={set('blackborderdetector', 'unknownFrameCnt')} />
                <NumberField integer label="Inconsistent frames tolerated" min={0} value={Number(border.maxInconsistentCnt)} onChange={set('blackborderdetector', 'maxInconsistentCnt')} />
                <NumberField integer label="Blurry edge to ignore" unit="px" min={0} value={Number(border.blurRemoveCnt)} onChange={set('blackborderdetector', 'blurRemoveCnt')} />
              </div>
            )}
          </Advanced>
        )}
      </Section>
    </PageFrame>
  );
}
