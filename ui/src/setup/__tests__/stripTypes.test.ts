import { describe, expect, it } from 'vitest';
import { STRIP_PRESETS, acceptsVolts, presetById } from '../plan';
import { STRIP_CHOICES, guessStripType, parseListing } from '../stripTypes';

describe('strip choices', () => {
  it('are presets the backend knows, the most common first', () => {
    for (const c of STRIP_CHOICES) expect(presetById(c.id), c.id).toBeDefined();
    expect(STRIP_CHOICES[0]).toMatchObject({ id: 'ws2812b', mostCommon: true });
    expect(STRIP_CHOICES.filter((c) => c.mostCommon)).toHaveLength(1);
    expect(new Set(STRIP_CHOICES.map((c) => c.id)).size).toBe(STRIP_CHOICES.length);
    // presets and choices agree on volts
    expect(acceptsVolts(presetById('ws2814-rgbw')!, 12)).toBe(true);
    expect(acceptsVolts(presetById('ws2814-rgbw')!, 24)).toBe(true);
    expect(acceptsVolts(presetById('ws2812b')!, 12)).toBe(false);
    expect(STRIP_PRESETS.every((p) => acceptsVolts(p, p.volts))).toBe(true);
  });
});

describe('Not sure? (what the strip looks like)', () => {
  it('finds each type from volts, wires and a white half', () => {
    expect(guessStripType({ volts: 5, wires: '3', white: false })).toEqual({ id: 'ws2812b', note: null });
    expect(guessStripType({ volts: 5, wires: '3', white: true })).toEqual({ id: 'sk6812-rgbw', note: null });
    expect(guessStripType({ volts: 12, wires: '4-backup', white: false })).toEqual({ id: 'ws2815', note: null });
    expect(guessStripType({ volts: 24, wires: '4-backup', white: true })).toEqual({ id: 'ws2814-rgbw', note: null });
    expect(guessStripType({ volts: 5, wires: '4-clock', white: false })).toEqual({ id: 'apa102', note: null });
  });

  it('picks the type driven the same way for answers that fit none exactly, and says so', () => {
    const ws2813 = guessStripType({ volts: 5, wires: '4-backup', white: false });
    expect(ws2813.id).toBe('ws2812b');
    expect(ws2813.note).toMatch(/WS2813/);
    expect(guessStripType({ volts: 12, wires: '3', white: false })).toMatchObject({ id: 'ws2811-12v', note: expect.stringMatching(/WS2811/) });
    expect(guessStripType({ volts: 12, wires: '4-clock', white: false }).note).toMatch(/5 V/);
    expect(guessStripType({ volts: 24, wires: '3', white: true }).id).toBe('ws2814-rgbw');
  });
});

describe('the product name from an order', () => {
  const cases: Array<[string, { id: string | null; density?: number | null; volts?: number | null }]> = [
    ['BTF-LIGHTING WS2812B 5V 60LED/m IP30', { id: 'ws2812b', density: 60, volts: 5 }],
    ['BTF-LIGHTING WS2812B RGB 5050SMD Individual Addressable 16.4FT 300LEDs Black PCB IP30 DC5V', { id: 'ws2812b', density: null, volts: 5 }],
    ['ALITOVE WS2812B LED Strip 144 Pixels/m 3.3ft Black PCB 5V', { id: 'ws2812b', density: 144, volts: 5 }],
    ['BTF-LIGHTING RGBW RGBNW Natural White SK6812 (Similar WS2812B) 16.4ft 5m 60leds/Pixels/m 300LEDs IP30 DC5V', { id: 'sk6812-rgbw', density: 60, volts: 5 }],
    ['SK6812 RGB 5V 30 LEDs/m', { id: 'ws2812b', density: 30 }],
    ['BTF-LIGHTING WS2815 (Upgraded WS2812B/WS2813) DC12V Individually Addressable 60 LEDs/m Dual-Signal', { id: 'ws2815', density: 60, volts: 12 }],
    ['WS2811 12V 5050 RGB LED Strip 60LEDs/m 20 Pixels/m', { id: 'ws2811-12v', density: 20, volts: 12 }],
    ['WS2811 DC24V addressable strip 60 LEDs/m IP65', { id: 'ws2811-12v', density: 20, volts: 24 }],
    ['BTF-LIGHTING WS2814 RGBW 4 in 1 DC24V 60LEDs/m 16.4FT', { id: 'ws2814-rgbw', density: 10, volts: 24 }],
    ['WS2814 RGBW 12V 60 LED/M', { id: 'ws2814-rgbw', density: 20, volts: 12 }],
    ['APA102C 5V 60 LEDs/m Black PCB', { id: 'apa102', density: 60, volts: 5 }],
    ['SK9822 Addressable LED Strip 144LED/M DC5V (Similar APA102)', { id: 'apa102', density: 144, volts: 5 }],
    ['Govee RGBIC LED Strip Lights 32.8ft', { id: null }],
  ];
  it.each(cases)('%s', (text, want) => {
    expect(parseListing(text)).toMatchObject(want);
  });

  it('knows COB strips light in segments: never takes their hundreds of LEDs/m as the count', () => {
    // the owner's strip: 720 LEDs/m, no IC/m in the name
    const owner = parseListing('BTF-LIGHTING FCOB WS2811 COB LED Strip 720LEDs/m DC24V Addressable');
    expect(owner).toMatchObject({ id: 'ws2811-cob', density: null, volts: 24 });
    expect(owner.note).toMatch(/IC\/m/);
    expect(parseListing('WS2814 COB RGBW 24V 784LEDs/m 70 IC/m')).toMatchObject({ id: 'ws2814-cob-rgbw', density: 70 });
    expect(parseListing('5V WS2812B COB strip 160 pixels/m')).toMatchObject({ id: 'ws2812-cob', density: 160 });
    for (const c of STRIP_CHOICES.filter((x) => x.cob)) {
      expect(presetById(c.id)?.cob, c.id).toBe(true);
      expect(c.recognize).toMatch(/no visible dots/);
    }
  });

  it('says when it had to choose', () => {
    expect(parseListing('WS2811 5V pixel strip 60 LED/m')).toMatchObject({ id: 'ws2812b', note: expect.stringMatching(/WS2812B/) });
    expect(parseListing('Govee RGBIC LED Strip Lights 32.8ft').note).toMatch(/doesn’t say/);
  });
});
