import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PLAN_INPUT,
  STRIP_PRESETS,
  currentLimitMa,
  diagonalInches,
  feedReachM,
  jointNear,
  jointText,
  ledPoint,
  normalizeInput,
  planFeeds,
  planStrips,
  presetById,
  screenFromDiagonal,
  startOptions,
  suggestSupply,
  supplyText,
  wireGauge,
  type PlanInput,
} from '../plan';

const monitor27: PlanInput = { ...DEFAULT_PLAN_INPUT, screen: { widthMm: 597, heightMm: 336 }, insetMm: 20 };

describe('strip presets', () => {
  it('has the ids the hardware protocol names, with sane facts', () => {
    for (const id of ['ws2812b', 'sk6812-rgbw', 'ws2811-12v', 'ws2815', 'ws2814-rgbw', 'apa102']) expect(presetById(id), id).toBeDefined();
    for (const p of STRIP_PRESETS) {
      expect(p.densities).toContain(p.defaultDensity);
      expect([5, 12, 24]).toContain(p.volts);
      expect(p.maPerLed).toBeGreaterThan(0);
      expect(p.ledsPerCut).toBeGreaterThanOrEqual(1);
    }
    expect(new Set(STRIP_PRESETS.map((p) => p.id)).size).toBe(STRIP_PRESETS.length);
  });

  it('feeds 5 V strips about every 2.5 m at 60 LEDs/m, less at higher densities, further at 12 and 24 V', () => {
    const ws = presetById('ws2812b')!;
    expect(feedReachM(ws, 60)).toBeCloseTo(2.5);
    expect(feedReachM(ws, 144)).toBeCloseTo(1.04, 2);
    expect(feedReachM(presetById('ws2815')!, 60)).toBeCloseTo(5);
    expect(feedReachM(presetById('ws2814-rgbw')!, 10)).toBeCloseTo(10);
  });
});

describe('screen size', () => {
  it('computes width and height from the diagonal and aspect ratio', () => {
    expect(screenFromDiagonal(27, '16:9')).toEqual({ widthMm: 598, heightMm: 336 });
    expect(screenFromDiagonal(34, '21:9')).toEqual({ widthMm: 797, heightMm: 333 });
    expect(screenFromDiagonal(65, '16:9').widthMm).toBe(1439);
    expect(diagonalInches({ widthMm: 597, heightMm: 336 })).toBe(27);
  });
});

describe('power', () => {
  it('suggests the next standard supply with 20% headroom', () => {
    expect(suggestSupply(5, 4.2)).toEqual({ volts: 5, amps: 6, watts: 30, count: 1, each: 6 });
    expect(suggestSupply(5, 5)).toMatchObject({ volts: 5, amps: 6, watts: 30 });
    expect(suggestSupply(12, 2.5)).toMatchObject({ volts: 12, amps: 3, watts: 36 });
    // above the largest 5 V supply (60 A): two of one size, each powering its own part
    expect(suggestSupply(5, 75)).toEqual({ volts: 5, amps: 120, watts: 600, count: 2, each: 60 });
    expect(suggestSupply(5, 60)).toMatchObject({ count: 2, each: 40 });
    expect(supplyText(suggestSupply(5, 75))).toBe('2 supplies of 5 V, 60 A each (600 W in all), each powering its own part of the strip');
  });

  it('limits the board to 85% of the supply', () => {
    expect(currentLimitMa(10)).toBe(8500);
    expect(currentLimitMa(4)).toBe(3400);
  });

  it('picks thicker power wire for more current', () => {
    expect(wireGauge(2)).toMatch(/20 AWG/);
    expect(wireGauge(8)).toMatch(/16 AWG/);
    expect(wireGauge(12)).toMatch(/14 AWG/);
  });
});

describe('planStrips', () => {
  it('plans a 27" monitor on three sides from the bottom left, clockwise', () => {
    const plan = planStrips(monitor27)!;
    expect(plan.pieces.map((p) => [p.label, p.leds, p.arrow, p.firstLed, p.lastLed])).toEqual([
      ['Left', 17, 'up', 1, 17],
      ['Top', 33, 'right', 18, 50],
      ['Right', 17, 'down', 51, 67],
    ]);
    expect(plan.totalLeds).toBe(67);
    // 17 LEDs at 60/m: cut at 28.3 cm, within the 29.6 cm of room
    expect(plan.pieces[0]).toMatchObject({ spaceMm: 296, cutMm: 283 });
    expect(plan.joints.map((j) => [j.corner, j.afterLed, j.join])).toEqual([
      ['top-left', 17, 'connector'],
      ['top-right', 50, 'connector'],
    ]);
    expect(plan.fullWhiteA).toBeCloseTo(4.02);
    expect(plan.suggestedSupply).toMatchObject({ volts: 5, amps: 6, watts: 30, count: 1 });
    expect(plan.limitMa).toBe(5100);
    expect(plan.feeds.map((f) => f.led)).toEqual([1]);
    expect(plan.warnings).toEqual([]);
  });

  it('starts from the bottom right when asked, running counterclockwise', () => {
    const plan = planStrips({ ...monitor27, start: 'bottom-right', clockwise: true })!;
    expect(plan.input.clockwise).toBe(false);
    expect(plan.pieces.map((p) => [p.label, p.arrow])).toEqual([
      ['Right', 'up'],
      ['Top', 'left'],
      ['Left', 'down'],
    ]);
  });

  it('goes around four sides, leaving the stand out and starting next to it', () => {
    const plan = planStrips({ ...monitor27, sides: 4, start: 'bottom-middle', clockwise: true, standGapMm: 200 })!;
    expect(plan.pieces.map((p) => p.label)).toEqual(['Bottom, left of the stand', 'Left', 'Top', 'Right', 'Bottom, right of the stand']);
    expect(plan.pieces[0]).toMatchObject({ arrow: 'left', leds: 10 });
    expect(plan.joints.every((j) => j.kind === 'corner')).toBe(true);
    expect(plan.joints.map((j) => j.corner)).toEqual(['bottom-left', 'top-left', 'top-right', 'bottom-right']);
  });

  it('bridges the stand with a wire when the run starts at a corner', () => {
    const plan = planStrips({ ...monitor27, sides: 4, start: 'top-left', clockwise: true, standGapMm: 200 })!;
    expect(plan.pieces.map((p) => p.label)).toEqual(['Top', 'Right', 'Bottom, right of the stand', 'Bottom, left of the stand', 'Left']);
    const jumper = plan.joints.find((j) => j.kind === 'jumper')!;
    expect(jumper).toMatchObject({ afterLed: 33 + 17 + 10, wireMm: 260 });
    expect(plan.checklist.some((c) => c.text.startsWith('Across the stand: 26 cm'))).toBe(true);
    // counterclockwise from the same corner goes down the left side first
    const ccw = planStrips({ ...monitor27, sides: 4, start: 'top-left', clockwise: false, standGapMm: 200 })!;
    expect(ccw.pieces[0]).toMatchObject({ label: 'Left', arrow: 'down' });
  });

  it('keeps the bottom in one piece without a stand', () => {
    const plan = planStrips({ ...monitor27, sides: 4, start: 'bottom-left', clockwise: true, standGapMm: 0 })!;
    expect(plan.pieces.map((p) => p.label)).toEqual(['Left', 'Top', 'Right', 'Bottom']);
    expect(plan.joints).toHaveLength(3);
  });

  it('snaps LED counts to the cut marks and feeds long runs again within the reach', () => {
    const tv: PlanInput = { ...DEFAULT_PLAN_INPUT, screen: screenFromDiagonal(65, '16:9'), sides: 4, start: 'bottom-middle', standGapMm: 0, insetMm: 30 };
    const plan = planStrips(tv)!;
    expect(plan.totalLeds).toBeGreaterThan(250);
    // 5 V at 60/m: a feed 150 LEDs after the start (the next corner is further: no stretch longer than the reach)
    expect(plan.feeds.map((f) => f.where)).toEqual(['LED 1, the start', 'LED 150, along the top']);
    // a longer run gets a feed along a side and one at the far end
    const big = planStrips({ ...tv, screen: screenFromDiagonal(75, '16:9') })!;
    expect(big.feeds.map((f) => f.led)).toEqual([1, 150, big.totalLeds]);
    expect(big.feeds[1].where).toBe('LED 150, along the top');
    expect(big.feeds[2].where).toBe(`LED ${big.totalLeds}, the end`);
    const grouped = planStrips({ ...tv, preset: 'ws2811-12v', density: 20 })!;
    // groups of 3 at 20 per meter: far fewer LEDs, no extra feed at 12 V
    expect(grouped.totalLeds).toBeLessThan(plan.totalLeds / 2);
    expect(grouped.feeds.length).toBeLessThanOrEqual(2);
  });

  it('warns when the supply’s volts don’t match the strip, or it is too small', () => {
    const wrong = planStrips({ ...monitor27, supply: { volts: 12, amps: 5 } })!;
    expect(wrong.warnings[0]).toMatchObject({ tone: 'bad' });
    expect(wrong.warnings[0].text).toMatch(/burn/);
    const low = planStrips({ ...monitor27, supply: { volts: 5, amps: 2 } })!;
    expect(low.warnings[0]).toMatchObject({ tone: 'warn' });
    expect(low.limitMa).toBe(1700);
    expect(planStrips({ ...monitor27, supply: { volts: 5, amps: 10 } })!.warnings).toEqual([]);
  });

  it('lists the parts, cuts and connections', () => {
    const plan = planStrips(monitor27)!;
    const text = plan.checklist.map((c) => `${c.group}: ${c.text}`);
    expect(text).toContain('Parts: WS2812B strip, 60 LEDs/m, at least 1.3 m (one 5 m reel)');
    expect(text).toContain('Parts: 2 L-shaped corner connectors for this strip (3-pin)');
    expect(text).toContain('Parts: A 5 V power supply, at least 6 A (30 W)');
    expect(text).toContain('Cut: Left: 17 LEDs (28 cm), LEDs 1–17');
    expect(text).toContain('Connect: Top-left corner: a corner connector, LED 17 to LED 18');
    const folded = planStrips({ ...monitor27, corners: 'fold' })!;
    expect(folded.checklist.filter((c) => c.group === 'Cut')[0].text).toBe('Cut the strip after LED 68 (113 cm)');
  });

  it('refuses a screen without a usable size', () => {
    expect(planStrips({ ...monitor27, screen: { widthMm: 0, heightMm: 300 } })).toBeNull();
    expect(planStrips({ ...monitor27, screen: { widthMm: 50, heightMm: 50 } })).toBeNull();
  });

  it('finds an LED on the run and the joint where lights stop', () => {
    const plan = planStrips(monitor27)!;
    const first = ledPoint(plan, 1);
    expect(first.x).toBe(20);
    expect(first.y).toBeLessThan(316);
    expect(ledPoint(plan, 18).y).toBe(20);
    const joint = jointNear(plan, 50)!;
    expect(jointText(joint)).toBe('the top-right corner (between LED 50 and LED 51)');
    expect(jointNear(plan, 30)).toBeNull();
  });

  it('keeps start and direction consistent with the sides', () => {
    expect(startOptions(3)).toEqual(['bottom-left', 'bottom-right']);
    expect(normalizeInput({ ...monitor27, start: 'top-left' }).start).toBe('bottom-left');
    expect(normalizeInput({ ...monitor27, density: 33 }).density).toBe(60);
    expect(normalizeInput({ ...monitor27, preset: 'nope' }).preset).toBe('ws2812b');
  });
});

describe('planStrips, edge cases', () => {
  it('cuts folded runs at the stand, counting LEDs along the run so corner LEDs match their places', () => {
    const plan = planStrips({ ...monitor27, sides: 4, start: 'top-left', clockwise: true, standGapMm: 200, corners: 'fold' })!;
    // top, right and bottom right in one piece, then bottom left and left in another
    // a side has the LEDs whose middle is on it; a run ends at the last cut mark that fits
    expect(plan.pieces.map((p) => p.leds)).toEqual([33, 18, 10, 11, 17]);
    expect(plan.pieces.map((p) => p.lastLed)).toEqual([33, 51, 61, 72, 89]);
    const cuts = plan.checklist.filter((c) => c.group === 'Cut').map((c) => c.text);
    expect(cuts[0]).toBe('Top to bottom, right of the stand: one piece of 61 LEDs (102 cm), LEDs 1–61');
    expect(cuts[1]).toBe('Bottom, left of the stand to left: one piece of 28 LEDs (47 cm), LEDs 62–89');
    // the top's 33 LEDs take 550 of its 557 mm, so the fold puts LED 34's middle 1.3 mm below the corner (cut at the
    // corner, it would be half a spacing, 8.3 mm, below it)
    expect(plan.pieces[1].offsetMm).toBeCloseTo(-7);
    expect(ledPoint(plan, 34).x).toBeCloseTo(577);
    expect(ledPoint(plan, 34).y).toBeCloseTo(20 + 1000 / 60 / 2 - 7, 1);
    const cut = planStrips({ ...monitor27, sides: 4, start: 'top-left', clockwise: true, standGapMm: 200, corners: 'connector' })!;
    expect(ledPoint(cut, 34).y).toBeCloseTo(20 + 1000 / 60 / 2, 1);
    // the second run starts afresh after the stand's wire
    expect(plan.pieces[3].offsetMm).toBe(0);
  });

  it('runs counterclockwise all around with the stand bridged', () => {
    const plan = planStrips({ ...monitor27, sides: 4, start: 'bottom-right', clockwise: false, standGapMm: 200 })!;
    expect(plan.pieces.map((p) => [p.label, p.arrow])).toEqual([
      ['Right', 'up'],
      ['Top', 'left'],
      ['Left', 'down'],
      ['Bottom, left of the stand', 'right'],
      ['Bottom, right of the stand', 'right'],
    ]);
    expect(plan.joints.map((j) => j.kind)).toEqual(['corner', 'corner', 'corner', 'jumper']);
  });

  it('doesn’t lose an LED to floating point', () => {
    // 2050 mm at 60/m is exactly 123 LEDs
    const plan = planStrips({ ...monitor27, screen: { widthMm: 2090, heightMm: 500 }, sides: 4, start: 'top-left', standGapMm: 0 })!;
    expect(plan.pieces[0]).toMatchObject({ label: 'Top', leds: 123 });
  });

  it('ends the feeds even when they can’t move on (a reach of one LED)', () => {
    const plan = planStrips(monitor27)!;
    // a feed that lands where one is already: no progress, so the loop stops instead of running forever
    expect(planFeeds(plan, plan.joints, plan.totalLeds, 1).map((f) => f.led)).toEqual([1, plan.totalLeds]);
    expect(planFeeds(plan, plan.joints, plan.totalLeds, 0.2).map((f) => f.led)).toEqual([1, plan.totalLeds]);
  });

  it('puts each power feed within the reach of the one before', () => {
    const plan = planStrips({ ...monitor27, screen: screenFromDiagonal(100, '16:9'), sides: 4, start: 'bottom-middle', standGapMm: 0, insetMm: 30 })!;
    const leds = [...plan.feeds.map((f) => f.led), plan.totalLeds];
    for (let k = 1; k < leds.length; k++) expect(leds[k] - leds[k - 1]).toBeLessThanOrEqual(150);
    expect(plan.feeds.length).toBeGreaterThanOrEqual(3);
  });
});
