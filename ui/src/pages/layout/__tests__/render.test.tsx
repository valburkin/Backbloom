import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import legacyLeds from '../../../layout/__fixtures__/my-layout.json';
import wledCfg from '../../../layout/__fixtures__/wled-two-outputs.json';
import { controllerFromWledConfig, specFromLegacy, validateLayout, type LedRect } from '../../../layout';
import { LayoutCanvas } from '../LayoutCanvas';
import { StripPanel } from '../StripPanel';
import { SegmentPanel } from '../SegmentPanel';
import { IssueSummary } from '../IssueSummary';

// Smoke test: the editor's presentational pieces render the product owner's real layout without throwing.
const spec = specFromLegacy(legacyLeds as LedRect[], controllerFromWledConfig('wled.local', wledCfg));
const noop = () => {};

describe('layout editor rendering', () => {
  it('renders the canvas with dots, start markers and sampling overlays', () => {
    for (const ratio of [16 / 9, 32 / 9]) {
      const html = renderToStaticMarkup(
        <LayoutCanvas spec={spec} ratio={ratio} selection={{ stripId: spec.strips[0].id, segmentIndex: 1 }} found={{ stripId: spec.strips[0].id, local: 3 }} onSelect={noop} />,
      );
      expect((html.match(/<circle/g) ?? []).length).toBeGreaterThanOrEqual(168);
      expect(html).toContain('<rect');
      expect(html).not.toContain('NaN');
    }
  });

  it('renders strip cards, every segment editor, and the issue summary', () => {
    const issues = validateLayout(spec);
    expect(renderToStaticMarkup(<StripPanel spec={spec} selectedId={spec.strips[0].id} issues={issues} dispatch={noop} />)).toContain('91 / 91');
    const strip = spec.strips[0];
    strip.segments.forEach((_, i) => {
      const html = renderToStaticMarkup(<SegmentPanel strip={strip} selectedIndex={i} issues={issues} dispatch={noop} />);
      expect(html).toContain('Number of LEDs');
    });
    expect(renderToStaticMarkup(<IssueSummary spec={spec} issues={issues} validatorError={null} blockers={[]} onPick={noop} />)).toContain('Layout check');
  });
});
