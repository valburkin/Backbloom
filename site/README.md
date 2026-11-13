# Backbloom landing page

The source of the landing page at https://getbackbloom.com: how to build it, where its content lives, and how it is
deployed. The page isn't part of the app or its releases.

The page has a live demo hero (game scenes on a monitor, the glow on the wall computed from the picture's edges like
the app does), the presets, features with app screens, the device kinds Backbloom works with, the roadmap and the
download section. Backbloom is free and open source (MIT); every download button points to the latest GitHub release
(`https://github.com/valburkin/Backbloom/releases/latest`), and the GitHub links point to the repository. The site has
no forms, cookies or analytics.

It is a static site: Vite + TypeScript, no framework; [Lenis](https://github.com/darkroomengineering/lenis) (MIT) for
smooth scrolling; Inter and Space Grotesk (OFL) self-hosted.

## Contents

- [Commands](#commands)
- [Content](#content)
- [Deployment (Netlify)](#deployment-netlify)

## Commands

```powershell
cd site
npm ci
npm run dev        # http://127.0.0.1:5190
npm run build      # type check + build -> site/dist
npm run preview    # serve dist on http://127.0.0.1:5191
```

`npm run build` also runs in CI (`.github/workflows/ci.yml`), so a broken page shows up before it is deployed. A new
page needs its own entry in `build.rollupOptions.input` (`vite.config.ts`).

## Content

| What | Where | Notes |
|---|---|---|
| Copy | `index.html` | Keep claims to what the app does today; plans go in the roadmap. The download and GitHub links (nav, hero, app showcase, roadmap, download section, FAQ, finale, footer) are plain URLs in the HTML; the roadmap and FAQ link to `docs/roadmap.md` on GitHub, and the FAQ sends questions to the repository's issues. |
| Devices and roadmap | `src/data.ts` | The Gear section's cards name the device kinds Backbloom supports today, with no links and no product picks. Categories with `coming` (the title of the roadmap chapter that brings them) show as slim cards below the panels, with no links. Update the roadmap together with `docs/roadmap.md`. |
| Photos | `public/img` | Concept renders, disclosed in the footer (see [Photos](#photos)). |
| App screens | `public/screens` | Real screenshots of the UI (see [App screens](#app-screens)). |
| Link previews | `public/og.jpg` | See [Link previews](#link-previews). |
| Licenses | `public/licenses.txt` | The third-party code and fonts shipped with the page; update it with the dependencies. |

### Photos

- The photos are concept renders generated with fal.ai (FLUX 1.1 pro ultra), disclosed in the footer ("Pictures of
  rooms, people and products are AI-generated concept renders; app screens are real"). The Haptics and Wind pictures
  are from OpenAI's GPT Image 2 (on fal, `fal-ai/gpt-image-2`), which followed the brief best for realistic scenes;
  prefer it for new renders (it outputs about 1088x608, enough for the small cards).
- Replace them with photos of real setups when there are some.
- Check every render up close for brand logos (the model likes to add them to monitor bezels) and for a glow that
  doesn't match the picture on screen.
- **Before/after** (`img/ba-on.webp`, `img/ba-off.webp`): the hero render and a copy with the backlight switched off,
  made with FLUX Kontext (an image edit, so everything else stays in place). Kontext returns its own size, so the edit
  was scaled (separately across and down) and shifted to line up with the original (found by matching edges), and both
  were cropped to the shared area. Any new pair needs the same, or the halves won't line up at the handle.

### Motion and showcases

- **Reduced motion.** The hero demo has a pause button and starts paused with reduced motion, which also turns off the
  scroll-driven zoom and tilt; the showcases then show one still frame. The roadmap is a vertical timeline whose line
  fills as you scroll.
- **Music showcase** (`src/music.ts`): a simulated beat drives the room photo's light (a CSS hue/brightness filter on
  `music.webp`, whose light is all from the strips), an EQ and LEDs drawn over the preview of
  `screens/playing-music.webp` (Home's Playing now card with a music effect). The LED positions in `music.ts` and the
  overlay box in `style.css` are tied to that screenshot; when it is retaken, shoot the same card once more with every
  LED lit white and measure the strips and the preview area from it.
- **Wind and Haptics cards** (`src/wind.ts`, `src/haptics.ts`, `live` in `data.ts`): a readout under the text shows the
  game's side and what the device gets: a simulated lap's speed and the wind that follows it; simulated game events
  over an engine rumble and the bass a shaker would get. On Wind, the driver's hair flutters (an SVG turbulence
  displacement on a copy of the picture, masked to the hair) and soft wisps drift past the head; on Haptics, each
  impact lights the chair's backrest with a violet rim and glowing bands ripple out from it. The positions (chair edge,
  head, hair) are tied to these two pictures; `src/cover.ts` maps them through the object-fit crop. Their animations
  (and the music showcase's) run through `src/loop.ts`, only while on screen.

### App screens

Real screenshots of the UI at 1440x900 (2x), from a test instance, saved as WebP at 1800 px wide.

- The app hides the `file` test device (Home then says "No lights yet"), so the instance's controllers are E1.31 devices
  aimed at 127.0.0.1 (nothing listens there; the socket binds to loopback only).
- Most show the most common setup, one strip around the monitor, with `--capture synthetic`, and the default presets
  (Gaming used while `starfall.exe` runs).
- `layout.webp` shows the four strips of the "Your room, mapped" photo (two along the ceiling, the monitor and the desk
  edge) in Customize with Live colors on, from `--capture-image` with a picture whose edges carry the photo's light
  (orange to teal).
- `playing-music.webp` (music showcase, the card alone at 2x) is from a ceiling + monitor + desk setup, to match its
  photo.

### Link previews

`public/og.jpg` (1200x630) is a screenshot of `tools/og.html`; re-render it when the headline or hero photo changes.
The `og:`/`twitter:` tags need absolute URLs: `%SITE_URL%` in `index.html` is replaced at build time with Netlify's
`URL` (the site's main address), or `http://127.0.0.1:5190` locally.

## Deployment (Netlify)

The site runs on Netlify as `getbackbloom` (https://getbackbloom.com; DNS at Cloudflare, two CNAME records to
`getbackbloom.netlify.app`, proxy off; Netlify issues the certificate). It is deployed from a local build, not from
GitHub: build with the site's address so the link previews are absolute, then upload `dist`:

```powershell
cd site; $env:URL = 'https://getbackbloom.com'; npm run build; cd ..
netlify deploy --prod --no-build --dir site/dist --site getbackbloom
```

Site settings:

1. **Forms**: the site has none (the CSP's `form-action` is `'none'`), so form detection can be turned off.
2. **Team login**: the Netlify team protects new sites with a login by default; it is turned off for this site.
3. **Badge**: Netlify injects a "Powered by Netlify" badge on some sites; `netlify.toml`'s CSP allows its inline script
   by hash. If Netlify changes the script, the badge is blocked again (harmless); update the hash from the console
   error.
4. **Indexing**: the pages are public (no `noindex`); `robots.txt` and `sitemap.xml` are in `public`.
