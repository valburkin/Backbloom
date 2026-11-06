# UI guidelines

Backbloom is used by people who just bought an LED strip, not by lighting engineers. Every screen should answer "what
do my lights do and how do I change that" with as few visible controls as possible. These rules keep it that way as
features are added.

## Structure

One app, no modes. The sidebar has two groups (`MAIN_NAV` and `SHARED_NAV` in `ui/src/app/nav.tsx`): four sections
about what the lights show and how they look, and the hardware and the app at the bottom, no more:

| Section | Pages | What it's for |
|---|---|---|
| Home | Home | What the lights show right now: on/off and brightness; Playing now (what the setup in use plays, its name and the live preview; Change leads to Effects, nothing to pick there); the setup switch (Everyday and the presets, always one in use) with the Look of the setup in use; health |
| Effects | Effects | What the setup in use plays: the setup switch in the header, a live preview that stays in view while scrolling, then Screen (Screen edges, Screen average), Color, Effects and Music, the current pick highlighted |
| Presets | Presets | The setups: Everyday (what plays and its Look) and the saved presets (what plays, a Look, the programs that use each one) |
| Colors | Colors | Fine-tuning the Look of the setup in use: saturation, color brightness, minimum glow, transitions |
| Setup | Layout, Devices, Screen, Calibration | The hardware: where the strips are and what each part shows (Layout → Customize), what they're plugged into, how the screen is read, and what makes the LEDs match the screen and the power supply (white balance, contrast, per-color calibration, power limit) |
| Settings | General (with the startup and idle effects), Remote access, Integrations, About (Support with expert tools) | Everything about the app itself |

A new feature goes into one of these sections; Setup when it is about the hardware. Each setting lives in exactly one
place; Home may offer a shortcut to a setting, but it is the same control and the same value.

**Setups and the Show.** One setup is always in use: Everyday (the layout's own strips with the saved Look) or a
preset (its strip sections and its Look; none of its own = Everyday's). A preset picked by hand stays in use after a
restart (the backend keeps it in the `presets` settings; the UI only sends `profile select`). What a setup plays, the Show (Screen edges,
Screen average, an effect, music or a color), is stored only in its strip sections, so nothing the user picks is
temporary and nothing is started for a while. A pick on Effects is written into the setup in use (`usePick` in
`ui/src/control/usePick.ts`, the logic in `control/setup.ts` and `control/show.ts`): every part that isn't Off or a
gap plays it (an effect's parts form one zone, so it flows across all strips), replacing what the strips were
customized with (a toast with Undo then), and the layout is saved at once. A pick never switches to another setup.
What plays is read back from the sections; a mix is "Customized in Layout". Two exceptions show something for a
while: dragging the color picker (the color is sent live, then saved when the drag ends) and a source another app
started (Playing now says so, with **Back to <setup>**). The Look on Home and the Look values on Colors save to the
setup in use too ("Changes save to Gaming"); the other Colors settings are the same for every preset, and Colors says
so. Picks and Looks change the layout as saved last (and the edited one the same way, a mirror: `editor/mirror.ts`),
so Layout edits that aren't saved yet neither block them nor get saved with them. While a mirror is saved it doesn't
count as an unsaved Layout edit; a failed save puts what it wrote back to the layout saved last, value by value, where
nothing changed it since. Every layout save goes through the editor's one queue (`quietSave` in `app/editor.tsx`,
`app/layoutSaver.ts`); changes saved together are made one at a time, so one that can't be made fails alone. A layout
that never had presets gets Gaming, Movie, Music and Cozy at its first save with strips (`withDefaultPresets` in
`control/presets.ts`, applied in `saveLayout`); deleting them leaves an empty list. A preset that plays one thing on
all its parts follows the strips: each save gives strips added or reshaped since the last one its pick
(`followStrips`; a list a pick wrote for a strip while it was still being drawn doesn't make the preset customized);
presets customized in Layout keep their lists. The previews show what the LEDs light right now: each
channel at the screen value of its light, brightened by part of what Home's Brightness took away (the stream's
`share`), so they get darker along the slider but stay readable at the low brightness most people use; colors keep
their hue, LEDs that are off stay off (`live/previewLight.ts`). The light around the LEDs adds up like light: a glow
close to each LED and a faint wide wash, each by the LED's own color and brightness (`LayoutLights`).

To send the user to one control (Home's "Lights look wrong?" troubleshooter does), link with a focus:
`hrefFor('capture', null, 'crop')` is `#/capture?focus=crop`. The page scrolls to the element with that id (else the
first with `data-focus="crop"`), opens the More options around it (and, with `data-focus-open`, its own) and
highlights it briefly. Give the control a stable id when a link needs it, and keep the troubleshooter's links working
when moving it (`ui/src/control/Troubleshooter.tsx`).

## Setting tiers

Every setting has a tier. Prefer them in this order: **automate** (the app decides) > **preset** (one choice sets
several values, like the Look) > **advanced** > **essential**. A preset lives in one place (Home); the
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
| What a COB strip lights as one (one chip's run of tiny LEDs) | lit group ("lights in groups of LEDs"; one LED to Backbloom) | segment, pixel |
| A physical strip | strip | segment |
| Hardware that receives colors | device | controller, LED controller, driver |
| The WLED electronics while Set up a WLED board finds it and its strips are set up | board (it becomes a device when added) | controller, ESP, chip |
| Its protocol choice | device type; its settings: connection settings | driver, LED driver settings |
| A pin/port on a device | output (WLED's word) | bus |
| What the lights show | Show; its choices: Screen edges, Screen average, an effect, music, a color (“Playing now”, “What plays”) | Mode, Ambient / Game mode, Now showing |
| Where a change goes | “Changes save to Gaming” (the setup in use), “Your pick is saved to it” | temporary, Stop |
| A saved setup: what plays, a Look, programs that use it while they run | Preset (“Used while running”, “Use”, “In use”) | profile, game, scene |
| The setup without a preset (the layout's own strips, the saved Look) | Everyday (“Use Everyday”) | default, no preset, off |
| How the lights look (the five presets on Home) | Look: Relaxed, Natural, Rich, Vivid, Intense | Mood, Intensity, style |
| Lights follow the screen | Screen sync | screen capture, mirror, grabber |
| Lights react to sound | Music (what's shown), sound input (the setting) | sound capture |
| How gently colors change | transitions | smoothing, smoothness |
| Rates | fps (screen frames), per second (LED updates) | Hz |
| Durations | seconds (0.35 s) | ms |
| A corner of a drawn strip | bend | point |
| The way a strip runs, from its first LED to its last | route (“Trace your strip”, “Use this route”); its turns: corners | path, wiring order, bends (that is Layout's word for a drawn strip) |
| A pin of a WLED board while Find your strips looks for the strips | connection (“Light the next connection”) | pin, GPIO, candidate |
| A piece between bends | part | section |
| LEDs matched to bends | matched | pinned |
| LEDs off on purpose | turned-off LEDs | dark parts |
| Switching between Everyday and the presets | Use; one is always in use, so never “turn off” a preset (it hides what comes instead) | Turn on, Turn off, Not now, Preview, activate |
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
  button (`.btn-brand`, the landing page's: a sheen on hover and a soft halo), a switch that is on, Home's brightness
  slider, the active section's marker, and the thin edge of a chosen option or of the card in use (`.choice-on`,
  `.edge-brand`). Never for status, never for text. Other sliders fill in violet (pass `rangeFill(value, min, max)` as
  their style).
- Depth without shadows: the app is dark only, where a drop shadow barely shows. Cards (`.card`) stand out by a lighter,
  translucent surface, a faint white edge and a light top edge; hairlines (`line`, `line-strong`) are translucent white.
  Only what floats over the page (menus, dialogs, toasts, the unsaved-changes bar) has a shadow (`FLOATING`), and only
  brand-colored things glow (and the selected strip in Layout, in its own color).
- Corners: cards and dialogs 24 px (`rounded-3xl`); tiles, notices and menus 16 px (`rounded-2xl`); inputs 12 px
  (`rounded-xl`), small ones in a toolbar or row 8 px (`rounded-lg`, `INPUT_SMALL`), search boxes pills; buttons and
  the options of "pick one" rows pills (`rounded-full`), their tracks rounded to match (20 px).
- The page glow behind the top of every page (`.app-glow` on the main area) shows the brand colors; on Home it takes
  the colors the lights show right now (`useAppGlow`, once a second, not with reduced motion). Cards are `CARD` (`Card`, `Section`), slightly translucent so the glow shows
  through; a hand-made element that must look like a button uses `buttonClass`, not its own colors.
- One container level: a section is a card; inside it, separate groups with spacing or `divide-y divide-line`, not
  nested bordered boxes. More options is a divider and a disclosure, not a box.
- One primary button per view, and only when it does something now (Save is primary only with unsaved changes).
- "Pick one" controls: `Segmented` for short labels, `ChoiceCard` for options with a title and a line of text. A row of
  short options (Segmented, tabs; the Look as a grid of five) is a pill track (`CHOICE_TRACK`) whose chosen option is a thumb that slides
  to it (`useChoiceThumb`). The chosen option always looks like `CHOICE_ACTIVE` (`ui/src/components/ui.tsx`); options
  not chosen are `TILE`s (a faint fill and edge), not outlined boxes.
- Color carries meaning: green, amber and red only for status, and status badges only when something needs
  attention ("91 / 91 LEDs" in green is noise). Section icons are muted. Only live things pulse
  (`StatusDot live`), and never with reduced motion.
- Contrast: hint text `ink-faint` (≥ 4.5:1), the edge of text inputs, selects and number boxes `line-input` (≥ 3:1;
  `INPUT_BASE`, `SELECT_CLASS`). Buttons and options are named by their text and need no such edge; the chosen option's
  gradient edge keeps 3:1. Number boxes have no spin arrows.
- Settings pages are at most `max-w-3xl` wide; short inputs (numbers, ports, selects with short options) use
  `max-w-xs`.
- Touch targets at least 44 px on touch screens (`pointer-coarse:`).
- A list that opens from a button is a `Menu` (`ui/src/components/Menu.tsx`), not an absolutely positioned box: it's
  drawn over the page, so scrolling panels don't clip it, and it handles outside clicks, Escape and arrow keys.

### Setup wizards

The guided setup's flows (Set up a WLED board, Add lights; `ui/src/setup`) share a few patterns:

- A wizard is a `Dialog` with `closeOnBackdrop={false}`: a stray click beside it mustn't lose a pairing or a board
  that is half set up. Its close button and Esc still close it (Set up a WLED board asks first).
- Its steps show as `StepDots` (`ui/src/setup/parts.tsx`): numbered dots with labels, the current one highlighted, the
  done ones checked. One primary button per step, in the footer; Back on the left.
- A choice of ways to go on (what to add, Add it as it is or Set it up again) is a list of `OptionButton`s: an
  icon, a title and one line of text, a whole row to click.
- What the board is doing is said in the step (`Busy`, "The board restarts…"), and Back is off while it runs.
