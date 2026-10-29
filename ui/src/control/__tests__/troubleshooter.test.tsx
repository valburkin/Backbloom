import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { symptoms, TroubleshooterDialog } from '../Troubleshooter';

describe('troubleshooter', () => {
  it('links every symptom to the control that fixes it', () => {
    const links = Object.fromEntries(symptoms({ held: false, expert: false }).map((s) => [s.id, s.links.map((l) => l.href)]));
    expect(links).toEqual({
      nothing: ['#/devices'],
      'color-order': ['#/devices?focus=connection'],
      direction: ['#/layout'],
      hdr: ['#/capture?focus=hdr'],
      crop: ['#/capture?focus=crop'],
      white: ['#/colors?focus=white-balance'],
      flicker: ['#/home?focus=intensity', '#/colors?focus=transitions'],
    });
  });

  it('offers the log only with expert tools, and points at Turn on lights while the lights wait', () => {
    const nothing = (o: { held: boolean; expert: boolean }) => symptoms(o).find((s) => s.id === 'nothing')!;
    expect(nothing({ held: false, expert: true }).links.map((l) => l.href)).toEqual(['#/devices', '#/settings/support']);
    expect(String(nothing({ held: true, expert: false }).fix)).toContain('Turn on lights');
  });

  it('renders the list', () => {
    const html = renderToStaticMarkup(<TroubleshooterDialog open onClose={() => {}} held={false} expert={false} />);
    expect(html).toContain('Lights look wrong?');
    expect(html).toContain('Red shows as green or blue');
    expect(html).toContain('href="#/capture?focus=crop"');
  });
});
