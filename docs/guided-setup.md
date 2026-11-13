# Guided hardware setup

Someone unpacks LED strips, a WLED board or two and a power supply. Backbloom should take them from the box to
working lights without sending them to the board's own web page, and without IP addresses: one Search finds the
board. Real setups vary (several strips per board, several boards, strips behind the screen, on the ceiling, the desk
or a shelf), so boards and strips are first-class; strips around a monitor are one case among them. This page
describes the parts and the protocol between the UI and the backend.

## Contents

- [Overview](#overview)
- [Set up a WLED board](#set-up-a-wled-board)
- [Plan the cuts and power](#plan-the-cuts-and-power)
- [Add lights](#add-lights)
- [Protocol: `hardware`](#protocol-hardware)
- [Trying it without hardware](#trying-it-without-hardware)
- [Safety](#safety)

The backend parts behind this flow (Improv, the WLED client, the board search, Wi-Fi handling) are described in
[Guided hardware setup (backend)](backend/hardware-setup.md).

## Overview

| Part | What the user does | Where |
|---|---|---|
| **Set up a WLED board** | Searches once (USB, the home Wi-Fi, new boards' hotspots), gives a new board the Wi-Fi, says what's on the power supply, finds each strip by lighting the board's connections one at a time and says where it runs, picks each strip's type and checks it with guided tests; then back to Your lights | Devices → Add device → LED strips on a WLED board, Home's and Devices' "No lights yet", and the first-run setup's Your lights |
| **Plan the cuts and power** | For a strip behind the monitor or TV that isn't cut yet: a cut plan, a power plan and a parts checklist | Optional, from that strip in Set up a WLED board ("Not cut yet? Plan the cuts and power") |
| **Add lights** | Adds room lights (Hue, Nanoleaf, LIFX, WiZ, Yeelight, Govee, Home Assistant) from one search, with one short prompt per brand where pairing needs it | Devices → Add device, and the first-run setup |

Add device (on Devices) first asks what to add: **LED strips on a WLED board**, **Room lights**, or **Another kind of
device** (DDP, Art-Net, E1.31, USB devices, or WLED by its address). The first-run setup's Your lights shows the same
three choices while there is no device (`ui/src/setup/SetupChoices.tsx`). All end with the devices added to the
layout (`useAddToLayout`, which also replaces the test device of a new install), so the Layout page places them next.

**Your lights** (`ui/src/setup/YourLights.tsx`). After a board, a set of room lights or another device is added, the
flow comes back to Your lights: everything added so far (one row per device with its strips or lights, "New" on the
ones added just now), Add more with the same three choices (another board, room lights, another device), and one
primary to go on. In the first-run setup that is its Your lights step (Next goes on with the setup); on Devices, Add
device's last view ("Added to your setup: Philips Hue. Check them on Layout, then save.", Done back to Devices, where the
Save bar saves the layout). A flow opened from there goes Back to it.

**Roles** (`ui/src/setup/roles.ts`). Every new strip and light shows the whole screen's average (Screen average, one
of the layout's own roles, `ROLE_CHOICES`), whether it comes from Set up a WLED board, Add lights, Another kind of
device or a lamp added to a lamp list on Devices. It looks right whichever way the strip runs, and sharp changes at
the edge of vision distract ([immersion.md](immersion.md)). Following the part of the screen next to it (Screen edge)
needs the strip's real route (from which end, which way round, across which places: one real strip starts at the
desk's right end, runs left to under the monitor, up and around it counterclockwise and on along the desk's left
half), which can't be guessed, so it is never set by itself: the first-run setup's What each light does traces it
(below), and Layout draws and matches it.

**Make it ready** (the first-run setup after Your lights, before Turn on; `ui/src/pages/welcome/ReadySteps.tsx`, logic
in `ready.ts`; the whole setup is described in [Access, first run and network](backend/access-and-first-run.md#first-run-setup)).
Each step can be passed by:

1. **What each light does**: the room as Layout draws it, in its live colors (else each strip in its Layout color),
   the light being chosen picked out (the others dimmed), and the monitor from `hardware screens` ("DELL U2723QE · 27
   inches (60 × 34 cm)"; the room's screen takes that size while it still has the default one, standing where it
   stood). Below it one row per strip or room light, a strip along several places being one light, described by them
   ("Monitor and desk · Behind the monitor and along the desk"; parts of one strip with different roles are a Layout
   thing), with Screen average (the default), Screen edge or Its own effect or color (the layout's default effect;
   which one, Off and the like are on Layout), and a line saying that music, an effect or a color picked on Home plays
   on all the lights. Each choice is saved to the layout at once (the devices are in it already), so the
   picture's live colors show it right away; Turn on saves again. Opening the step saves the layout only when the
   setup added devices itself (an imported layout stays as it was until the user changes something or turns the lights
   on). **Skip — use the defaults** puts back the paths as
   added (every light the screen's average).
   - **Trace your strip** (`ui/src/setup/TraceStrip.tsx`, logic in `trace.ts`): Screen edge for a strip on a WLED
     board. A front view of the monitor and the desk (or TV and stand) as Layout draws them, with the **board** on it
     (it starts below the screen's middle, on the desk's back edge; the user drags it to where the board is). The
     user draws the route by clicking: where the strip starts (the end its wire from the board goes into; the first
     click snaps to the board too), each corner or turn, and where it ends; clicks snap to the screen's corners and
     edges (and the middle of its bottom edge) and to the desk's back edge and its ends. **Undo last point**,
     **Clear**; at most 64 points. Meanwhile the board lights the strip dimly and its first LED brightly (`wled-test`
     `points` with LED 1). **Next: check the corners** makes the end nearest the board LED 1 and spreads the strip's
     LEDs evenly along the route, as Layout spreads a drawn strip's (`pathStations` and `placeLeds` in
     `layout/room.ts`); the board lights the LEDs that land on the corners and ends brightly, the rest dimly
     (`points`), and the drawing shows the same LEDs numbered, with the strip's LEDs as dots. **Are the bright LEDs on
     the corners?** **Yes, use this route**; **No, move them**: per corner Fewer and More move which LED sits on it
     (lit again with `points`), which matches that corner to its LED (`at`, LED n's middle: n − 0.5) and spreads the
     LEDs between matched corners evenly (gaps and uneven runs need no more than that); **Change the route** goes back
     to drawing. The path is the route on the front wall, every part following the screen next to it, so any route
     works; **Cancel** keeps what the strip showed. The board's state comes back afterward (`off`). Where the board is
     only decides which end is LED 1: the layout has no place for a device's position, so it isn't kept. A strip
     following the screen's edge already offers **Trace its route**. A room light (one LED) gets Screen edge at once;
     a strip on another kind of device too, with a hint to draw where it runs on Layout.
2. **Look**: Home's five Looks (Relaxed, Natural, Rich, Vivid, Intense), the same control and values, saved at once;
   then optionally **Add your game to Gaming** (the Presets page's program picker): the picked program goes into the
   default Gaming preset's programs (Screen edges on every strip, the Intense Look), which is made again when it was
   deleted (`addGameToGaming` in `ui/src/control/presets.ts`); saved with the layout. The default presets (Gaming,
   Movie, Music, Cozy) come with the first save of the layout that has strips. **Skip — use the defaults** sets Natural (on a new install; an imported one keeps its
   colors).

Without lights (Your lights skipped) the setup goes from Your lights to Look. Turn on lights saves the
layout first, unless something blocks saving it. On a new install (not imported) whose Brightness is still at 100%, it
sets Brightness to 60% (about a fifth of the light; full is far too bright in a room); Home's slider changes it.

**No lights yet.** A new install has only the test device (`file`, which writes the colors nowhere). Without expert
tools it is never shown: Home and Devices show "No lights yet" with one primary **Add your lights** until a real
device exists (`ui/src/setup/NoLights.tsx`; also after the first-run setup's "Look around first" or Skip), and its
status is never "Sending colors" in green (`control/health.ts`: "Test device: the colors go nowhere", neutral). With
expert tools on it is listed like any device. The first-run list of devices shows no addresses unless expert tools
are on.

## Set up a WLED board

UI: `ui/src/setup/SetUpBoard.tsx` with `FindStep`, `BoardWifiStep`, `PowerStep`, `FindStripsStep`, `StripTypeStep`,
`ChecksStep` (steps Find, Wi-Fi, Power, Strips, Check); the board model and its writes in `wled.ts`, the place
cards in `StripPlaces.tsx`. At most a few inputs per step; pins, color order and the like under More options.

1. **Find.** "Power the board on. If it has a USB port, you can also plug it into this PC." One primary **Search**
   (`hardware find`, after the one-time network search explanation): USB, the home network and new boards' hotspots
   at once. Before the first search one line says Windows may ask to let Backbloom see nearby Wi-Fi networks. The
   search runs only on that click (or Search again), never by itself or repeatedly: it opens USB serial ports, and
   other devices on CH340 or CP210x bridges (Adalight clones, 3D printers) may restart when their port opens. The
   boards are cards, new ones first, each with what tells it apart: "New board · Plugged into this PC · Silicon Labs
   CP210x (COM5) · ESP32" (the port's name and the chip), "New board · Its own Wi-Fi “WLED-AP” · strong signal"
   (strong from 70%, good from 40%, else "weak signal: move closer"), "WLED ‘Desk’ · On your Wi-Fi · wled-9a0b2e ·
   168 LEDs set up" (its mDNS name). A board on the network has a **Blink** button beside it (`wled-identify`: the
   board flashes). Boards in the layout already are marked and not pickable. No address is shown unless expert tools
   are on. With `wifi.blocked` "location" a callout says how to allow it (Settings → Privacy & security → Location →
   Let desktop apps access your location) or to use USB; without Wi-Fi on this PC, to use USB; ESP ports that didn't
   answer get a hint. Typing an address is only under **Can’t find it?**, after the things to check.
2. **Wi-Fi** (boards found by USB or their hotspot; a board on the network skips it). The network this PC is on,
   prefilled. With `wifi.savedPassword` a checked **Use the password saved on this PC** ("Backbloom hands it to the
   board without showing or keeping it": `connect-wifi` with `useSavedPassword: true`); unchecking it, or another
   network name, shows the password field. Connect: `hardware connect-wifi`. For a hotspot board one line says this
   PC switches to the board's Wi-Fi for about a minute, and another that Windows may open a browser page
   (msftconnecttest.com) meanwhile, since that network has no internet, which can be closed; `hardware-progress-update` events show what happens
   ("Switching this PC to the board’s Wi-Fi…", "Giving the board your Wi-Fi…", "Switching this PC back…", "Waiting
   for the board…"). Errors are shown as the backend words them (wrong password or a 5 GHz network, no saved
   password, no Wi-Fi on this PC, the hotspot couldn't be joined). Then the board flashes (`wled-identify`) and the
   next step says "Your board’s lights just flashed: that’s the one." The board is read (`wled-read`, waiting up to
   a minute while it comes onto the network).
3. **This board is set up already** (a name of its own or LEDs other than WLED's first-start 30, also for a board
   that just got its Wi-Fi: it may only have lost its network): **Add it as it is** or **Set it up again** (that
   replaces its LED settings). Add it as it is asks only "Where is it?" for each of its strips (the place cards
   below, its LED counts shown) and adds it with its outputs as the board reports them (where each starts and how
   long it is, `wled-read`): nothing is written to the board.
4. **Power supply**, once per board, required (`SupplyPicker.tsx`): **What’s printed on the power supply?**, with how
   its label reads ("INPUT 100-240V~ 50/60Hz · OUTPUT 5V ⎓ 4A"): the common sizes in one click (5 V 2 A, 5 V 4 A,
   5 V 10 A, 12 V 5 A, 24 V 5 A), Other (volts, and amps or watts), or Powered over USB (WLED's USB-safe 850 mA).
   Next writes the board's limit, 85% of the supply (`wled-write` `maxCurrent`), before any strip lights up. The
   supply's volts must fit every strip type picked later (WS2811 and WS2814 run on 12 and 24 V).
5. **Find your strips** (`FindStripsStep.tsx`; nobody knows their pins). `wled-probe-start` round 1 (the board
   restarts, about 15 s): the first 20 LEDs of every likely connection light white, dimly, the rest black. **How many
   strips light up?** ("The board lights the first LEDs of each strip on it white, dimly"): None, 1, 2, 3 or More (a
   number up to the connections tried). Then **Which connection is your strip on?** ("Looking for strip 2 of 2"),
   with the total said once in plain words: "Your board has 6 connections a strip is usually wired to (on ESP32
   boards). Backbloom lights them one at a time, white and dimly: tell it when one of your strips lights up." The user
   lights one connection at a time, never by itself (looking up at a ceiling strip takes a moment): "Connection 2 of 6
   is lit", **Light the next connection**, **Previous** and **This one lit up** (`wled-test` `color` white on one
   output, at the setup's low brightness; the others black; never `white`, which is full brightness). Connections keep
   their numbers over the whole search: stepping skips the ones found, which are listed ("Connection 1: your strip
   along the desk"), and the next strip's search starts at the next connection not found yet. This one lit up asks
   **Where is this strip?**: "Pick every place it passes", any combination, as a set (Behind the monitor or TV,
   Ceiling, Desk, Wall or shelf, Somewhere else; cards with icons; the way the strip runs between them comes from
   Trace your strip), with an Advanced **Not mounted yet? How to mount it** (tips per place); **That’s not it: keep
   looking** goes back to lighting connections; **Wrong number of strips? Count again** searches again (the count
   question with every connection lit). After the last strip: `wled-write` with one output per found strip on its pin
   (it turns every LED black first, as the outputs get shorter, so nothing of the search stays lit past a strip's
   end), waiting while the board restarts, then `wled-probe-end` `keep: true` (which only forgets the settings from
   before the search); the last LED check lights its pattern as soon as it opens. If the write fails the search is
   still on. Leaving the step before that (Back, closing, also while the search is starting) sends `wled-probe-end`
   `keep: false`, which puts the board's settings back.
   - **None** → "No strip lights up": what to check (power on, the wires on the strip's start where the arrows
     point away, connectors), **Look again**, **Try other connections** (round 2: the other safe pins), and **My
     strip has 4 wires, one marked CLK**: those can't be found by lighting them, so their data and clock pins are
     entered (with the places) and written as APA102 / SK9822.
6. **Each strip in turn** (`StripTypeStep.tsx`, then `ChecksStep.tsx`), lit white and dim while it is asked about (`color`
   white): "This is the strip along the desk and behind the monitor. It’s lit now." (**Not there?** changes its
   places.)
   - The **strip type**, required (`StripTypePicker.tsx`, the logic in `stripTypes.ts`, the facts in the presets of
     `plan.ts`, ids the backend's), nothing preselected: cards in buyers' words, each with how to recognize it and
     the names sold as the same type, the most common first and marked — WS2812B (5 V, 3 wires; also WS2812,
     WS2813, SK6812 RGB), SK6812 RGBW (5 V, each LED has a yellow-white half), WS2815 (12 V, 4 wires, one marked
     BI), WS2811 (12 V, cut marks every 3 LEDs), WS2814 RGBW (12 or 24 V, white half, 4 wires), APA102 / SK9822 (5
     V, 4 wires, one marked CLK or CI), and the addressable COB strips WS2812 COB (5 V), WS2811 COB (12 or 24 V) and
     WS2814 COB RGBW ("Continuous light, no visible dots. The listing says LEDs/m, but it lights in groups of LEDs: look
     for “IC/m” or “pixels/m”"). **Not sure?** asks three things seen on the strip (the volts printed on it; 3
     wires, or 4 with CLK/CI, or 4 with BI/BO; a yellow-white half) and picks the type; answers that fit none
     exactly pick the one driven the same way and say so. **Or paste the product name from your order**
     ("BTF-LIGHTING WS2812B 5V 60LED/m IP30") finds the chip, COB, white, volts and LEDs per meter by keywords (IC/m,
     pixels/m and segments/m before LEDs/m; for a COB strip only those, never its hundreds of LEDs/m: "BTF-LIGHTING
     FCOB WS2811 COB LED Strip 720LEDs/m DC24V" is a WS2811 COB without a count, with a note to look for IC/m). A
     chosen type shows as one line with Change; the next strips offer Same as strip 1.
   - More options: count the LEDs from the length and the LEDs per meter (a free number, 1–1000, with 30, 60, 96
     and 144 one click away: "From the product page or the label on the reel"; segments per meter for a COB strip),
     the number of LEDs if known, the data pin found by the probe, the clock pin of two-wire strips, the color order
     (the strip type's usual one until the color check sets it). For a strip behind the monitor or TV: **Not cut
     yet? Plan the cuts and power** (below); its plan fills in the LED count and type.
   - **Next: find how many LEDs it has** writes the strip (`wled-write`, waiting while it restarts). The checks,
     each lit with `wled-test` at low brightness, only after the write it depends on has returned (writes and tests
     run one after the other; a write without a restart returns once the board applied it, so it can't wipe the check
     lit after it):
     - *Last LED* ("Now we find the strip’s last LED"): the first N LEDs dim, the Nth bright; −10, Fewer, More, +10
       until the bright LED is the very last one at the end of the strip, with nothing lit after it ("Dim LEDs after
       the bright one? Press More. No bright LED at all? It’s past the end of the strip: press Fewer"). Past the length
       on the board, a longer one is written first (WLED applies it without a restart; leaving puts the confirmed
       length back). "That’s the last LED" writes the count; "The strip goes on, but stays dark" names the joint from
       the plan, if there is one.
     - *Colors*: red, then green: "What color is the strip?" The two answers give the color order (written at once);
       different colors along the strip → another type wired the same way (with or without a white LED, data only or
       data and clock as before, fitting the supply's volts; checked like the type step before it is written); nothing
       lit points to Find your strips' checklist and goes back to the last LED.
     - *Power* ("Last: the power check"): says first that the whole strip lights white at full brightness, as far as
       the supply allows, for up to 20 seconds, and that the supply may hum under the full load (normal); **Light the
       whole strip** (`white`, the only full-brightness pattern), then "Does the far end look as bright and white as the start?". The answer,
       or 20 seconds without one, puts it back to dim white (then **Light it again**); Yes → where to add a power
       feed.
7. **Into the layout.** The device (named after the board, or its strips: "Monitor and desk") and its strips are in the layout,
   each strip named and placed by its places: the path runs through each place's shape (around the screen,
   or as planned; across the ceiling's front edge; behind the desk; a line on the wall to drag into place), with a
   gap between them, every part showing the screen's average (Roles, above). The screen takes a plan's size only while no other
   strip is placed. Then the wizard goes back to Your lights (another board is "LED strips on a WLED board" there).

Back is off while a write or a wait runs, and a step that is left or closed ignores what still answers. Closing the
wizard during the checks sends `off`.

**Stopping and going on.** A click beside the wizard doesn't close it. The close button and Esc ask "Stop setting up
this board?" (Stop for now, Keep setting up). What was answered is kept in memory until Backbloom's page is closed
(never a password): Add device then offers **Continue setting up your board**, which opens the wizard at the same step
("Going on where you left off. Start over"); a board that is on the Wi-Fi already goes on after the Wi-Fi step. The
plain **LED strips on a WLED board** starts afresh and forgets it. Closing puts back what a step lit on the board
(`off`), from Find your strips' connections and the strip type step on.

## Plan the cuts and power

An optional helper for a strip behind the monitor or TV that isn't cut yet, never the entry and never required. Pure
UI logic (`ui/src/setup/plan.ts`, tested), no backend calls except the screen size; the form is `PlanStep.tsx`,
`PlanDrawing.tsx`, opened from the strip by `SetUpBoard`'s plan view ("Use this plan").

1. **Screen**: width and height from the monitor's EDID (`hardware screens`), else a diagonal and aspect ratio the
   user enters, else width and height by hand. Where the strips go: three sides (no bottom, the usual for a
   monitor on a stand) or four, and the gap at the stand when the bottom is used.
2. **Strip type**: presets with the facts the plan needs (chip, volts, LEDs per meter, LEDs per cut, current per LED
   at full white, how far one power feed reaches). Rules of thumb, documented next to the presets.
3. **Plan**: per side the length, the LED count and the cut point (rounded down to the strip's cut marks), the start
   corner and the direction of the data arrow, and the corners (connector, soldered wires or folded; arrows keep
   pointing away from the board). A drawing of the screen with the strips, cut marks and arrows, from the seat or
   from behind (where the strip is stuck on).
4. **Power**: total current at full white, the suggested power supply (next standard size with 20% headroom), where
   to add power feeds, and a warning when the strip's and the supply's volts differ.
5. **Checklist**: a printable list of cuts, connectors and wires ("Print the plan": the drawing from behind and the
   checklist on white). The form is kept on this PC (`localStorage`), so a plan made before buying parts is still
   there.

The plan's LED count and type go into the strip, and the layout draws the strip as planned (a point at each corner
matched to its LED, a gap across the stand; the matches are dropped when the last LED check changed the count).

Rules used (next to the presets in `plan.ts`): LED counts are rounded down to the cut marks (each piece's, or with folded
corners the run's end: one piece up to the stand's wire, LEDs counted along it: a side has the LEDs whose middle is on it,
so the LED at each corner is where the fold puts it); full white is about 60 mA per 5 V RGB LED (80 mA RGBW; 12 and 24 V strips per lit group); a power feed
reaches about 2.5 m at 5 V and 60 LEDs/m (less at higher densities, 5 m at 12 V, 10 m at 24 V), each feed after the
first goes one reach after the one before, moved back to a corner within a fifth of that (so no stretch is longer than
the reach), and one goes at the far end when the last stretch is longer than three quarters of it; the supply is the
next standard size with 20% headroom (above the largest size: as few supplies of one size as cover it, each powering
its own part of the strip), the board's limit 85% of the supply.

## Add lights

One **Search for lights** runs every room-light driver's discovery at once (the existing `leddevice discover`, plus
Govee), after the existing one-time network search explanation. Results are grouped by brand; each light has one
action:

| Brand | Prompt | Backend |
|---|---|---|
| Hue | "Press the button on your Hue bridge, then Connect." Then: pick an entertainment area, or Create one from the lights shown | `leddevice getProperties philipshue { host, pair: true }` → `{ username, clientkey }` (exists); `{ host, user, filter: "/clip/v2/resource/…" }` for `entertainment_configuration`, `light` and `entertainment` (the areas, the lights, which lights can stream); `{ host, user, createArea: { name, lights } }` with v2 light ids → `{ id }`, the new area's id (new) |
| Nanoleaf | "Hold the power button for 5–7 seconds until the lights flash, then Connect." | `leddevice getProperties nanoleaf { host, pair: true }` → `{ token }` (new); `{ host, token }` → its info, `panelLayout.layout.numPanels` used (exists) |
| Govee | "Turn on LAN Control for this light in the Govee app" (with where to find it) when it isn't found | new `govee` driver |
| Yeelight | "Turn on LAN Control in the Yeelight app" when it isn't found | exists |
| LIFX, WiZ | none (WiZ: "Allow local communication" hint) | exist |
| Home Assistant | address and long-lived access token, then its lights | `leddevice getProperties home_assistant { host, token }` → `{ lights: [{ id, name }] }`, `identify home_assistant { host, token, entityId }` (new) |

A driver tool that fails puts `{ error: "<what to tell the user>" }` in its `properties` (as Hue pairing does); the UI
shows it. Hue's "link button not pressed" becomes "Press the round button on top of it, then Connect within 30
seconds".

Then **Which light is which**: each light blinks (`leddevice identify`), the user names it and picks where it is in
the room (left, right, behind, ceiling…), and the lights are added to the layout as one device per brand.

`leddevice identify` parameters per driver: LIFX and WiZ the lamp entry (`{ ipAddress, macAddress, name }`), Yeelight
`{ hostname, port }`, Hue `{ host, user, lightId }`, Nanoleaf `{ host, token }` (exist); Govee `{ ipAddress }` and
Home Assistant `{ host, token, entityId }` (new).

The **govee** device type is described with the protocol below.

How they are added (`ui/src/setup/AddLights.tsx`, logic in `lights.ts`): one device per brand, per Hue bridge (its
entertainment area over API v2: `useEntertainmentAPI`, `useEntertainmentAPIV2`, `entertainmentConfigurationId`, one LED per
channel in the area's channel order, as the driver streams them; a gradient light's channels are numbered after
its name), per Nanoleaf (one strip for its panels) and per Home Assistant server; LIFX, WiZ, Govee and Yeelight
lamps go into the device's lamp list. Each light gets its own output of length 1 and its own short strip where the
user said it is (left or right of the screen, behind or above it, the ceiling, the left or right wall, behind the
seat), showing the screen's average (Roles, above). Lights in the layout already (by address; Home Assistant lights
by entity, so a known server can add new ones) are marked and can't be picked. Search again keeps what was picked; paired devices (Hue, Nanoleaf, Home
Assistant) stay even when a search misses them.
A Home Assistant server the search doesn't find can be entered by address.

On Devices, the lamp list of such a device (one output per lamp) keeps each strip with its lamp: removing a lamp
removes its output and strip and moves the lamps after it up one output with their strips; an added lamp gets an
output and a strip behind the screen (also after every lamp was removed).

First Wi-Fi setup of a new bulb stays in the maker's app (each brand has its own pairing); Backbloom takes over once
the light is on the network. Govee over Bluetooth (older models without LAN control) and Govee's cloud API are later
steps (see [immersion.md](immersion.md)).

## Protocol: `hardware`

Backbloom addition, admin sessions only (`backend/src/Backbloom.Server/Rpc/Commands/HardwareCommand.cs`). The backend
talks to the port or board the request names and nothing else; Wi-Fi passwords are sent to the board and never
stored or logged. Parameters are top-level properties of the request, as the UI's `call(command, subcommand, params)`
sends them (also accepted inside a `params` object). Failures reply `success: false` with an `error` meant for the
user as it is (the messages below). Over a WebSocket every subcommand except `screens` answers when the board is done,
without holding up the connection's other requests (joining a Wi-Fi network takes up to 30 s).

| Subcommand | Params | Reply |
|---|---|---|
| `find` | | `{ boards: [{ id, via, name, isNew, port?, portName?, firmware?, version?, chip?, deviceName?, host?, mdnsName?, mac?, ledCount?, setUp?, hotspot?, signal? }], wifi: { adapter, currentSsid, blocked, savedPassword }, usbSilent: [{ port, name }] }`: one search over every route at once (about 3–5 s), see below |
| `connect-wifi` | `{ id, ssid, password }` or `{ id, ssid, useSavedPassword: true }` (also `{ via, port \| hotspot \| host, ... }` instead of `id`) | `{ host, address, name, mac }`: the board on the home network (`host` for `wled-*`), with `hardware-progress-update` events meanwhile, see below |
| `wled-identify` | `{ host }` | `{}`: the whole board flashes white 3 times (400 ms on, 400 ms off; brightness 40 unless WLED limits every output by its version's rules), then its state goes back; for a Blink button and to confirm a board right after `connect-wifi` |
| `wled-probe-start` | `{ host, round: 1 \| 2 }` | `{ candidates: [{ output, pin }] }`: finding the strips, see below |
| `wled-probe-end` | `{ host, keep }` | `{ restarting }`: `keep: false` turns every LED black (LEDs past the board's own, shorter outputs keep their last colors), then puts the LED settings and "on at boot" (`def`) from before the probe back (the board restarts); `keep: true` only forgets them (the UI writes the found strips with `wled-write` first: with fewer or shorter outputs it turns every LED black before writing). `off` after the setup puts back the state from before the probe, turned off |
| `screens` | | `{ screens: [{ name, primary, widthMm, heightMm, device, devicePath }] }`: sizes from the monitor's EDID, null when it doesn't report them, as the desktop shows the monitor (swapped when it is turned to portrait); `device` is Windows' name for the output (`\\.\DISPLAY1`), `devicePath` the id screen capture keeps |
| `usb-scan` | | `{ boards: [{ port, name, state, firmware, version, chip, deviceName, address }], silent: [{ port, name }] }`: USB serial ports of ESP boards that answer Improv; `name` is the USB bridge's name; `state` `ready` (no Wi-Fi yet), `provisioned` (on Wi-Fi, `address` set), `authorize` (press the board's button first), `provisioning` (joining), `unavailable` (Wi-Fi off). `silent`: ESP ports that didn't answer (no WLED on them, or another program has the port) |
| `usb-networks` | `{ port }` | `{ networks: [{ ssid, signal, secure, rssi }] }` (signal in %, 2 × (RSSI + 100) as WLED shows it; strongest first, one entry per name, hidden networks left out) |
| `usb-wifi` | `{ port, ssid, password }` | `{ address, host }`: the URL the board reports (`http://192.168.1.50`) and its host part for `wled-*`; `address` null when the board joined without reporting one. Fails with "couldn’t join &lt;ssid&gt;: check the password", "The board didn’t join &lt;ssid&gt; in time: check the network name and password", "Press the button on the board, then try again" |
| `wled-read` | `{ host }` | `{ name, version, chip, mac, address, ledCount, outputs: [{ index, strip, type, pin, clockPin, length, start, colorOrder, reverse }], maxCurrent, ledCurrent, wifi: { ssid } }`; `strip` null for a WLED type without a preset (`type` is WLED's number), `clockPin` only for two-wire strips, `maxCurrent` 0 without a limit, `ledCurrent` null when not set |
| `wled-write` | `{ host, name?, outputs?: [{ strip, length, pin?, clockPin?, start?, colorOrder?, reverse? }], maxCurrent?, ledCurrent?, wifi?: { ssid, password } }` | `{ restarting }`; while restarting the UI polls `wled-read` (30 s at most). Without a restart it answers once the board has applied the new LED settings. A shorter output turns every LED black first (the LEDs past its new end would keep their colors) |
| `wled-wifi` | `{ host, ssid, password }` | `{ restarting }` (the access point fallback; same as `wled-write` with only `wifi`) |
| `wled-test` | `{ host, output, pattern: "first" \| "range" \| "color" \| "white" \| "points" \| "off", count?, color?: [r, g, b], leds?: [n, ...] }` | `{}`; `range` lights the first `count` LEDs, `points` the LEDs in `leds` (counted from 1, 1–64 of them) bright and the rest of the output dim; `off` restores the board's state from before the first test |

**`find`** runs three searches in parallel and lists each board once (`backend/src/Backbloom.Devices/Setup/BoardFinder.cs`):

- `usb`: the `usb-scan` search (Improv over USB serial; `isNew` while it isn't on Wi-Fi): `port`, `portName` (the port as
  Windows names it, "Silicon Labs CP210x USB to UART Bridge (COM5)"), and Improv's `firmware`, `version`, `chip`,
  `deviceName`; `name` is the device name, else the port's.
- `network`: mDNS `_wled._tcp` (3 s), then each board's `/json/info` (2.5 s): `name`, `mdnsName` (its mDNS host name,
  "wled-a1b2c3": WLED makes it from the MAC, so it tells two new boards apart), `mac`, `version`, `ledCount`,
  `host` (its address, with `:port` when not 80), `setUp` (a name or LED count other than WLED's defaults "WLED" and
  30; a heuristic) and `isNew` (not `setUp`). A board whose info can't be read is listed with its mDNS name only.
- `hotspot`: a Wi-Fi scan of this PC for networks named "WLED-AP" or starting with "WLED" (not the one the PC is on);
  `isNew` is true, `hotspot` the network name, `signal` its strength in percent (the nearest board is the strongest).

A board on USB that is on Wi-Fi and was found on the network too is one `network` entry with its `port` (Improv doesn't
tell the MAC, so they are matched by the address it reports). `id`s: `usb:COM3`, `network:A0:B1:C2:D3:E4:F5` (or
`network:<address>` without info), `hotspot:WLED-AP`. `wifi`: `adapter` (this PC has Wi-Fi), `currentSsid` (to prefill
the home network; null when not connected or hidden), `blocked`: `"location"` when Windows hides Wi-Fi from apps
without location access (below), else null; `savedPassword`: Windows keeps a password for `currentSsid` that this
user can read (then `connect-wifi` can take `useSavedPassword: true` instead of a typed one). A route that fails finds
nothing; the others still answer. A board found on USB and the network carries both routes' fields.

`useSavedPassword: true` reads the key of the current connection's profile (`WlanGetProfile` with
WLAN_PROFILE_GET_PLAINTEXT_KEY, what `netsh wlan show profile key=clear` shows the user without elevation) for that call
only: it goes to the board and is never returned, logged or stored. It works only for the network this PC is on
("This PC has no saved password for &lt;ssid&gt;: type it" otherwise, or without such a key).

**`connect-wifi`** puts a found board on the home network:

- `usb`: Improv (as `usb-wifi`), then the board's `/json/info` at the address it reports (name, MAC; best effort).
- `hotspot`: the PC remembers its current connection, joins the board's hotspot with a temporary Wi-Fi profile (WLED's
  default password "wled1234", then open), waits for an address from it (4.3.2.x, 15 s), reads `/json/info` at 4.3.2.1
  (the MAC), sends the Wi-Fi (`nw.ins`, as `wled-wifi`; the board restarts), and then always rejoins the PC's previous
  network (also after a failure or a cancel) and deletes the temporary profile. Then it looks for the board on the
  home network by its MAC (mDNS and `/json/info`, up to 60 s).
- `network`: already there; replies with what the search found.

`hardware-progress-update` events go to the asking connection only: `{ command: "hardware-progress-update", data:
{ tan, id, step } }` with `step` `"joining-hotspot"`, `"sending"`, `"rejoining"`, `"waiting"` (`tan` is the request's;
the `-update` suffix makes them events in the UI's client, so they don't answer the request). The UI's connection is
on loopback and survives the PC's Wi-Fi switching. Errors: "This PC has no Wi-Fi: plug the board into this PC with a
USB cable instead", "This PC couldn’t join the board’s Wi-Fi (&lt;hotspot&gt;): move closer to the board, or plug it into
this PC with a USB cable", "This PC joined the board’s Wi-Fi (&lt;hotspot&gt;) but got no address from it: try again", "The
board didn’t show up on &lt;ssid&gt;: check the Wi-Fi password, and that &lt;ssid&gt; is a 2.4 GHz network (WLED boards can’t
join 5 GHz)", "Search again: that board isn’t there any more" (an unknown `id`), and the USB and `wled-*` errors.

**Finding the strips** (`wled-probe-start`, for a board whose strips are wired but whose pins aren't known): the board's
LED settings (`hw.led`) are saved in memory (once: a second round keeps the first's), then a temporary configuration
with one WS281x output (type 22) per candidate pin is written (2000 LEDs over the candidates, 60 to 300 each: long
enough that no strip keeps colors from before), keeping the board's limit (split over the test outputs), name and
Wi-Fi; the board restarts, and once it answers again (with a lower uptime) the first 20 LEDs of every candidate light
white at brightness 40, the rest black. The UI then lights single candidates with `wled-test`
(`color` white on one `output`, at the low brightness; the other outputs go black). Candidates per chip (`WledPins`, from `info.arch`),
without the pins the board's settings give to buttons (`hw.btn`, enabled ones), IR, the relay, I2C and SPI (`hw.if`):

| Chip | Round 1 (the usual pins, at most its RMT channels) | Round 2 (the other safe pins) |
|---|---|---|
| ESP32 | 16, 2, 4, 5, 17, 18, 19, 21 | 3 (QuinLED-style boards' LED2), 13, 14, 15, 22, 23, 25, 26, 27, 32, 33 |
| ESP8266 | 2, 3 | 5, 4, 14 (WLED drives 3 outputs) |
| ESP32-S2 | 16, 18, 2, 4 | 1, 3, 5–13, 33 |
| ESP32-S3 | 16, 18, 2, 4 | 1, 5–15 |
| ESP32-C3 | 2, 10 | 4, 5 (WLED drives 2 outputs) |

Never tried: the flash and PSRAM pins, the USB pins, input-only pins, the boot straps (ESP32 0 and 12; S2/S3 0, 45, 46
and S3's 3; C3 8 and 9) and the serial console's TX. An unknown chip is treated as an ESP32. The saved settings are kept
30 minutes after the last probe or check, like a check's saved state. A board that doesn't answer within 30 s after the
restart fails with "The board at &lt;host&gt; didn’t come back after restarting: check its power, then try again".

**This PC's Wi-Fi** (`IWifi`; on Windows `WindowsWifi` over wlanapi.dll). The temporary profile is a per-user profile
named "Backbloom setup <8 hex digits>" (manual connection, so Windows never joins it by itself), created without
overwriting anything; only those profiles are deleted, the user's own are never changed or deleted. Windows 11 24H2
and newer hide the scan and the current network from apps without location access (Settings → Privacy & security →
Location): `find` then reports `blocked: "location"`, finds no hotspots and no `currentSsid`; the first time an app is
refused, Windows shows a one-time location prompt, so the UI should say before the first search that Windows may ask.
Joining and leaving a network aren't affected, but without the current network the way back relies on Windows
reconnecting to the user's network by itself once the temporary profile is gone. Tests and dev runs never scan or
switch the real Wi-Fi (the test host registers `NoWifi`, the tests use `FakeWifi`).

`strip` is a preset id (table below; `type`, WLED's number, instead of `strip` for anything else), `colorOrder` one of
`GRB`, `RGB`, `BRG`, `RBG`, `BGR`, `GBR`, currents in mA (`maxCurrent` for the whole board: 0 or 250 to 65000;
`ledCurrent` per LED: 1 to 254). The backend maps them to WLED's numbers (`hw.led.ins[].type`, `order`,
`hw.led.maxpwr`, `ledma`).

**`wled-write`** sends every output (WLED rebuilds its outputs from the whole list it gets), each based on what the
board reported for it, so the fields Backbloom doesn't set keep their values. An output's `pin` defaults to the one it
has (a new output needs one), `start` to the end of the previous output, `colorOrder` to the one it has when the strip
type stays, else the preset's, `reverse` to false. Which limit WLED enforces depends on its version: 0.14 the global
`hw.led.maxpwr` (with the global `ledma`), 0.15 only the outputs' own `hw.led.ins[].maxpwr` (the global value only
seeds an output sent without one), 0.16 and the development branch the global one when set, else the outputs'. So the
limit (`maxCurrent`, else the one the board has) is written both ways: as `hw.led.maxpwr` and split over the outputs
by their LED count (what 0.15's own settings page does), with `ledma` on every output and globally; `maxCurrent` 0
writes 0 everywhere. 0.15 doesn't limit an output whose own limit is below its share of the 100 mA WLED keeps for the
ESP (100 / outputs), so each output gets at least that share, rounded up (taken from the largest parts, so the parts
still add up to the limit; with the smallest limit, 250 mA, that holds for any practical number of outputs): 250 mA
over 490 + 10 LEDs is 200 + 50, not 245 + 5. `ws2815` uses WLED's own WS2815 current model (`ledma` 255). `wled-read`'s `maxCurrent` is the
limit the board's version enforces (`info.ver`; with an unknown version only one every version would enforce). The
board restarts (`{"rb":true}` on `/json/state`, a second after the settings so WLED has saved them) for new Wi-Fi, and
when outputs are added or removed or one gets another strip type or pin; length, color order, direction, currents and
name apply without a restart. A board whose settings are locked with a PIN fails with "The board’s settings are
locked with a PIN: unlock them in WLED first", one in realtime mode (another program streams to it) with "Another
program is sending colors to this board right now: stop it first".

Wi-Fi through `wled-write` / `wled-wifi` (`nw.ins[0]`: `ssid`, `psk`) has one limit: WLED keeps its stored password
when the one it gets is empty, so an open network can't be joined this way on a board that already has a password
saved. New boards (the case this is for) have none; otherwise the user joins the open network in WLED's own page.
Network names are sent as typed (spaces kept); only an empty one is refused.

**`wled-test`** lights the checks with WLED's individual LED control (`seg.i` of segment 0, set to cover every LED,
everything else black; the board's other segments are turned off meanwhile) at brightness 40 of 255; `white` (the power check only) uses
full brightness only when the board's version limits the tested output (on 0.15 by that output's own limit, which
another output's doesn't replace; else 40 too). `first` lights the output's
first LED white (or `color`), `range` the first `count` LEDs dim (`A0A0A0`: WLED gamma-corrects these colors, so a
darker one would look off at this brightness) and the last of them bright, `color` the whole output in `color` (red by
default), `points` the whole output dim and the LEDs in `leds` (counted from 1, at most 64) bright: where a drawn route
puts its corners. The first check saves the board's state (without its preset and playlist and without the segments' bounds,
which may have changed since: the segments keep the ones the checks gave them), `off` posts it back (segments
unfrozen, the other segments on as they were) and forgets it; a saved state is forgotten 30 minutes after the last
check too (each check keeps it another 30 minutes, as wiring between checks may take a while), and `off` without a
saved state does nothing. A board in realtime mode is refused.

**Which boards.** `wled-*` contact only boards on the local network: `host` may be any name (`wled-desk.local`,
`wled.lan`, `fritz.box`, `desk.home.arpa`, a single label) or an IP address (IPv6 with its scope, `fe80::1%12`); the
address it resolves to must be one of a private, link-local or unique-local network, or 4.3.2.1 (WLED's access point)
while this computer is on that access point (an interface address in 4.3.2.0/24; otherwise 4.3.2.1 is a public
address). Others ("&lt;host&gt; isn’t on your local network: only boards there can be set up") — loopback, multicast,
broadcast and public addresses — are refused, and so is something that isn't a host name at all ("&lt;host&gt; isn’t an
address: use the board’s IP address or its name"). A name that can't be resolved fails with "Couldn’t find &lt;host&gt; on your network", a
board that doesn't answer with "Couldn’t reach &lt;host&gt;" (no socket details).

| `strip` | WLED type | Notes |
|---|---|---|
| `ws2812b`, `ws2813`, `sk6812` | 22 (WS281x) | GRB |
| `ws2811-12v` | 22 | BRG, the usual order of 12 V WS2811 strips (one IC per 3 LEDs: the LED count is the number of ICs) |
| `ws2815` | 22 | GRB; `ledma` 255 (WLED's WS2815 model, 12 mA) |
| `sk6812-rgbw` | 30 (SK6812/WS2814 RGBW) | GRB |
| `ws2814-rgbw` | 30 | RGB |
| `ws2811-cob` | 22 | addressable COB, 12/24 V, RGB; one "LED" per controllable segment (one IC) |
| `ws2812-cob` | 22 | addressable COB, 5 V, GRB; one "LED" per segment |
| `ws2814-cob-rgbw` | 30 | addressable COB RGBW, 12/24 V, RGB; one "LED" per segment |
| `ws2805` | 32 (RGB + 2 whites) | RGB |
| `tm1814` | 31 | RGB |
| `gs8208` | 23 | RGB |
| `ucs8903`, `ucs8904` | 26, 29 (RGBW) | RGB |
| `apa106` | 27 | RGB |
| `ws2811-400khz` | 24 | RGB |
| `apa102`, `sk9822` | 51 | data and clock pin, BGR |
| `ws2801` | 50 | data and clock pin, RGB |
| `lpd8806` | 52 | data and clock pin, GRB |
| `p9813` | 53 | data and clock pin, BGR |

The color orders are starting points; the Colors check corrects them. On an addressable COB strip WLED's "LED" is one
controllable segment (one IC driving a run of many tiny LEDs), so the LED count is the number of segments and
`ledCurrent` is a segment's current at full white; the backend has no per-type current (it writes what `ledCurrent`
says, 55 mA when none is given), and a sensible default per segment is the same IC's non-COB strip's: about 55 mA
for `ws2812-cob` as `ws2812b`, and for the 12/24 V `ws2811-cob` and `ws2814-cob-rgbw` the per-group value the UI uses
for `ws2811-12v` and `ws2814-rgbw`.

`usb-scan` opens only USB serial ports of ESP boards (`SerialPortInfo.IsLikelyEsp`) that no configured device uses
(the port a serial device names, or for `auto` the one it would pick), one at a time, with DTR and RTS off (so opening
the port doesn't reset the board), at 115200 baud, and closes them again. It asks for the state and the device
information and waits 1.2 s, then once more 2.5 s (for a board that is still starting). `usb-networks` and `usb-wifi`
open only such a port too ("&lt;port&gt; is used by one of your devices", "There is no ESP board on &lt;port&gt;").

**Add lights.** Pairing and the other Add lights steps report what the user can act on in `properties.error`
(`success` stays true): not in pairing mode, a refused token, a bridge or server that can't be reached ("Couldn't
reach the Hue bridge at &lt;host&gt;", no socket details), an unknown light. Only requests the backend can't handle at all
(and `identify` failures) reply `success: false` ("getProperties failed for &lt;type&gt;: ...").
`leddevice getProperties nanoleaf { host, pair: true }` posts to `/api/v1/new` and replies
`{ properties: { token } }`, or `{ properties: { error } }` when the controller isn't in pairing mode or can't be
reached. `leddevice getProperties philipshue { host, pair: true }` replies `{ username, clientkey }` or `{ error }`.
`leddevice getProperties philipshue { host, user, createArea: { name, lights } }` creates
an entertainment area (API v2 `entertainment_configuration`, type `screen`) and replies `{ properties: { id } }`, or
`{ properties: { error } }` (a name that isn't 1 to 32 characters, no lights, a light that can't stream, the bridge's
own error description, a bridge that can't be reached);
`lights` are v2 light ids, entertainment service ids or v1 light numbers, each optionally `{ id, x, y, z }` (without
a position they are spread from left to right; the Hue app can move them). With a `filter` starting with `/clip/v2`,
`getProperties philipshue` reads that API v2 resource (`/clip/v2/resource/entertainment_configuration` for the areas,
`/clip/v2/resource/light` for the lights). Govee: `leddevice discover govee` replies
`{ devices: [{ name, ipAddress, deviceId, sku, value }] }` (one per address, and per device id when the light reports
one), `identify govee { ipAddress }` blinks the light and then puts it back as it was (asked with `devStatus` first,
answered at port 4002 like the scan; a light that doesn't answer stays off), its commands 200 ms apart as Govee
lights drop those that come faster, and the
`govee` device has `lamps: [{ name, ipAddress, deviceId, sku }]` like WiZ and LIFX (one LED per light). Home
Assistant: `leddevice getProperties home_assistant { host, token }` reads `GET /api/states` with the long-lived access
token and replies `{ properties: { lights: [{ id, name }] } }` (the `light.*` entities: `id` the entity id, `name`
its `friendly_name`, else the id; sorted by name), or `{ properties: { error } }` when the address or token is
missing, the token is refused (HTTP 401/403) or the server can't be reached. `host` is as the device's
`homeAssistantHost` takes it (`ha.local`, `192.168.1.5:8123`, `https://...`; port 8123 by default).
`leddevice identify home_assistant { host, token, entityId }` flashes the light once (`light.turn_on` with
`flash: "short"`; a light that was off stays on afterward, as Home Assistant handles the flash); failures reply
`success: false` ("identify failed for home_assistant: Home Assistant answered HTTP 401").

## Trying it without hardware

The mock backend (`?mock`, `ui/src/api/mockHardware.ts`) has a new WLED board on USB ("Silicon Labs CP210x (COM5)",
an ESP32, joins Wi-Fi at 192.168.1.77; the reference wiring: a WS2812B strip of 91 LEDs on GPIO 16 and a 720 LEDs/m
WS2811 COB strip of 77 segments on GPIO 2), a new board that only opens its hotspot "WLED-AP" (88% signal, joins at
192.168.1.78 with progress events; one strip of 60 on GPIO 16), and in the demo the reference board "Desk" on the network
(91 and 77 LEDs, in the layout, so it refuses tests while Backbloom streams to it). This PC has the Wi-Fi password
saved (not with `?mock=location`). The strip search gives each candidate pin 300 LEDs and lights the first 20; only the
wired ones are strips. A write that makes an output shorter or removes one turns every LED black first, like the
backend; ending the search with `keep` only forgets the settings from before it. `wled-test` `points` checks its LEDs like the backend. Boards restart for 4 s after a new pin or LED type; other writes answer after 0.7 s, once the board applied
them. The LED preview keeps running while the first-run setup holds the output (as the backend's does), so What each
light does shows live colors. To see the other paths: `?mock=welcome` (a new
install), `?mock=welcome-hotspot` (a new install with only the hotspot board), `?mock=existing` (the reference board on
the network, not in the layout: Add it as it is), `?mock=nousb`, `?mock=noboards` (nothing
found), `?mock=location` (Windows hides nearby Wi-Fi). Failures: the Wi-Fi password `wrongpass` (or one shorter than 8
characters), the network "Garage" (too far), the first Hue Connect (button not pressed), the Home Assistant token
`wrong`.

## Safety

- Development and tests never contact a real board: a fake WLED (HTTP on 127.0.0.1) and a fake serial stream speak
  the protocol in tests. A developer's real WLED in particular is never written to by a test or a dev run.
- `wled-write`, `wled-wifi` and `wled-test` refuse a board that a device of the layout streams to: a WLED, DDP, E1.31
  or Art-Net device that is connected or connecting (not one waiting to retry, nor other types) whose `host` names it,
  also by a name resolving to the same address (the shared resolver falls back to the last address the name had, the
  one the device connected to). The reply is "Backbloom is sending colors to this board (the device "&lt;name&gt;"): turn
  that device off first" (the user stops it, or the wizard is for new boards). A connected device whose host has no
  known address at all is assumed to be sending to it ("Backbloom may be sending colors to this board (the device
  "&lt;name&gt;" at &lt;host&gt;, which can’t be found right now)"); one still connecting to such a name sends nothing and is
  ignored.
- The Govee search binds each interface address on port 4002 and sends its query from that socket, so the answers
  come back as replies to our own query; Windows Firewall should let them in without a rule (as for the other
  searches), to be confirmed on a real network before release. The sockets exist only for the 2 s of the search
  (`MulticastProbe`; 1 s for identify's `devStatus`); an address where another program holds port 4002 is skipped.
  This relies on Windows Firewall's exemption for unicast responses to multicast (and broadcast) traffic, which lets
  such answers in for 3 s; Group Policy can turn it off (`DisableUnicastResponsesToMulticastBroadcast`), and then
  every search (mDNS, SSDP, the broadcast probes, Govee) finds nothing without an inbound rule.
- Tests use a fake multicast probe in every driver context (`Ctx.Discovery`), so no test binds port 4002 on a network
  interface; the only real multicast probe test runs on 127.0.0.1.
