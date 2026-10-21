import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import legacyLeds from '../../../layout/__fixtures__/my-layout.json';
import wledCfg from '../../../layout/__fixtures__/wled-two-outputs.json';
import { controllerFromWledConfig, specFromLegacy, validateLayout, type LedRect } from '../../../layout';
import { RoomCanvas } from '../RoomCanvas';
import { StripPanel } from '../StripPanel';
import { StripEditor } from '../StripEditor';
import { IssueSummary } from '../IssueSummary';

// Smoke test: the editor's presentational pieces render the product owner's real layout without throwing.
const spec = specFromLegacy(legacyLeds as LedRect[], controllerFromWledConfig('wled.local', wledCfg));
const noop = () => {};

describe('layout editor rendering', () => {
  it('renders the room with every LED, the handles of the selected strip and its sampling areas', () => {
    const html = renderToStaticMarkup(
      <RoomCanvas
        spec={spec}
        selection={{ stripId: spec.strips[0].id, pointIndex: 1, sectionIndex: 0 }}
        dispatch={noop}
        drawing={null}
        onDrawn={noop}
        found={{ stripId: spec.strips[0].id, local: 3 }}
        effects={['Candle']}
        onRoles={noop}
      />,
    );
    expect((html.match(/<circle/g) ?? []).length).toBeGreaterThanOrEqual(168);
    expect(html).toContain('FRONT WALL');
    expect(html).toContain('Drag to add a bend here');
    expect(html).toContain('hsl(');
    expect(html).not.toContain('NaN');
  });

  it('renders strip cards, the strip editor, and the issue summary', () => {
    const issues = validateLayout(spec);
    expect(renderToStaticMarkup(<StripPanel spec={spec} selectedId={spec.strips[0].id} issues={issues} dispatch={noop} onAdd={noop} onAddDrawn={noop} />)).toContain('91 / 91');
    const sectioned = {
      ...spec.strips[1],
      path: spec.strips[1].path.map((p, i) => (i === 0 ? { ...p, next: { kind: 'area' as const, area: { hmin: 0, hmax: 1, vmin: 0, vmax: 1 } } } : p)),
      dark: [{ start: 0, count: 2 }],
    };
    for (const strip of [spec.strips[0], sectioned]) {
      const html = renderToStaticMarkup(<StripEditor spec={spec} strip={strip} issues={issues} dispatch={noop} effects={['Candle']} onRoles={noop} onDraw={noop} onMatch={noop} />);
      expect(html).toContain('Number of LEDs');
      expect(html).toContain('Match with my lights');
    }
    // A profile: only what it shows, with the effect it plays.
    const playing = { ...spec.strips[1], path: spec.strips[1].path.map((p, i) => (i === 0 ? { ...p, next: { kind: 'effect' as const, effect: 'Candle' } } : p)) };
    const html = renderToStaticMarkup(<StripEditor spec={spec} strip={playing} issues={issues} dispatch={noop} effects={['Candle']} profileName="Witcher 3" onRoles={noop} onDraw={noop} onMatch={noop} />);
    expect(html).toContain('What it shows in Witcher 3');
    expect(html).not.toContain('Number of LEDs');
    expect(renderToStaticMarkup(<IssueSummary spec={spec} issues={issues} validatorError={null} blockers={[]} onPick={noop} />)).toContain('Layout check');
  });
});
