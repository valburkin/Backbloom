# UI guidelines

Backbloom is used by people who just bought an LED strip, not by lighting engineers. Every screen should answer "what
do my lights do and how do I change that" with as few visible controls as possible. These rules keep it that way as
features are added.

## Structure

The app is two apps in one: the Mode switch at the top of the sidebar (under the logo; in Home's header on a phone,
`ui/src/control/ModeSwitch.tsx`) chooses Ambient or Game. The sidebar has two groups (`MODE_NAV` and `SHARED_NAV` in
`ui/src/app/nav.tsx`), three sections for the mode and two shared ones at the bottom, no more:

| Section | Ambient | Game | What it's for |
|---|---|---|---|
| Home | Home | Home | Ambient: what the lights show right now (on/off, Show: screen, music, effect, color), brightness, Mood, health. Game: on/off, brightness, the game being played, Intensity, health |
| Colors | Colors | Colors, with Game's own values | Fine-tuning what Home's preset sets. Ambient: saturation, color brightness, minimum glow, transitions. Game: saturation, color brightness, reaction time, screen reads per second (no minimum glow while playing) |
| Effects & Music / Games | Effects & Music | Games | Ambient: the library of effects and music effects. Game: the games that turn Game mode on while they run |
| Setup (shared) | Layout, Devices, Screen, Calibration | the same | The hardware: where the strips are, what they're plugged into, how the screen is read, and what makes the LEDs match the screen and the power supply (white balance, contrast, per-color calibration, power limit) |
| Settings (shared) | General, Remote access, Integrations, About (Support with expert tools) | the same | Everything about the app itself |

Pages that differ between the modes show the mode next to their title (`ModeBadge`). A page the mode doesn't offer
leads Home: Effects & Music in Game mode (it shows only the screen, so it offers nothing that starts a color, effect or
music), Games in Ambient. Games stays open when the mode changes while it is open (a game quits or is removed, or
Ambient is chosen there). A new feature goes into one of these sections: the mode it serves (game events and room
reactions go to Game), or Setup when it is about the hardware and the same in both modes. Each setting lives in exactly
one place; Home may offer a shortcut to a setting, but it is the same control and the same value.

To send the user to one control (Home's "Lights look wrong?" troubleshooter does), link with a focus:
`hrefFor('capture', null, 'crop')` is `#/capture?focus=crop`. The page scrolls to the element with that id (else the
first with `data-focus="crop"`), opens the More options around it (and, with `data-focus-open`, its own) and
highlights it briefly. Give the control a stable id when a link needs it, and keep the troubleshooter's links working
when moving it (`ui/src/control/Troubleshooter.tsx`).

## Setting tiers

Every setting has a tier. Prefer them in this order: **automate** (the app decides) > **preset** (one choice sets
several values, like Mood and Intensity) > **advanced** > **essential**. A preset lives in one place (Home); the
page that fine-tunes its values says which preset they match.

| Tier | Shown | Use for |
|---|---|---|
| Essential | Always | What a regular user needs to get good light. At most about 7 per page, each with one short hint |
| Advanced | In the section's collapsed **More options** (`Advanced` in `ui/src/components/form.tsx`) | Fine-tuning with a visible effect that some users want |
| Expert | Only with **Settings → General → Show expert and diagnostic tools** on (`Expert` in `form.tsx`, `useExpertTools()`) | Diagnostics, protocol details, timeouts, priorities, test drivers, logs |

- Schema-driven forms take the tier from the schema's `access` (`"advanced"` or `"expert"`, none = essential).
  Hand-written pages put advanced settings inside `<Advanced>` and expert ones inside `<Expert>`.
- More options sits at the end of the section it belongs to, never as a page-level dump. Pass `changed`, `invalid`
  and `onReset` so it says how many values differ from the defaults, opens on a problem and offers a reset.
- An expert field is still shown when its value isn't the default or it has a problem.
- A setting whose effect can't be seen gets a preview or test button, or goes to Advanced.

## Words

One term per concept, in the UI, docs, toasts and backend messages that reach the user.

| Concept | Say | Don't say |
|---|---|---|
| The whole setup | lights | lighting, output |
| One LED | LED, numbered from 1 everywhere | pixel, LED position, LED #0 |
| A physical strip | strip | segment |
| Hardware that receives colors | device | controller, LED controller, driver |
| Its protocol choice | device type; its settings: connection settings | driver, LED driver settings |
| A pin/port on a device | output (WLED's word) | bus |
| What the lights show (Ambient's Home) | Show | Mode (that is Ambient / Game) |
| Ambient or Game | Mode: Ambient mode, Game mode | gaming mode, ambient lighting |
| A profile for a game | game (Games page: “Your games”, “Add a running game”) | game profile type, “It’s a game” |
| Lights follow the screen | Screen sync | screen capture, mirror, grabber |
| How the room feels (Ambient's preset) | Mood: Relaxed, Natural, Rich | intensity, style, profile |
| How punchy the lights are while playing (Game's preset) | Intensity: Subtle, Vivid, Intense | mood, level |
| Lights react to sound | Music (what's shown), sound input (the setting) | sound capture |
| How gently colors change | transitions | smoothing, smoothness |
| Rates | fps (screen frames), per second (LED updates) | Hz |
| Durations | seconds (0.35 s) | ms |
| A corner of a drawn strip | bend | point |
| A piece between bends | part | section |
| LEDs matched to bends | matched | pinned |
| LEDs off on purpose | turned-off LEDs | dark parts |
| Trying a profile | Preview / Stop preview | Try it now, tried |
| Network access | Remote access | listen on the local network |
| App credential | app key | token |
| The machine | this PC | this computer |
| The program | Backbloom | the lighting service, the service |

- Describe the effect the user sees, not the implementation ("Use the graphics card", not "Hardware acceleration").
- Hints: one sentence, plain words, one concrete example at most. Buttons: a verb ("Turn on lights", "Connect").
- American English, curly apostrophes and quotes (’ “ ”), en dash for ranges (30–60), spaced em dash for breaks,
  "100%" without a space, "for example" instead of "e.g.".

## Visual rules

- Type scale: page title 24 px (`text-2xl`), section title 16 px (`text-base`), labels and body 14 px (`text-sm`),
  hints 12 px (`text-xs`, `text-ink-faint`). Nothing smaller than 11 px. Like the landing page, titles (`h1`, `h2`) are
  Space Grotesk and everything else is Inter, both bundled (`@fontsource-variable`).
- The brand gradient (`bg-brand`: pink, violet, cyan, the logo's) is decoration and the one primary action: the primary
  button, a switch that is on, Home's brightness slider, the active section's marker. Never for status, never for
  text. Other sliders fill in violet (pass `rangeFill(value, min, max)` as their style).
- The page glow behind the top of every page (`.app-glow` on the main area) shows the brand colors; on Home it takes
  the colors the lights show right now (`useAppGlow`, once a second, not with reduced motion). Cards are `CARD` (`Card`, `Section`), slightly translucent so the glow shows
  through; a hand-made element that must look like a button uses `buttonClass`, not its own colors.
- One container level: a section is a card; inside it, separate groups with spacing or `divide-y divide-line`, not
  nested bordered boxes. More options is a divider and a disclosure, not a box.
- One primary button per view, and only when it does something now (Save is primary only with unsaved changes).
- "Pick one" controls: `Segmented` for short labels, `ChoiceCard` for options with a title and a line of text. The
  chosen option always looks like `CHOICE_ACTIVE` (`ui/src/components/ui.tsx`).
- Color carries meaning: green, amber and red only for status, and status badges only when something needs
  attention ("91 / 91 LEDs" in green is noise). Section icons are muted. Only live things pulse
  (`StatusDot live`), and never with reduced motion.
- Contrast: hint text `ink-faint` (≥ 4.5:1), input and choice borders `line-input` (≥ 3:1).
- Settings pages are at most `max-w-3xl` wide; short inputs (numbers, ports, selects with short options) use
  `max-w-xs`.
- Touch targets at least 44 px on touch screens (`pointer-coarse:`).
