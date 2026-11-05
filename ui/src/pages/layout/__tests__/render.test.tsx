import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import legacyLeds from '../../../layout/__fixtures__/my-layout.json';
import wledCfg from '../../../layout/__fixtures__/wled-two-outputs.json';
import { controllerFromWledConfig, specFromLegacy, validateLayout, type LayoutSpec, type LedRect } from '../../../layout';
import { RoomCanvas } from '../RoomCanvas';
import { CountBadge, StripPanel } from '../StripPanel';
import { CustomizeEditor, PlaceEditor } from '../StripEditor';
import { IssueSummary } from '../IssueSummary';
import { PlaceGuide } from '../PlaceGuide';
import { ProfileSettings, ProfileTabs } from '../ProfileBar';

// Smoke test: the editor's presentational pieces render the product owner's real layout without throwing.
const spec = specFromLegacy(legacyLeds as LedRect[], controllerFromWledConfig('wled.local', wledCfg));
const noop = () => {};
/** The owner's two strips in the fixture: 91 + 77 LEDs. */
const FIXTURE_LEDS = 168;

describe('layout editor rendering', () => {
  it('renders the room with every LED, the handles of the selected strip and its sampling areas', () => {
    const canvas = (customize: boolean) =>
      renderToStaticMarkup(
        <RoomCanvas
          spec={spec}
          selection={{ stripId: spec.strips[0].id, pointIndex: 1, sectionIndex: 0 }}
          dispatch={noop}
          drawing={null}
          onDrawn={noop}
          found={{ stripId: spec.strips[0].id, local: 3 }}
          effects={['Candle']}
          rolesOnly={customize}
          showMapping={customize}
          onRoles={noop}
        />,
      );
    const place = canvas(false);
    expect((place.match(/<circle/g) ?? []).length).toBeGreaterThanOrEqual(168);
    expect(place).toContain('FRONT WALL');
    expect(place).toContain('Drag to add a bend here');
    expect(place).not.toContain('Drag along the screen’s edge');
    expect(place).toContain('hsl(');
    expect(place).not.toContain('NaN');
    // Customize: no handles to move the strip, but the dots that adjust what its parts follow.
    const customize = canvas(true);
    expect(customize).not.toContain('Drag to add a bend here');
    expect(customize).toContain('Drag along the screen’s edge');
  });

  it('draws every strip lit with the live colors, without the rainbow sampling areas', () => {
    const colors = new Array(400 * 3).fill(0).map((_, i) => (i % 3 === 0 ? 255 : i % 3 === 1 ? 120 : 10));
    const html = renderToStaticMarkup(
      <RoomCanvas
        spec={spec}
        selection={{ stripId: spec.strips[0].id, pointIndex: null, sectionIndex: null }}
        dispatch={noop}
        drawing={null}
        onDrawn={noop}
        found={null}
        effects={[]}
        rolesOnly
        showMapping={false}
        onRoles={noop}
        liveColors={colors}
      />,
    );
    expect((html.match(/fill="rgb\(255,120,10\)"/g) ?? []).length).toBe(FIXTURE_LEDS);
    // No rainbow (the LEDs' and their sampling areas' hsl colors).
    expect(html).not.toContain('hsl(');
    expect(html).not.toContain('opacity="0.4"');
    expect(html).toContain('opacity="0.75"');

    // LEDs without a color yet are drawn unlit.
    const empty = renderToStaticMarkup(
      <RoomCanvas spec={spec} selection={{ stripId: null, pointIndex: null, sectionIndex: null }} dispatch={noop} drawing={null} onDrawn={noop} found={null} effects={[]} rolesOnly onRoles={noop} liveColors={[]} />,
    );
    expect((empty.match(/fill="#1e222e"/g) ?? []).length).toBe(FIXTURE_LEDS);
    expect(empty).not.toContain('hsl(');
  });

  it('renders the strip list for both modes, with count badges only when they need attention', () => {
    const issues = validateLayout(spec);
    const list = (customize: boolean, s: LayoutSpec = spec) =>
      renderToStaticMarkup(<StripPanel spec={s} selectedId={s.strips[0]?.id ?? null} issues={issues} dispatch={noop} onAdd={noop} onAddDrawn={noop} customize={customize} />);
    expect(list(false)).toContain('91 LEDs');
    expect(list(false)).toContain('Add strip');
    expect(list(true)).toContain('Screen edge');
    expect(list(true)).not.toContain('Add strip');
    expect(list(false, { ...spec, controllers: [], strips: [] })).toContain('Add a device first.');
    expect(renderToStaticMarkup(<CountBadge count={77} length={91} />)).toContain('77 / 91 · 14 unused');
    expect(renderToStaticMarkup(<CountBadge count={77} length={undefined} />)).toContain('Not connected');
  });

  it('renders the Place and Customize editors of a strip', () => {
    const issues = validateLayout(spec);
    const sectioned = {
      ...spec.strips[1],
      path: spec.strips[1].path.map((p, i) => (i === 0 ? { ...p, next: { kind: 'area' as const, area: { hmin: 0, hmax: 1, vmin: 0, vmax: 1 } } } : p)),
      dark: [{ start: 0, count: 2 }],
    };
    for (const strip of [spec.strips[0], sectioned]) {
      const place = renderToStaticMarkup(<PlaceEditor spec={spec} strip={strip} issues={issues} dispatch={noop} onDraw={noop} onMatch={noop} />);
      expect(place).toContain('Number of LEDs');
      expect(place).toContain('Match with my lights');
      expect(place).toContain('Plugged into');
      expect(place).not.toContain('What it shows');
      const customize = renderToStaticMarkup(<CustomizeEditor spec={spec} strip={strip} issues={issues} dispatch={noop} effects={['Candle']} onRoles={noop} />);
      // following the Show, with what it takes from the screen with Screen sync (not for a strip whose parts differ)
      expect(customize).toContain('Follows the Show');
      expect(customize).toContain('Its own effect');
      expect(customize.includes('With Screen sync')).toBe(strip === spec.strips[0]);
      expect(customize).not.toContain('Choosing here switches them back to your layout.');
      expect(customize).toContain('Brightness');
      expect(customize).toContain('More options');
      expect(customize).not.toContain('Number of LEDs');
    }
    // While music shows: what it plays now, and that choosing brings the layout back (the last choice wins).
    const music = renderToStaticMarkup(<CustomizeEditor spec={spec} strip={spec.strips[0]} issues={issues} dispatch={noop} effects={['Candle']} showing="music" onRoles={noop} />);
    expect(music).toContain('Plays what your lights show — music now');
    expect(music).toContain('Your lights play music on every strip now. Choosing here switches them back to your layout.');
    expect(music).toContain('until you pick one for all your lights');
    // Turned-off LEDs count as a changed value of More options.
    const plain = { ...sectioned, depth: undefined, blend: undefined };
    expect(renderToStaticMarkup(<CustomizeEditor spec={spec} strip={plain} issues={issues} dispatch={noop} effects={['Candle']} onRoles={noop} />)).toMatch(/\((<!-- -->)?1(<!-- -->)? changed\)/);
    // A profile: only what it shows, with the effect it plays.
    const playing = { ...spec.strips[1], path: spec.strips[1].path.map((p, i) => (i === 0 ? { ...p, next: { kind: 'effect' as const, effect: 'Candle' } } : p)) };
    const html = renderToStaticMarkup(<CustomizeEditor spec={spec} strip={playing} issues={issues} dispatch={noop} effects={['Candle']} profileName="Witcher 3" onRoles={noop} />);
    expect(html).toContain('What it shows in Witcher 3');
    expect(html).toContain('shared by all presets');
    expect(html).not.toContain('Brightness');
  });

  it('renders profiles as tabs and a sentence', () => {
    const withProfile: LayoutSpec = { ...spec, profiles: [{ id: 'p1', name: 'Witcher 3', apps: ['witcher3.exe'], sections: {} }] };
    expect(renderToStaticMarkup(<ProfileTabs spec={withProfile} editing="p1" dispatch={noop} onEdit={noop} />)).toContain('Everyday');
    const sentence = renderToStaticMarkup(
      <ProfileSettings spec={withProfile} profileId="p1" dispatch={noop} onEdit={noop} loadPrograms={async () => []} previewing={false} onPreview={noop} />,
    );
    for (const words of ['is used while', 'witcher3.exe', 'Add program…', 'Pick from running apps', 'is running', 'Use', 'Switch to this preset now'])
      expect(sentence).toContain(words);
  });

  it('renders the guided steps, saying when the strips came from an import', () => {
    const guide = (imported: boolean, step: number) =>
      renderToStaticMarkup(
        <PlaceGuide imported={imported} state={{ step, tested: false }} onState={noop} onHide={noop} furniture={<span>FURNITURE</span>} matchLabel="Desk" onMatch={noop} onTest={noop} />,
      );
    expect(guide(true, 0)).toContain('Your strips need placing');
    expect(guide(true, 0)).toContain('FURNITURE');
    expect(guide(false, 2)).toContain('Match Desk');
    expect(guide(false, 3)).toContain('Show test pattern');
    expect(guide(false, 4)).toContain('All set');
  });

  it('renders problems only when there are some', () => {
    const issues = validateLayout(spec);
    expect(renderToStaticMarkup(<IssueSummary spec={spec} issues={[]} validatorError={null} blockers={[]} onPick={noop} />)).toBe('');
    const broken = [...issues, { severity: 'error' as const, message: 'Bend 2 of strip “Desk” is outside the room.', stripId: spec.strips[0].id }];
    expect(renderToStaticMarkup(<IssueSummary spec={spec} issues={broken} validatorError={null} blockers={[]} onPick={noop} />)).toContain('Problems');
    expect(renderToStaticMarkup(<IssueSummary spec={spec} issues={[]} validatorError="boom" blockers={[]} onPick={noop} />)).toContain('Couldn’t check the layout (boom)');
  });
});
