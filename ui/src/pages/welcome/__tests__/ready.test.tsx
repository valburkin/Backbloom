import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { defaultRoom, shapePath, type Controller, type LayoutSpec } from '../../../layout';
import { withRole } from '../../../setup/roles';
import { YourLights, stripsLine } from '../../../setup/YourLights';
import { fittedScreen, highlightFrame, monitorText, shownSteps, sizedMonitor, stepAfter, stripColorFrame } from '../ready';
import { FeelStepBody, RolesStepBody } from '../ReadySteps';
import { roleRows } from '../../../setup/roles';

const room = defaultRoom();
const wled: Controller = { id: 'wled', name: 'WLED', kind: 'wled', host: '192.168.1.77', outputs: [{ index: 0, start: 0, length: 91 }, { index: 1, start: 91, length: 77 }], device: { type: 'wled', host: '192.168.1.77' } };
const hue: Controller = { id: 'hue', name: 'Hue', kind: 'philipshue', host: '', outputs: [{ index: 0, start: 0, length: 1 }, { index: 1, start: 1, length: 1 }], device: { type: 'philipshue', host: '192.168.1.20' } };
const spec: LayoutSpec = {
  version: 1,
  room,
  controllers: [wled, hue],
  strips: [
    { id: 'strip-1', name: 'Monitor', controllerId: 'wled', output: 0, ledCount: 91, path: withRole(shapePath(room, 'around-screen'), 'soft') },
    { id: 'strip-2', name: 'Ceiling', controllerId: 'wled', output: 1, ledCount: 77, path: withRole(shapePath(room, 'ceiling-front'), 'soft') },
    { id: 'strip-3', name: 'Hue Go', controllerId: 'hue', output: 0, ledCount: 1, path: withRole(shapePath(room, 'line'), 'soft') },
    { id: 'strip-4', name: 'Hue Play', controllerId: 'hue', output: 1, ledCount: 1, path: withRole(shapePath(room, 'line'), 'soft') },
  ],
};
const noop = () => {};

describe('the first-run setup’s steps', () => {
  it('skips the roles without lights, and goes back and forth among the shown ones', () => {
    expect(shownSteps(true)).toEqual([0, 1, 2, 3, 4]);
    expect(shownSteps(false)).toEqual([0, 1, 3, 4]);
    expect(stepAfter(1, false)).toBe(3);
    expect(stepAfter(3, false, -1)).toBe(1);
    expect(stepAfter(1, true)).toBe(2);
    expect(stepAfter(4, true)).toBe(4);
  });

  it('lists everything added so far with the three ways to add more', () => {
    expect(stripsLine(spec, wled)).toBe('Monitor · 91 LEDs, Ceiling · 77 LEDs');
    expect(stripsLine(spec, hue)).toBe('Hue Go, Hue Play');
    const html = renderToStaticMarkup(<YourLights spec={spec} fresh={new Set(['hue'])} onAdd={noop} />);
    expect(html).toContain('WLED');
    expect(html.match(/>New</g)).toHaveLength(1);
    expect(html).toContain('Add more');
    expect(html).toContain('Room lights');
    expect(html).not.toContain('192.168.1.77');
  });
});

describe('the room picture', () => {
  it('sizes the screen as the monitor reports it, unless it has a size of its own', () => {
    const m = sizedMonitor([
      { name: 'Projector', primary: true, widthMm: null, heightMm: null },
      { name: 'DELL U2723QE', primary: false, widthMm: 597, heightMm: 336 },
    ]);
    expect(m?.name).toBe('DELL U2723QE');
    expect(monitorText(m!)).toBe('DELL U2723QE · 27 inches (60 × 34 cm)');
    const big = fittedScreen(room, { widthMm: 1430, heightMm: 800 })!;
    expect(big.width).toBeCloseTo(1.43);
    // standing where it stood: the bottom's middle kept
    expect(big.x + big.width / 2).toBeCloseTo(room.screen.x + room.screen.width / 2);
    expect(big.y + big.height).toBeCloseTo(room.screen.y + room.screen.height);
    expect(fittedScreen({ ...room, screen: { ...room.screen, width: 0.7 } }, { widthMm: 1430, heightMm: 800 })).toBeNull();
    expect(sizedMonitor([])).toBeNull();
  });

  it('shows the live colors (else each strip in its Layout color), the light being chosen picked out', () => {
    const frame = stripColorFrame(spec);
    expect(frame.slice(0, 3)).toEqual([0x22, 0xd3, 0xee]);
    expect(frame.slice(91 * 3, 91 * 3 + 3)).toEqual([0xf5, 0xa5, 0x24]);
    // the ceiling strip chosen: its LEDs as they are, the others at a quarter
    const picked = highlightFrame(spec, null, 'strip-2');
    expect(picked.slice(91 * 3, 91 * 3 + 3)).toEqual([0xf5, 0xa5, 0x24]);
    expect(picked.slice(0, 3)).toEqual([0x22, 0xd3, 0xee].map((v) => Math.round(v / 4)));
    // live colors once they show something
    const live = new Array(170 * 3).fill(200);
    expect(highlightFrame(spec, live, null).slice(0, 3)).toEqual([200, 200, 200]);
    expect(highlightFrame(spec, new Array(170 * 3).fill(0), null).slice(0, 3)).toEqual([0x22, 0xd3, 0xee]);
  });
});

describe('What each light does', () => {
  const body = (s: LayoutSpec) =>
    renderToStaticMarkup(
      <RolesStepBody spec={s} rows={roleRows(s)} frame={null} monitor="DELL U2723QE · 27 inches (60 × 34 cm)" selected="strip-1" onSelect={noop} onRole={noop} onTrace={noop} />,
    );

  it('shows the room, then three plain choices per light, every light the screen’s average to start with', () => {
    const html = body(spec);
    expect(html).toContain('Your lights in the room');
    expect(html).toContain('Your screen: DELL U2723QE');
    expect(html.match(/role="radiogroup"/g)).toHaveLength(4);
    expect(html).toContain('Screen edge');
    expect(html).toContain('Effect or color');
    expect(html.match(/aria-checked="true"[^>]*>Screen average/g)).toHaveLength(4);
    expect(html).toContain('Behind the monitor');
    expect(html).not.toContain('Trace its route');
  });

  it('offers to trace a strip that follows the screen’s edge again', () => {
    const traced = { ...spec, strips: spec.strips.map((x) => (x.id === 'strip-1' ? { ...x, path: withRole(x.path, 'screen') } : x)) };
    const html = body(traced);
    expect(html.match(/aria-checked="true"[^>]*>Screen edge/g)).toHaveLength(1);
    expect(html).toContain('Trace its route');
  });
});

describe('Feel and games', () => {
  it('uses Home’s presets, and adds running games', () => {
    const html = renderToStaticMarkup(
      <FeelStepBody mood="natural" intensity="vivid" disabled={false} onMood={noop} onIntensity={noop} games={[]} loadPrograms={async () => []} onAddGame={noop} />,
    );
    for (const label of ['Relaxed', 'Natural', 'Rich', 'Subtle', 'Vivid', 'Intense']) expect(html).toContain(`>${label}<`);
    expect(html.match(/aria-checked="true"/g)).toHaveLength(2);
    expect(html).toContain('Add a running game');
  });
});
