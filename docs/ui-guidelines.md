# UI guidelines

Backbloom is used by people who just bought an LED strip, not by lighting engineers. Every screen should answer "what
do my lights do and how do I change that" with as few visible controls as possible. These rules keep it that way as
features are added.

## Structure

Five sections in the navigation, no more:

| Section | Pages | What it's for |
|---|---|---|
| Home | Home | What the lights show right now: on/off, Show (screen, music, effect, color), brightness, intensity, health |
| Setup | Layout, Devices | Where the strips are and what they're plugged into |
| Picture | Screen sync, Colors | How the screen is read and how its colors look on the LEDs |
| Effects & Music | Effects & Music | The library of effects and music effects |
| Settings | General, Remote access, Integrations, About (Support with expert tools) | Everything about the app itself |

A new feature goes into one of these. The only planned addition is a Profiles/Games page once game profiles grow
(see [roadmap.md](roadmap.md) and [immersion.md](immersion.md)). Each setting lives in exactly one place; Home may
offer a shortcut to a setting, but it is the same control and the same value.

To send the user to one control (Home's "Lights look wrong?" troubleshooter does), link with a focus:
`hrefFor('capture', null, 'crop')` is `#/capture?focus=crop`. The page scrolls to the element with that id (else the
first with `data-focus="crop"`), opens the More options around it (and, with `data-focus-open`, its own) and
highlights it briefly. Give the control a stable id when a link needs it, and keep the troubleshooter's links working
when moving it (`ui/src/control/Troubleshooter.tsx`).

## Setting tiers

Every setting has a tier. Prefer them in this order: **automate** (the app decides) > **preset** (one choice sets
several values, like Intensity) > **advanced** > **essential**.

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
| What the lights show (Home) | Show | Mode (reserved for the planned Ambient / Game modes) |
| Lights follow the screen | Screen sync | screen capture, mirror, grabber |
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
  hints 12 px (`text-xs`, `text-ink-faint`). Nothing smaller than 11 px.
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
