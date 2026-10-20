import { describe, expect, it } from 'vitest';
import serverInfo from '../../api/__fixtures__/serverinfo.json';
import type { PriorityInfo } from '../../api/types';
import { currentSource, describeSource, effectLabel, splitEffects } from '../modes';

const p = (componentId: string, extra: Partial<PriorityInfo> = {}): PriorityInfo => ({
  priority: 1,
  componentId,
  origin: 'Backbloom UI@::ffff:127.0.0.1',
  active: true,
  visible: true,
  ...extra,
});

describe('modes', () => {
  it('splits the real effect list into music and regular effects', () => {
    const { music, regular } = splitEffects(serverInfo.effects);
    expect(music).toHaveLength(9);
    expect(music[0]).toBe('Music: bass glow');
    expect(regular).toHaveLength(6);
    expect(regular).toContain('Rainbow swirl');
    expect(regular.some((n) => n.startsWith('Music:'))).toBe(false);
    expect(effectLabel('Music: stereo for LED strip (MULTI COLOR)')).toBe('Stereo');
    expect(effectLabel('Music: VU meter')).toBe('VU meter');
  });

  it('derives the mode from the visible source (captured priorities-update payloads)', () => {
    expect(currentSource([p('COLOR', { value: { RGB: [255, 100, 0] } })])).toMatchObject({ mode: 'color' });
    expect(currentSource([p('EFFECT', { owner: 'Music: stereo for LED strip (BLUE)' })]).mode).toBe('music');
    expect(currentSource([p('EFFECT', { owner: 'Rainbow swirl', visible: false }), p('SYSTEMGRABBER', { priority: 245 })]).mode).toBe('screen');
    expect(currentSource([]).mode).toBeNull();
  });

  it('describes sources in plain language', () => {
    expect(describeSource(p('COLOR', { value: { RGB: [255, 100, 0] } }))).toBe('Static color (255, 100, 0)');
    expect(describeSource(p('EFFECT', { owner: 'Music: stereo for LED strip (BLUE)' }))).toBe('Music: Stereo (BLUE)');
    expect(describeSource(p('SYSTEMGRABBER'))).toBe('Your screen');
    expect(describeSource(p('FLATBUFSERVER', { origin: 'Game plugin@10.0.0.4' }))).toBe('Game plugin');
    expect(describeSource(null)).toMatch(/idle/);
  });
});
