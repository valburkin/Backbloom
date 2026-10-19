import { Crop, Monitor, RectangleHorizontal, ScreenShare } from 'lucide-react';
import { SliderField } from '../components/ui';
import { Advanced, Callout, NumberField, Section, Select, Toggle } from '../components/form';
import { PageFrame } from '../components/PageFrame';
import { usePageSettings } from '../app/config';
import { useConnection } from '../app/connection';
import { propertyAt, rangeOf } from '../schema/schema';

type Obj = Record<string, unknown>;

const LABELS: Record<string, string> = {
  'systemControl.systemInstanceEnable': 'Screen capture',
  'systemControl.systemInstancePriority': 'Priority',
  'systemGrabber.device': 'Monitor',
  'systemGrabber.fps': 'Frame rate',
  'systemGrabber.monitor_nits': 'SDR brightness in HDR mode',
  'systemGrabber.videoMode': 'Capture width',
  'systemGrabber.cropLeft': 'Crop left',
  'systemGrabber.cropRight': 'Crop right',
  'systemGrabber.cropTop': 'Crop top',
  'systemGrabber.cropBottom': 'Crop bottom',
  'blackborderdetector.threshold': 'Black bar threshold',
};

/** "\\.\DISPLAY5|NVIDIA GeForce RTX 5080" → "Display 5 · NVIDIA GeForce RTX 5080". */
export function monitorLabel(mode: string): string {
  const [dev, adapter] = mode.split('|');
  const n = /DISPLAY(\d+)/i.exec(dev ?? '')?.[1];
  return [n ? `Display ${n}` : dev, adapter].filter(Boolean).join(' · ');
}

const BORDER_MODES: Record<string, { label: string; hint: string }> = {
  default: { label: 'Standard', hint: 'Detects bars on all sides. Works for most movies and games.' },
  classic: { label: 'Classic', hint: 'Older, simpler detection.' },
  osd: { label: 'With on-screen menus', hint: 'Ignores overlays and menus at the edges while detecting bars.' },
  letterbox: { label: 'Letterbox only', hint: 'Only top and bottom bars (cinema films).' },
};

export function CapturePage() {
  const ps = usePageSettings('capture', LABELS);
  const { serverInfo } = useConnection();
  const control = ps.value<Obj>('systemControl');
  const grabber = ps.value<Obj>('systemGrabber');
  const border = ps.value<Obj>('blackborderdetector');
  const err = (path: string) => ps.issues.find((i) => i.path === path)?.message;
  const range = (section: string, key: string, fb: { min: number; max: number; step?: number }) => rangeOf(propertyAt(ps.schemaOf(section), key), fb);
  const set = (section: string, key: string) => (v: unknown) => ps.setPath(section, [key], v);
  const modes = serverInfo?.systemGrabbers?.modes ?? [];
  const backendName = serverInfo?.systemGrabbers?.device;
  const enabled = control.systemInstanceEnable === true;
  const fps = range('systemGrabber', 'fps', { min: 5, max: 120 });
  const nits = range('systemGrabber', 'monitor_nits', { min: 0, max: 10000 });
  const width = range('systemGrabber', 'videoMode', { min: 196, max: 1280 });
  const threshold = range('blackborderdetector', 'threshold', { min: 0, max: 100 });

  return (
    <PageFrame title="Screen capture" subtitle="How Overglow watches your screen." settings={ps}>
      <Section
        title="Capture"
        icon={<ScreenShare size={18} />}
        description={backendName ? `Captures with ${backendName === 'DirectX11' ? 'DirectX 11 (Windows desktop duplication)' : backendName}.` : undefined}
      >
        <Toggle
          label="Screen capture"
          hint="Mirror what’s on your screen onto the LEDs. Turn it off if you only use effects or colors."
          checked={enabled}
          onChange={set('systemControl', 'systemInstanceEnable')}
        />
        <Select
          label="Monitor"
          hint={modes.length ? 'Which display to mirror.' : 'No displays were reported by the service.'}
          value={String(grabber.device ?? 'auto')}
          options={[{ value: 'auto', label: 'Automatic (main display)' }, ...modes.map((m) => ({ value: m, label: monitorLabel(m) }))]}
          onChange={set('systemGrabber', 'device')}
        />
        <SliderField
          label="Frame rate"
          unit="fps"
          min={fps.min}
          max={fps.max}
          step={1}
          value={Number(grabber.fps ?? fps.default ?? 30)}
          onChange={set('systemGrabber', 'fps')}
          hint="How often the screen is sampled. 30–60 is smooth; higher uses more CPU and GPU."
        />
        <Toggle
          label="Hardware acceleration"
          hint="Let the graphics card scale and convert the picture. Recommended."
          checked={grabber.hardware === true}
          onChange={set('systemGrabber', 'hardware')}
        />
      </Section>

      <Section title="HDR" icon={<Monitor size={18} />} description="For games and videos in HDR, so the LEDs don’t look washed out.">
        <Toggle
          label="HDR tone mapping"
          hint="Convert HDR colors to normal brightness before sending them to the LEDs. Turn on if Windows HDR is on."
          checked={grabber.hdrToneMapping === true}
          onChange={set('systemGrabber', 'hdrToneMapping')}
        />
        {grabber.hardware === true ? (
          <SliderField
            label="SDR brightness in HDR mode"
            unit="nits"
            min={nits.min}
            max={Math.min(nits.max, 1000)}
            step={10}
            value={Number(grabber.monitor_nits ?? nits.default ?? 200)}
            onChange={set('systemGrabber', 'monitor_nits')}
            hint="Match Windows’ “SDR content brightness” (Settings → Display → HDR). 0 turns off the hardware conversion."
          />
        ) : (
          <p className="text-xs text-ink-faint">The brightness correction needs hardware acceleration.</p>
        )}
      </Section>

      <Section title="Crop" icon={<Crop size={18} />} description="Ignore pixels at the edges of the screen, for example a taskbar or a game HUD.">
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
          {(['cropLeft', 'cropRight', 'cropTop', 'cropBottom'] as const).map((k) => {
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
      </Section>

      <Section
        title="Black bar detection"
        icon={<RectangleHorizontal size={18} />}
        description="Ignores black bars around movies and ultrawide content so the LEDs show the picture, not the bars."
      >
        <Toggle label="Detect black bars" checked={border.enable === true} onChange={set('blackborderdetector', 'enable')} />
        {border.enable === true && (
          <>
            <Select
              label="Detection"
              value={String(border.mode ?? 'default')}
              hint={BORDER_MODES[String(border.mode)]?.hint}
              options={Object.entries(BORDER_MODES).map(([value, m]) => ({ value, label: m.label }))}
              onChange={set('blackborderdetector', 'mode')}
            />
            <SliderField
              label="Black bar threshold"
              unit="%"
              min={threshold.min}
              max={Math.min(threshold.max, 50)}
              step={1}
              value={Number(border.threshold ?? 5)}
              onChange={set('blackborderdetector', 'threshold')}
              hint="Raise it if dark gray bars aren’t detected."
            />
            <Advanced label="Detection timing">
              <div className="grid gap-4 sm:grid-cols-2">
                <NumberField integer label="Frames to confirm a bar" min={0} value={Number(border.borderFrameCnt)} onChange={set('blackborderdetector', 'borderFrameCnt')} />
                <NumberField integer label="Frames before resetting" min={0} step={50} value={Number(border.unknownFrameCnt)} onChange={set('blackborderdetector', 'unknownFrameCnt')} />
                <NumberField integer label="Inconsistent frames tolerated" min={0} value={Number(border.maxInconsistentCnt)} onChange={set('blackborderdetector', 'maxInconsistentCnt')} />
                <NumberField integer label="Blur pixels removed" unit="px" min={0} value={Number(border.blurRemoveCnt)} onChange={set('blackborderdetector', 'blurRemoveCnt')} />
              </div>
            </Advanced>
          </>
        )}
      </Section>

      <Advanced label="Advanced capture options">
        <SliderField
          label="Capture width"
          unit="px"
          min={width.min}
          max={width.max}
          step={4}
          value={Number(grabber.videoMode ?? 512)}
          onChange={set('systemGrabber', 'videoMode')}
          hint="The picture is scaled down to this width before the colors are calculated. Larger is slower and rarely looks different."
        />
        {grabber.hardware === true && (
          <NumberField
            integer
            label="Display order"
            hint="Change only if the wrong monitor is captured with several displays connected."
            min={0}
            max={255}
            value={Number(grabber.reorder_displays ?? 0)}
            onChange={set('systemGrabber', 'reorder_displays')}
          />
        )}
        <NumberField
          integer
          label="Priority"
          hint="Lower numbers win over higher ones. Effects and colors you start use priority 1."
          min={range('systemControl', 'systemInstancePriority', { min: 100, max: 253 }).min}
          max={range('systemControl', 'systemInstancePriority', { min: 100, max: 253 }).max}
          value={Number(control.systemInstancePriority ?? 245)}
          error={err('systemControl.systemInstancePriority')}
          onChange={set('systemControl', 'systemInstancePriority')}
        />
      </Advanced>
      {!enabled && (
        <Callout tone="info" icon={<ScreenShare size={16} />}>
          Screen capture is off. The settings above apply once you turn it on.
        </Callout>
      )}
    </PageFrame>
  );
}
