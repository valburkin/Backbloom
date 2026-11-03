// Recognizing a strip's type (Set up a WLED board): the types in buyers' words with how to recognize each, three
// questions answered by looking at the strip, and the product name from an order. Pure functions, tested; the facts
// are the presets' (plan.ts STRIP_PRESETS), whose ids are the backend's strip table.
import { presetById, type StripPreset, type Volts } from './plan';

/** How a COB strip looks, and why its LEDs per meter are not what counts. */
export const COB_RECOGNIZE = 'Continuous light, no visible dots. The listing says LEDs/m, but it lights in groups of LEDs: look for “IC/m” or “pixels/m”';

/** A strip type as a buyer recognizes it. */
export interface StripChoice {
  /** A preset id (plan.ts, the backend's `strip`). */
  id: string;
  title: string;
  /** How to recognize it on the strip. */
  recognize: string;
  /** Other names sold as the same type (it is driven the same). */
  aliases: string;
  mostCommon?: boolean;
  /** A COB strip (counted in segments). */
  cob?: boolean;
}

/** The types to pick from, the most common first. */
export const STRIP_CHOICES: readonly StripChoice[] = [
  { id: 'ws2812b', title: 'WS2812B', recognize: '5 V, 3 wires', aliases: 'also WS2812, WS2813, SK6812 RGB', mostCommon: true },
  { id: 'sk6812-rgbw', title: 'SK6812 RGBW', recognize: '5 V, each LED has a yellow-white half', aliases: 'also SK6812 RGBWW, RGBNW' },
  { id: 'ws2815', title: 'WS2815', recognize: '12 V, 4 wires (one marked BI)', aliases: 'also WS2818' },
  { id: 'ws2811-12v', title: 'WS2811', recognize: '12 V, cut marks every 3 LEDs', aliases: 'also WS2811 24 V, UCS1903' },
  { id: 'ws2814-rgbw', title: 'WS2814 RGBW', recognize: '12 or 24 V, white half, 4 wires', aliases: 'also WS2814 RGBW 12 V' },
  { id: 'apa102', title: 'APA102 / SK9822', recognize: '5 V, 4 wires (one marked CLK or CI)', aliases: 'also APA107, HD107S' },
  { id: 'ws2812-cob', title: 'WS2812 COB', recognize: COB_RECOGNIZE, aliases: '5 V; also WS2812B COB, SK6812 COB', cob: true },
  { id: 'ws2811-cob', title: 'WS2811 COB', recognize: COB_RECOGNIZE, aliases: '12 or 24 V; also FCOB WS2811, UCS1903 COB', cob: true },
  { id: 'ws2814-cob-rgbw', title: 'WS2814 COB RGBW', recognize: COB_RECOGNIZE, aliases: '12 or 24 V with white; also COB RGBW', cob: true },
];

export function choiceOf(id: string | null): StripChoice | undefined {
  return STRIP_CHOICES.find((c) => c.id === id);
}

/** What a strip shows at its start: 3 pads/wires, or 4 with a clock (CLK, CI) or a backup data line (BI, BO). */
export type StripWires = '3' | '4-clock' | '4-backup';

/** The answers to "Not sure?". */
export interface StripLook {
  volts: Volts;
  wires: StripWires;
  /** The LEDs have a yellow-white half (RGBW). */
  white: boolean;
}

/** A type found from the answers or a product name, and what to say when it had to be guessed. */
export interface StripGuess {
  id: string;
  note: string | null;
}

/**
 * The type from what the strip looks like. Answers that fit no type exactly pick the one driven the same way (so it
 * still works) and say so.
 */
export function guessStripType({ volts, wires, white }: StripLook): StripGuess {
  if (wires === '4-clock')
    return { id: 'apa102', note: volts !== 5 ? 'Strips with a clock wire are 5 V (APA102, SK9822): check the volts printed on it.' : null };
  if (white) {
    if (volts === 5) return { id: 'sk6812-rgbw', note: null };
    return { id: 'ws2814-rgbw', note: wires === '4-backup' ? null : '12 and 24 V strips with a white half are usually WS2814 RGBW.' };
  }
  if (wires === '4-backup') {
    if (volts === 12) return { id: 'ws2815', note: null };
    if (volts === 5) return { id: 'ws2812b', note: 'A 5 V strip with a backup wire is a WS2813: it is driven like a WS2812B.' };
    return { id: 'ws2815', note: '24 V strips with a backup wire are rare: WS2815 is the closest (it is 12 V, so check the label).' };
  }
  if (volts === 5) return { id: 'ws2812b', note: null };
  return {
    id: 'ws2811-12v',
    note: `${volts} V strips with 3 wires are usually WS2811 (cut marks every 3 LEDs). If each LED can be cut on its own and there's a 4th wire, it's a WS2815.`,
  };
}

/** What a product name from an order tells: the type, the LEDs per meter (as Backbloom counts them) and the volts. */
export interface ListingMatch {
  id: string | null;
  /** LEDs per meter as Backbloom counts them (groups of 3 on WS2811, of 6 on WS2814 24 V), when the name says. */
  density: number | null;
  volts: Volts | null;
  white: boolean;
  note: string | null;
}

/** The density a listing gives (physical LEDs per meter) in the preset's terms, if it is one of its densities. */
function presetDensity(preset: StripPreset | undefined, perMeter: number | null, volts: Volts | null): number | null {
  if (!preset || !perMeter) return null;
  // WS2814 at 12 V lights in groups of 3, at 24 V in groups of 6
  const group = preset.id === 'ws2814-rgbw' && volts === 12 ? 3 : preset.groupOf;
  const n = Math.round(perMeter / group);
  return preset.densities.includes(n) ? n : group === 1 ? perMeter : n;
}

/**
 * The strip type from a product name as shops write it ("BTF-LIGHTING WS2812B 5V 60LED/m IP30"): the chip (and
 * whether it has white), the volts and the LEDs per meter. Null type when the name doesn't say.
 */
export function parseListing(text: string): ListingMatch {
  const t = ` ${text.toUpperCase().replace(/[‐-―]/g, '-')} `;
  // "5V", "DC5V", "DC 12V", "24VDC"
  const voltsMatch = /(?:\bDC\s*-?\s*|\b)(5|12|24)\s*V(?:DC)?\b/.exec(t);
  const volts = voltsMatch ? (Number(voltsMatch[1]) as Volts) : null;
  // "60LED/m", "60 LEDs/m", "144 Pixels/m", "60leds/Pixels/m", "60 LEDs per meter"
  const unit = '(?:LEDS?|PIXELS?|PCS|L)';
  const perMeterMatch =
    new RegExp(`\\b(\\d{2,3})\\s*${unit}(?:\\s*/\\s*${unit})?\\s*(?:/|PER\\s+)\\s*(?:M|METERS?|METRES?)\\b`).exec(t) ?? /\b(\d{2,3})\s*(?:LEDS?|PIXELS?)\s*M\b/.exec(t);
  const perMeter = perMeterMatch ? Number(perMeterMatch[1]) : null;
  const white = /\bRGB\s*-?\s*(?:W|WW|NW|CW)\b|\bRGBW\w*|\b4\s*-?\s*IN\s*-?\s*1\b/.test(t);
  // COB: its LEDs per meter (hundreds) aren't what counts, its chips (IC/m, pixels/m) are
  const cob = /\bF?COB\b/.test(t);
  const chipsMatch = /\b(\d{1,3})\s*(?:ICS?|PIXELS?|SEGMENTS?)\s*(?:\/|PER\s+)\s*(?:M|METERS?|METRES?)\b/.exec(t);
  const chips = chipsMatch ? Number(chipsMatch[1]) : null;
  if (cob) {
    const cobId = /\bWS\s*-?\s*2814\b/.test(t) || white ? 'ws2814-cob-rgbw' : /\bWS\s*-?\s*2811\b|\bUCS\s*-?\s*1903\b/.test(t) || (volts !== null && volts !== 5) ? 'ws2811-cob' : 'ws2812-cob';
    return {
      id: cobId,
      density: chips,
      volts,
      white,
      note: chips
        ? null
        : 'A COB strip lights in groups of LEDs, each one LED to Backbloom: its LEDs per meter aren’t the count. Look for “IC/m” or “pixels/m” on the product page.',
    };
  }
  let id: string | null = null;
  let note: string | null = null;
  if (/\b(?:APA\s*-?\s*10[27][A-Z]?|SK\s*-?\s*9822|HD\s*-?\s*107S?)\b/.test(t)) id = 'apa102';
  else if (/\bWS\s*-?\s*2814\b/.test(t)) id = 'ws2814-rgbw';
  else if (/\bWS\s*-?\s*281[58]\b/.test(t)) id = 'ws2815';
  else if (/\bSK\s*-?\s*6812\b/.test(t)) id = white ? 'sk6812-rgbw' : 'ws2812b';
  else if (/\bWS\s*-?\s*2811\b|\bUCS\s*-?\s*1903\b/.test(t)) {
    if (volts === 5) {
      id = 'ws2812b';
      note = 'A 5 V WS2811 strip has a chip per LED: it is driven like a WS2812B.';
    } else id = 'ws2811-12v';
  } else if (/\bWS\s*-?\s*281[23]B?\b/.test(t)) id = 'ws2812b';
  else if (white) id = volts === 5 ? 'sk6812-rgbw' : volts ? 'ws2814-rgbw' : null;
  if (id === 'ws2812b' && white) id = 'sk6812-rgbw';
  if (!id && /\bRGBIC\b|\bADDRESSABLE\b|\bPIXEL\b/.test(t)) note = 'The name doesn’t say which chip: the box or the shop’s listing usually does.';
  const preset = id ? presetById(id) : undefined;
  return { id, density: presetDensity(preset, perMeter, volts), volts, white, note };
}
