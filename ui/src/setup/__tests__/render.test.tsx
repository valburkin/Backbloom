import { describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MockBackend } from '../../api/mock';
import { ConnectionProvider } from '../../app/connection';
import { ConfigProvider } from '../../app/config';
import { EditorProvider } from '../../app/editor';
import { ToastProvider } from '../../app/toast';
import { AddDeviceDialog } from '../../pages/devices/AddDeviceDialog';
import { DEFAULT_PLAN_INPUT, planStrips } from '../plan';
import { PlanDrawing } from '../PlanDrawing';
import { Checklist, DEFAULT_PLAN_FORM, PlanStep, PlanSummary, sizedScreens, withScreen } from '../PlanStep';
import { checkPattern } from '../ChecksStep';

function render(node: ReactNode): string {
  return renderToStaticMarkup(
    <ToastProvider>
      <ConnectionProvider backend={new MockBackend()}>
        <ConfigProvider>
          <EditorProvider>{node}</EditorProvider>
        </ConfigProvider>
      </ConnectionProvider>
    </ToastProvider>,
  );
}

const plan = planStrips({ ...DEFAULT_PLAN_INPUT, sides: 4, start: 'top-left', standGapMm: 200 })!;

describe('Add device', () => {
  it('asks what to add first', () => {
    const html = render(<AddDeviceDialog open onClose={() => {}} />);
    expect(html).toContain('LED strips on a WLED board');
    expect(html).toContain('Room lights');
    expect(html).toContain('Another kind of device');
  });

  it('opens each flow directly', () => {
    expect(render(<AddDeviceDialog open start="wled-new" onClose={() => {}} />)).toContain('Find your board');
    expect(render(<AddDeviceDialog open start="lights" onClose={() => {}} />)).toContain('Search for lights');
    expect(render(<AddDeviceDialog open start="existing" onClose={() => {}} />)).toContain('Search my network');
    expect(render(<AddDeviceDialog open={false} onClose={() => {}} />)).not.toContain('role="dialog"');
  });

  it('starts setting up a board with one Search, no planner and no address', () => {
    const html = render(<AddDeviceDialog open start="wled-new" onClose={() => {}} />);
    expect(html).toContain('Set up a WLED board');
    expect(html).not.toContain('Plan your strips');
    expect(html).not.toContain('Address');
    // one primary button per view: Search
    expect(html.match(/btn-brand font-semibold/g)).toHaveLength(1);
  });
});

describe('plan views', () => {
  it('draws the pieces, joints, the stand’s wire and the feeds, from the seat or behind', () => {
    const front = renderToStaticMarkup(<PlanDrawing plan={plan} />);
    expect(front).toContain('Strip plan: 87 LEDs on 5 pieces, seen from your seat');
    expect(front).toContain('33 LEDs');
    expect(front).toContain('stroke-dasharray="5 3"');
    expect(front.match(/<circle r="8"/g)).toHaveLength(plan.feeds.length);
    const back = renderToStaticMarkup(<PlanDrawing plan={plan} fromBehind print />);
    expect(back).toContain('Back of the screen');
    expect(back).not.toContain('var(--color');
  });

  it('sums up LEDs and power, warns about the supply, and groups the checklist', () => {
    const summary = renderToStaticMarkup(<PlanSummary plan={planStrips({ ...DEFAULT_PLAN_INPUT, supply: { volts: 12, amps: 5 } })!} />);
    expect(summary).toContain('Yours: 12 V, 5 A');
    expect(summary).toContain('burn the LEDs out');
    const list = renderToStaticMarkup(<Checklist items={plan.checklist} />);
    for (const g of ['Parts', 'Cut', 'Connect']) expect(list).toContain(`>${g}<`);
  });

  it('takes the screen’s size from this PC when it reports one', () => {
    const screens = [
      { name: 'A', primary: true, widthMm: 700, heightMm: 300 },
      { name: 'B', primary: false, widthMm: null, heightMm: null },
    ];
    expect(sizedScreens(screens)).toHaveLength(1);
    expect(withScreen({ ...DEFAULT_PLAN_FORM, source: 'detected', screen: 0 }, screens).input.screen).toEqual({ widthMm: 700, heightMm: 300 });
    expect(withScreen({ ...DEFAULT_PLAN_FORM, source: 'diagonal', diagonal: 34, aspect: '21:9' }, screens).input.screen.widthMm).toBe(797);
    const html = render(<PlanStep form={DEFAULT_PLAN_FORM} plan={planStrips(DEFAULT_PLAN_FORM.input)} screens={screens} onChange={() => {}} />);
    expect(html).toContain('A (30″)');
  });
});

describe('wizard steps', () => {
  it('lights a pattern per check', () => {
    expect(checkPattern(1, 42, 'red')).toEqual({ output: 0, pattern: 'range', count: 42 });
    expect(checkPattern(2, 42, 'green')).toEqual({ output: 0, pattern: 'color', color: [0, 255, 0] });
    // the power check: dim white until the user asks for full brightness
    expect(checkPattern(3, 42, 'red')).toEqual({ output: 0, pattern: 'color', color: [255, 255, 255] });
    expect(checkPattern(3, 42, 'red', 1, true)).toEqual({ output: 1, pattern: 'white' });
  });

});
