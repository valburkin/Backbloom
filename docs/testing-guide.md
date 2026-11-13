# Hardware testing guide

Help test Backbloom on the hardware you own: connect a device below, use it for a while, and report what didn't work
as a [GitHub issue](https://github.com/valburkin/Backbloom/issues/new/choose). You don't need any programming
knowledge.

Backbloom is a Windows app that makes LED strips and smart lights follow what's on your screen or the music that's
playing. It sets up new LED strips by itself, and finds smart lights on your Wi-Fi. So far it has been tested on one
setup: a BTF-Lighting Wi-Fi controller (WLED) with two strips. The lists below are the other common devices and PCs
people own; each report helps show that Backbloom works with them and that its setup is easy to follow.

Plan on about 30 minutes per device, plus a few evenings of normal use (games, movies, music). Something going wrong is
a useful result, so please report everything, even small things: a confusing question in the setup counts too.

## Contents

- [Before you start](#before-you-start)
- [Devices to test](#devices-to-test)
- [How to test](#how-to-test)
  - [LED strips on a WLED controller](#led-strips-on-a-wled-controller-devices-14)
  - [Room lights](#room-lights-devices-5-6-912-home-assistant)
  - [Another kind of device](#another-kind-of-device-devices-7-8-13-zigbee2mqtt)
  - [After adding devices](#after-adding-devices)
  - [Checklist for each device](#checklist-for-each-device)
- [PC setups to cover](#pc-setups-to-cover)
- [Reporting a problem](#reporting-a-problem)

## Before you start

You need a Windows 10 or 11 PC with Wi-Fi, a 2.4 GHz Wi-Fi network for the Wi-Fi devices, and the installer from
the [latest release](https://github.com/valburkin/Backbloom/releases/latest) (`BackbloomApp-stable-Setup.exe`).

1. Run the installer. Windows may show a blue "Windows protected your PC" screen because the app isn't code-signed
   yet. Only for the file you downloaded from the release page: click **More info**, then **Run anyway**.
2. Backbloom opens and adds an icon to the system tray, next to the clock (on Windows 11 it may be hidden under the ^
   arrow). The first-run setup starts; follow it with your first device (see [How to test](#how-to-test)).
3. If you already use HyperHDR, Hyperion or another lighting app, quit it first. Two apps can't drive the same lights
   at once.
4. Windows may ask some questions while you test. Allow them, and note in your report when each one came up:
   - whether Backbloom may see nearby Wi-Fi networks (it uses this to find new LED controllers);
   - whether Backbloom may use the network (choose **Private networks** only). This can happen when you turn on Remote
     access or add a Yeelight bulb.
5. Turn on detailed logs, so your reports say more: in **Settings > General**, turn on **Show expert and
   diagnostic tools**, then in **Settings > Support** set **Log detail** to **Everything (for troubleshooting)**.

### Power safety for LED strips

- Use a power supply that matches the strip's voltage (5 V, 12 V or 24 V, printed on the strip). The wrong voltage
  destroys the strip.
- At full white, a 5 V strip draws about 3.6 A per meter at 60 LEDs per meter, and about 8.6 A per meter at 144. A 5 m
  strip can draw 18–43 A, more than most power supplies give. Backbloom asks what's printed on your power supply and
  keeps the strip within it: answer that question exactly.
- On strips longer than 2 m, connect power at both ends (the setup's power check tells you when it's needed). Never
  power a strip from the controller's USB port.
- Switch off the power before you connect or disconnect wires.

## Devices to test

Test whatever you already own; there is no need to buy anything. The Must-have group covers the setups most people
own, so reports about those help the most. Prices are approximate (USD, November 2026) and show what each setup
typically costs.

**Must-have**

| # | Device | Example to buy | What it checks | Price |
|---|---|---|---|---|
| 1 | Small Wi-Fi LED controller with WLED | Athom WLED controller (ESP8266, WLED already installed) | Setup of a new controller from the box; a slower chip and weaker Wi-Fi | $10–20 |
| 2 | 5 V LED strip, 5 m long, for controller 1 | WS2812B, 144 LEDs per meter (720 LEDs) | The most common strip type; long strips (more than 490 LEDs) | $25–40 |
| 3 | RGBW LED strip (has a separate white LED), for controller 4 | SK6812 RGBW, 5 V, 60 LEDs per meter | Colors on strips with a white channel | $20–30 |
| 4 | WLED controller from another brand, with a USB port | Gledopto ESP32 WLED controller | Setup over USB; two strips on one controller; different boards | $20–40 |
| 5 | Govee light with LAN Control | A Govee strip or light bar whose listing mentions "LAN Control" (most DreamView and Neon models) | The most popular brand of screen-sync lights | $30–80 |
| 6 | Philips Hue Bridge and a color light | Hue Bridge plus a Hue Play light bar or a color bulb | Pairing with the bridge; smooth streaming to Hue lights | $100–150 |
| 7 | USB LED controller ("Adalight") | ESP32 board with [HyperSerial](https://github.com/awawa-dev/HyperSerialESP32) firmware; connect a short piece of strip 2 or 3 to it | Strips connected over USB instead of Wi-Fi | $5–10 |
| 8 | Skydimo monitor backlight kit | Any Skydimo USB kit | The most common store-bought USB kit | $30–60 |
| | Power supplies for strips 2 and 3 | 5 V, 40 A or more for strip 2 (a smaller one works: Backbloom keeps the brightness within it); 5 V, 20 A for strip 3 | | $40–70 |

**Should-have**

| # | Device | Example to buy | What it checks | Price |
|---|---|---|---|---|
| 9 | WiZ color bulb | Philips WiZ or another WiZ-compatible color bulb | Cheap Wi-Fi bulbs | $10–15 |
| 10 | LIFX color bulb | LIFX Color A19 | A popular Wi-Fi bulb | $25–40 |
| 11 | Yeelight color bulb | Yeelight Smart LED Bulb (Color) | Bulbs with a strict command limit | $15–25 |
| 12 | Nanoleaf panels | Nanoleaf Shapes or Lines starter kit (not Nanoleaf Essentials) | Light panels and their pairing | $100–200 |
| 13 | Robobloq SyncLight kit | Any Robobloq SyncLight USB kit | Kits that plug in over USB as a "HID" device | $40–60 |

**Optional**, if you already run a smart home:

- Home Assistant with any color light in it. Costs nothing if you have it already. You'll need a long-lived access
  token from your Home Assistant profile page.
- Zigbee2MQTT: a Sonoff Zigbee USB dongle and an IKEA Zigbee color bulb (about $35). Backbloom reaches it through
  your MQTT broker, set up under **Settings > Integrations**.

**Not needed:** Govee lights without LAN Control (older Bluetooth-only models), Razer, Corsair and other RGB gaming
gear. Backbloom doesn't support them yet.

## How to test

Test the first device with the first-run setup, the others with **Add device** on the **Devices** page (under Setup in
the sidebar). Both ask the same first question: **LED strips on a WLED board**, **Room lights**, or **Another kind of
device**. Test the devices one at a time first, then several together.

### LED strips on a WLED controller (devices 1–4)

Don't set the controller up in the WLED app or its web page: Backbloom should do it all. Wire the strips to the
controller and the power supply as the kit's instructions say, then:

1. Choose **LED strips on a WLED board** and click **Search**. Try each way a controller can be found:
   - a new controller, powered on, not plugged into the PC (Backbloom finds the controller's own Wi-Fi; your PC
     switches to it for about a minute, and Windows may open a browser page meanwhile, which you can close);
   - a new controller plugged into this PC over USB (device 4);
   - a controller already on your Wi-Fi.
2. Give it your Wi-Fi, answer **What's printed on the power supply?**, and follow **Find your strips**: Backbloom
   lights the controller's connections one at a time, and you say when your strip lights up and where it runs.
3. Pick the strip type (try **Not sure?** and **Or paste the product name from your order** too), then do the three
   checks: the last LED, the colors and the power at the far end.
4. Optional, for a strip behind a monitor or TV that isn't cut yet: try **Not cut yet? Plan the cuts and power** and
   say in your report whether the plan made sense.

Also try: closing the setup halfway and choosing **Continue setting up your board**; and a controller that was set up
already (**Add it as it is**, then **Set it up again**).

### Room lights (devices 5, 6, 9–12, Home Assistant)

Set each light up in its maker's app first (that's where a new bulb joins your Wi-Fi), and check that it works there.
Then:

- **Govee and Yeelight:** turn on **LAN Control** for the light in the Govee or Yeelight app.
- **WiZ:** in the WiZ app, allow local communication.

Choose **Room lights** and click **Search for lights**. Each light should show up by itself:

- **Hue:** press the round button on the bridge, then click **Connect**. Pick an entertainment area, or let Backbloom
  create one from your lights.
- **Nanoleaf:** hold the power button for 5–7 seconds until the lights flash, then click **Connect**.
- **Home Assistant:** enter its token when asked.

Then **Which light is which**: each light blinks, and you name it and say where it is in the room.

### Another kind of device (devices 7, 8, 13, Zigbee2MQTT)

Choose **Another kind of device**, pick the device type and click **Find** (USB devices).

### After adding devices

1. On the Devices page, click **Save** at the top. Nothing is kept until you do.
2. In the first-run setup, go through **What each light does** (try **Trace your strip** for a strip around the
   monitor) and **Look**, then turn the lights on. They should start at 60% brightness.
3. Open **Layout** and check that the strips and lights are where they really are in your room. Fix them if not, then
   click **Save**.
4. Go to **Home**, turn on **Lights**, then open **Effects** and try **Screen edges**, **Screen average**, a color,
   an effect and a music effect. The preview at the top and Home's **Playing now** should name each one, and it should
   still play after you restart Backbloom.

To test the first-run setup again, quit Backbloom (tray icon > Quit), rename the `%LOCALAPPDATA%\Backbloom` folder (for
example to `Backbloom.old`) and start Backbloom again; rename it back to return to your own setup.

### Checklist for each device

Copy it for every device you test:

- [ ] Found by the search, without typing an address
- [ ] The setup's questions were clear; you never had to open the device's own web page
- [ ] Lights follow the screen in a video and a game, with no visible delay
- [ ] Colors look right: white is white, red is red, the LEDs match the edge of the screen they sit next to
- [ ] Music reacts to the beat
- [ ] No flicker, stutter or random wrong colors over 30 minutes
- [ ] When you quit Backbloom, the lights switch off (bulbs and panels may go back to how they were instead); they come
      back when you start it again
- [ ] Lights come back after the PC sleeps and wakes up
- [ ] Wi-Fi devices: lights come back within about a minute after you unplug the device for 10 seconds, and after you
      restart your Wi-Fi router
- [ ] USB devices: after you unplug the device and plug it back in, turn it off and on again in **Devices**; the lights
      should come back

After the single-device tests, connect three or more devices at once (for example two WLED controllers, a Govee light
and a Hue light) and repeat the screen and music checks.

Then try the presets: Backbloom comes with **Gaming**, **Movie**, **Music** and **Cozy**. On the **Presets** page, under
Gaming's **Used while running**, add a game you own (start it first, then **Add a running program**). Play it. Check
that Gaming is in use when the game starts and Everyday comes back when you quit it, that picking **Everyday** on Home
keeps Gaming unused until the game quits, and that each **Look** (Relaxed, Natural, Rich, Vivid, Intense) looks
different. Pick Music on Home, then an effect on **Effects**: the effect should be saved to Music (Everyday stays as it
was).

Extra checks for some devices:

- **RGBW strip (3):** set it up as **SK6812 RGBW** and check whether a white screen lights the white LEDs. Then also
  add it under **Another kind of device** as a **DDP** device with **Send a white channel** turned on (under More
  options), and say which looked better.
- **Long strip (2):** check that the far end updates as smoothly as the start, with all 720 LEDs.
- **USB devices (4, 7, 8, 13):** plug them into a different USB port, click **Find** again in the device's connection
  settings, and check that they work.

## PC setups to cover

The PC matters as much as the lights: Screen sync depends on the graphics card and the monitor. Test on whatever you
have, and say in your report which of these you tested on.

| Area | Setups to try |
|---|---|
| Graphics card | NVIDIA, AMD, Intel; a laptop with two graphics chips (Intel plus NVIDIA or AMD) |
| Screen | A normal (SDR) monitor, an HDR monitor with HDR on and off, a TV over HDMI, two or more monitors |
| Games | A full-screen game, a windowed game, a game with HDR |
| Sound for Music | Speakers, a USB headset, Bluetooth headphones |
| Network | Your normal router, a mesh Wi-Fi system, a device far from the router; a PC on a network cable (no Wi-Fi) |

## Reporting a problem

Open one [GitHub issue](https://github.com/valburkin/Backbloom/issues/new/choose) per problem (the **Hardware
report** form asks for everything below), with the log files attached. Issues are public: look through the logs before
you attach them.

To find the log files: press **Win + R**, paste `%LOCALAPPDATA%\Backbloom\logs` and press Enter. Attach the newest
`backbloom-….log` and `performance-….jsonl` files, and any `crash-….jsonl` file. Please don't change anything else in
the Backbloom folder. The logs contain your PC's name, your devices' names and their addresses on your home network,
nothing else personal; replace anything you'd rather not share.

A phone video of the lights next to the screen helps a lot with flicker, delay and wrong colors. A screenshot helps
with a confusing setup step.

Copy this into each report, and paste the system report below it (**Settings > About**, **Copy system report**):

```text
Device: (name and model, for example "Athom WLED controller, 720-LED WS2812B strip")
How I added it: (LED strips on a WLED board / Room lights / Another kind of device, and the device type)
PC: (graphics card, monitor or TV, Windows 10 or 11)
Preset: (the preset that was on, if any, and the Look)
What I did: (the steps, in order)
What I expected:
What happened instead:
How often: (every time / sometimes / once)
Time it happened: (roughly, to find it in the logs)
```
