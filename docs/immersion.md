# Immersion: where Backbloom is going

The long-term direction: from ambient lighting to game immersion. This page says what each part is, why, and the
decisions taken so far; the steps and their order are in [roadmap.md](roadmap.md#immersion).

Status: the direction is set, and the first piece is built: [presets and Looks](#one-app-presets-and-looks) on the LED
strips.

## Contents

- [The idea](#the-idea)
- [One app: presets and Looks](#one-app-presets-and-looks)
- [Outputs](#outputs)
- [Room and desk lights](#room-and-desk-lights)
- [Game events](#game-events)
- [Sound and haptics](#sound-and-haptics)
- [Wind](#wind)
- [Head tracking](#head-tracking)
- [Scent](#scent)
- [Figures in the room (research)](#figures-in-the-room-research)
- [Decided against, for now](#decided-against-for-now)

## The idea

Backbloom today mirrors the screen and the sound on LEDs. The next goal is game immersion as a whole: one app
that understands what happens in a game and drives everything around the player in sync: the lights behind the screen,
the room's lamps, the keyboard, mouse and PC, a shaker under the chair, a fan blowing in the player's face, and the
audio setup.

The approach: it works with any game, because it reads what the player sees and hears (screen capture and audio loopback, as today) and doesn't need the game to support it. Official game
telemetry is used where a game offers it, as a better signal, never as a requirement.

Rules that follow from this:

- **Never read game memory or inject into a game.** Anti-cheat software bans for that, and players must be able to
  trust that Backbloom never puts their accounts at risk. Screen capture, audio loopback and official telemetry interfaces only.
- **The game gets the GPU.** Everything Backbloom adds has a budget and backs off when the game needs the machine (see
  [game events](#game-events)).
- **Integrate, don't reinvent**: Dolby Atmos, DTS and Windows Sonic render the sound; OpenRGB and the vendor SDKs drive
  peripherals; Backbloom controls and coordinates them.
- **Software first.** Support hardware people already have or can buy or build cheaply.

## One app: presets and Looks

The app is one app: what the lights show (the screen, an effect, music or a color), how they look, and **presets**
that switch both, by hand or while a program runs. Every output works with every preset; a game preset is where game
events (the lights and the shaker react to them), wind and the audio setup per game come in.

Decided and built (an earlier version had two modes, Ambient and Game, which confused users):

- **One app, no modes.** The Effects page picks what the setup in use plays (Everyday or a preset), saved to its
  strips: nothing is temporary; strips can be customized on Layout. Home shows what plays with the live preview, the
  setup switch and the Look of the setup in use.
- **A Look** (Relaxed, Natural, Rich, Vivid, Intense) sets the transitions, the saturation and color brightness and
  the minimum glow (Vivid and Intense have none, so dark scenes stay dark). While transitions are fast (under 0.3 s:
  Vivid, Intense), the screen is read at least 60 times a second.
- **A preset is a layout profile**: what its strips show, an optional Look of its own, and optional programs that turn
  it on while they run. Picked by hand or used by its program; one is always in use, Everyday (the layout without a
  preset) or a preset, and picking Everyday keeps a program's preset unused until the program quits. A pick changes
  the setup in use and never switches to another one; turning a preset on clears the colors and effects other
  apps started, so its own strips show. Its Look applies on top of the saved settings, which stay as they are. A new
  layout comes with Gaming, Movie, Music and Cozy.

## Outputs

Today every controller is an LED device: a list of colors at the frame rate. Immersion needs outputs of different
kinds, each saying what it can do, and the engine gives each one what it can handle:

| Kind | Examples | Takes | Rate |
|---|---|---|---|
| LED strip | WLED, DDP, Adalight | one color per LED | 60 fps |
| Room light | Hue Entertainment, Nanoleaf, Govee, LIFX | one or a few colors per lamp | 10-25 updates/s |
| Peripheral | keyboard, mouse, headset, case fans, RAM, coolers | zones or keys | 30-60 fps |
| Haptic | bass shaker, seat pad, vest | a signal (audio-rate) or pulses | audio rate |
| Wind | fans on a controller | a speed per fan | 10-20 updates/s |

The room layout already places strips in the room; room lights and peripherals become objects in the same room (a lamp
in a corner, the PC case left of the desk), so effects and events can use where they are.

## Room and desk lights

- **Room lights.** Hue (Entertainment API, already a driver: about 25 updates/s, up to 10 lights per entertainment area,
  gradient strips take several colors), Nanoleaf (already a driver, panel streaming), LIFX, WiZ and Yeelight (already
  drivers). New: **Govee**, over two routes:
  - **LAN**, for models with "LAN Control" in the Govee app: the official LAN API sets one color per device and is
    rate-limited; the LAN streaming mode that other integrations use drives segments on some models; it isn't
    documented by Govee, so it's used only if its terms allow. Govee's discovery answers on a UDP port we'd listen
    on, so it falls under the network opt-in rules (Windows Firewall asks).
  - **Bluetooth LE**, for the many older models without LAN control (such as the H6199 TV backlight, used under the
    desk, has none: its Wi-Fi settings need the camera it came with). The commands are reverse-engineered
    (homebridge-govee, GoveeAPI); slower than LAN, fine for mood and event pulses, and no network listener. Windows'
    Bluetooth LE API, so platform code.
- **PC peripherals** through OpenRGB's SDK protocol (a TCP client to a local OpenRGB; OpenRGB is GPL, so we talk to it
  and don't link its code) and the Razer Chroma and Corsair iCUE SDKs (check their license terms).
- **What they show.** Light off to the side changes the player's gaze: the eye is most sensitive to movement and
  flicker at the edge of vision. So room lights and peripherals don't mirror the screen by default; they take the
  scene's mood slowly (a fade over seconds), follow the music's beat with music, and with a game preset react to
  events or dim while playing. The keyboard and mouse are in view
  below the screen and can do more. Mirroring stays possible as an option.

## Game events

Moments that make the room react: low health makes the room pulse red, a hit flashes the side it came from, a death
darkens everything, an explosion shakes the chair.

Signals, cheapest first:

1. **Generic picture cues**, the same in most games: a red damage vignette at the edges, white flashes, sudden
   blackouts and brightness drops (death, menus, loading). Plain image statistics on the downscaled frame that capture
   already makes; close to free.
2. **Generic sound cues**: loud low-frequency bursts (explosions, impacts), from the audio analysis music mode already
   has.
3. **Official telemetry** where a game offers it: Valve's Game State Integration (CS2, Dota 2: health, ammo, round
   state, posted to a local HTTP endpoint), racing and flight sims' data outputs (Forza and F1 UDP, Assetto Corsa
   shared memory). Listeners bind to loopback only.
4. **Per-game profiles** for things only one game shows, like where its health bar is. A large AI model works out the
   profile once, offline (from screenshots or videos of the game); at runtime Backbloom only reads those few pixels.
5. **Small runtime models** for what the cheap cues can't see: a tiny classifier on a low-resolution frame a few times a
   second (events don't need 60 fps), through ONNX Runtime (MIT), on the NPU where there is one (Copilot+ PCs; the game
   doesn't use it), then the CPU, the GPU last.

Budget: event detection runs off the frame loop on its own thread at a low rate, and doesn't allocate per frame. The
performance recording already measures Backbloom's and the whole system's GPU use; detection lowers its rate or turns
off model-based cues when the GPU is busy.

Events become reactions per output: a pulse, a flash on one side, a dim, a rumble, a gust. Profiles say which events a
game has and how each output reacts; sensible defaults work without a profile. This replaces the "game events" idea
from the layout work.

## Sound and haptics

**Audio setup per game.** Backbloom doesn't process the game's sound; it switches the setup when a game starts and back
when it quits:

| What | How | Note |
|---|---|---|
| Volume per app | Windows audio sessions (public API) | |
| Default output device (headset or speakers) | the undocumented policy interface switchers like SoundSwitch use | not public, but stable for years |
| Spatial sound format (Atmos, DTS, Windows Sonic) | the endpoint's stored setting | no public API; more fragile, test on each Windows update |
| EQ preset (footsteps, cinematic) | Equalizer APO's config file | GPL: write its file, don't link it |

Dolby Access and DTS Sound Unbound have no interface for other apps: Backbloom picks the format, their own settings stay
in their apps.

**Haptics.** A bass shaker (a transducer on the chair, through a small amplifier on a second audio output, often a cheap
USB sound card) is the cheapest strong effect. Most setups today just send the raw bass to it, music included.
Backbloom builds the shaker's signal itself: the game's low frequencies without the soundtrack where it can tell them
apart, plus pulses from game events (a hit, a landing, an engine's rumble from telemetry). Later: seat pads and vests
with their own SDKs (bHaptics; check terms). Controller rumble stays the game's: adding to it needs a virtual
controller driver, which is fragile and trips anti-cheat.

## Wind

Fans in front of the player, faster with speed. Smart plugs are too slow and only switch on and off, so the fans need a
controller with speed control: what sim racers build (two 12 V PC fans and an Arduino or ESP32 over USB). Support the
protocols the existing DIY kits use (look into SimHub's) so they work unchanged, and off-the-shelf USB wind units.

Speed from, best first: telemetry (racing and flight sims), how fast the picture moves (optical flow on the downscaled
frame; good for driving and flying, weak for shooters), gusts from events (an explosion) in any game.

## Head tracking

A webcam tracks the head, processed on the PC, never recorded or sent anywhere.

- **Looking around in the game**: Backbloom acts like a TrackIR/FreeTrack device. Works in the games that support those
  (mostly flight and racing sims). opentrack (ISC license) is the reference.
- **Head-tracked sound** (sound stays anchored to the screen when the head turns): the most immersive part, but it means
  Backbloom renders the final binaural sound between the game and the headphones (as far as we know, Atmos and DTS on
  Windows don't take head tracking from outside). That is building our own audio renderer, so it is a later step.

## Scent

Watched, not built. Scent has failed many times for physical reasons: smells linger (a scene can't switch in seconds),
cartridges run out, there are few scents and they blend into a muddle, and some people are allergic. It suits slow,
rare atmosphere (rain, forest, gunpowder). Devices are appearing (GameScent, for one); if one gets an API, integrate it.

## Figures in the room (research)

An idea to research, not a plan: game effects that leave the screen and appear in the room as 3D figures (an explosion
spilling over the desk, a creature in the corner). True mid-air 3D images don't exist at a usable size, brightness and
price: fog screens, Pepper's ghost, LED fans and floating-image plates are flat; laser-plasma and optical-trap displays
are tiny; swept-volume and light-field displays (Voxon, Looking Glass) work only inside their box or screen. The
realistic path is mixed reality: glasses or headsets drawing over the real room (Quest 3 passthrough, Vision Pro, light
AR glasses as they mature). Backbloom would add them as one more output fed by the same game events. Watch how
practical it becomes to wear AR glasses while playing on a monitor.

## Decided against, for now

- Mirroring the screen on PC peripherals by default (see [room and desk lights](#room-and-desk-lights)).
- Adding to controller rumble.
- An own spatial audio renderer, except later for head-tracked sound.
- Ultrawide fixes and other per-game patches.
- Reading game memory.
