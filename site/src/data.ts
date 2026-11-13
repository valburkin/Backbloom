// Content that changes often: the device groups and the roadmap.

export interface GearItem {
  name: string;
  note: string;
}

export interface GearGroup {
  title: string;
  blurb: string;
  image: string;
  imageAlt: string;
  /** The roadmap chapter that brings it, while Backbloom doesn't support the category yet (no links then). */
  coming?: string;
  /** A live readout over the picture (see wind.ts). */
  live?: 'wind' | 'haptics';
  items: GearItem[];
}

export const gear: GearGroup[] = [
  {
    title: 'Screen kit',
    blurb: 'Everything for the glow behind your monitor: the strip, the controller and the power.',
    image: '/img/kit.webp',
    imageAlt: 'LED strip reel glowing in a rainbow next to a small controller board and a power supply',
    items: [
      { name: 'WLED controller', note: 'ESP32 or ESP8266 board, over Wi-Fi or USB' },
      { name: 'Addressable LED strip', note: '5 V, 60 LEDs per meter, for monitors and TVs' },
      { name: 'Power supply', note: 'Backbloom keeps the brightness within it' },
      { name: 'Corner connectors', note: 'Clean bends around the screen' },
    ],
  },
  {
    title: 'Room lights',
    blurb: 'Bulbs, light bars and panels around the room that join in with the screen.',
    image: '/img/lights.webp',
    imageAlt: 'Two light bars, a bulb and a hexagon panel glowing in magenta, cyan and violet',
    items: [
      { name: 'Philips Hue', note: 'Bridge, bulbs and light bars' },
      { name: 'Nanoleaf', note: 'Panels and shapes' },
      { name: 'WiZ and LIFX', note: 'Wi-Fi bulbs, no hub needed' },
      { name: 'Govee', note: 'Strips and light bars with LAN Control' },
    ],
  },
  {
    title: 'Haptics',
    blurb: 'Feel the bass, the engine and the hits through your chair.',
    image: '/img/haptics-live.webp',
    imageAlt: 'A gamer recoiling in a shaking chair as an explosion fills the monitor, the wall glowing orange behind it',
    coming: 'Feel',
    live: 'haptics',
    items: [
      { name: 'Bass shaker', note: 'Bolts to your chair or desk' },
      { name: 'Compact amplifier', note: 'Drives the shaker from your PC' },
    ],
  },
  {
    title: 'Wind',
    blurb: 'Speed you can feel on your face, from games that report it.',
    image: '/img/wind.webp',
    imageAlt: 'A sim racer whose hair is blown back by two small fans at the wheel, the wall glowing blue behind the monitors',
    coming: 'Air',
    live: 'wind',
    items: [
      { name: 'Fan kit', note: 'USB or ESP32 fans for sims and racers' },
    ],
  },
];

export type RoadmapStatus = 'live' | 'building' | 'next' | 'later' | 'goal';

export interface RoadmapItem {
  status: RoadmapStatus;
  title: string;
  text: string;
}

/** The roadmap as chapters, one per sense; each holds its milestones. Keep it in step with docs/roadmap.md. */
export interface RoadmapChapter {
  title: string;
  tagline: string;
  items: RoadmapItem[];
}

export const roadmap: RoadmapChapter[] = [
  {
    title: 'Light',
    tagline: 'Everything that glows follows your game.',
    items: [
      {
        status: 'live',
        title: 'Screen sync',
        text: 'LED strips behind your screen follow every scene, HDR included.',
      },
      {
        status: 'live',
        title: 'Presets',
        text: 'Calm for movies and music, fast and vivid for games. Your game turns its preset on by itself when it starts.',
      },
      {
        status: 'live',
        title: 'Your room, mapped',
        text: 'LED strips drawn where they really are in your room, smart bulbs and panels joining in, with music, effects and presets per game.',
      },
    ],
  },
  {
    title: 'React',
    tagline: 'The room reacts to what happens.',
    items: [
      {
        status: 'next',
        title: 'Game events',
        text: 'Hits, explosions and low health light up the room, read from the picture, the sound and official game data.',
      },
    ],
  },
  {
    title: 'Feel',
    tagline: 'Feel the game, not just see it.',
    items: [
      {
        status: 'later',
        title: 'Sound and haptics',
        text: "The right audio setup per game, and bass and impacts through your chair, built from the game's own sound.",
      },
    ],
  },
  {
    title: 'Air',
    tagline: 'Speed you feel on your face.',
    items: [
      {
        status: 'later',
        title: 'Wind',
        text: 'Fans that blow with your speed in racers and flight sims, plus gusts when things explode.',
      },
    ],
  },
  {
    title: 'Presence',
    tagline: 'Forget the room. Be in the game.',
    items: [
      {
        status: 'goal',
        title: 'Every sense, one moment',
        text: 'Light, sound, rumble and wind hit together on the same game moment, tuned for each game, until the room disappears and you are in it.',
      },
    ],
  },
];

export const statusLabel: Record<RoadmapStatus, string> = {
  live: 'Live',
  building: 'Building now',
  next: 'Next',
  later: 'Later',
  goal: 'The goal',
};
