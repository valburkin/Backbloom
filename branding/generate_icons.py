"""Render the Overglow mark and the derived product images. Geometry mirrors overglow-mark.svg (512 units).

Usage: python -I generate_icons.py

Writes:
  ./png/overglow-<size>.png, ./overglow.ico                       app icons (exe, installer)
  ./installer/header.bmp, ./installer/welcome.bmp               NSIS installer banners (150x57, 164x314)
  ../www/img/brand/*.png, ../www/*.png                            old web UI logos, wordmark and favicons
The wordmark uses the first available font of WORDMARK_FONTS (Segoe UI Semibold on Windows).
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
NAME = "Overglow"
INK = (13, 14, 26, 255)
WHITE = (255, 255, 255, 255)
WORDMARK_FONTS = [
    "C:/Windows/Fonts/seguisb.ttf",
    "/System/Library/Fonts/SFNS.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "DejaVuSans-Bold.ttf",
]
BG = (13, 14, 26, 255)
STOPS = [(0.0, (255, 60, 172)), (0.5, (139, 92, 246)), (1.0, (34, 211, 238))]
SCREEN = (116, 156, 396, 356)  # x0, y0, x1, y1 in 512 units
SCREEN_SMALL = (72, 128, 440, 384)  # bigger screen for favicon sizes
SUPERSAMPLE = 4


def gradient(size: int, box: tuple[int, int, int, int]) -> Image.Image:
    """Diagonal top-left -> bottom-right gradient through STOPS, spanning `box` (like SVG objectBoundingBox)."""
    small = Image.new("RGB", (256, 256))
    px = small.load()
    for y in range(256):
        for x in range(256):
            t = (x + y) / 510
            for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
                if t <= t1:
                    f = (t - t0) / (t1 - t0)
                    px[x, y] = tuple(round(a + (b - a) * f) for a, b in zip(c0, c1))
                    break
    img = Image.new("RGB", (size, size), STOPS[0][1])
    x0, y0, x1, y1 = box
    img.paste(small.resize((x1 - x0, y1 - y0), Image.BICUBIC), (x0, y0))
    # Extend the end colors beyond the box so blurred glow outside it stays colored.
    right = img.crop((x1 - 1, 0, x1, size)).resize((size - x1, size)) if x1 < size else None
    if right:
        img.paste(right, (x1, 0))
    bottom = img.crop((0, y1 - 1, size, y1)).resize((size, size - y1)) if y1 < size else None
    if bottom:
        img.paste(bottom, (0, y1))
    left = img.crop((x0, 0, x0 + 1, size)).resize((x0, size)) if x0 > 0 else None
    if left:
        img.paste(left, (0, 0))
    top = img.crop((0, y0, size, y0 + 1)).resize((size, y0)) if y0 > 0 else None
    if top:
        img.paste(top, (0, 0))
    return img


def render(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    k = s / 512
    small_icon = size <= 32  # no blur at favicon sizes: bigger screen, thick crisp ring
    screen = [round(v * k) for v in (SCREEN_SMALL if small_icon else SCREEN)]
    radius = round((44 if small_icon else 30) * k)
    stroke = round((56 if small_icon else 20) * k)

    tile = Image.new("L", (s, s), 0)
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, s - 1, s - 1], radius=round(112 * k), fill=255)

    img = Image.new("RGBA", (s, s), BG)
    grad = gradient(s, tuple(screen)).convert("RGBA")

    if not small_icon:
        glow = Image.new("L", (s, s), 0)
        ImageDraw.Draw(glow).rounded_rectangle(screen, radius=radius, outline=255, width=round(80 * k))
        glow = glow.filter(ImageFilter.GaussianBlur(26 * k))
        img = Image.composite(grad, img, glow)

    ring = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(ring)
    half = stroke // 2
    d.rounded_rectangle([screen[0] - half, screen[1] - half, screen[2] + half, screen[3] + half],
                        radius=radius + half, fill=255)
    inner = Image.new("L", (s, s), 0)
    ImageDraw.Draw(inner).rounded_rectangle([screen[0] + half, screen[1] + half, screen[2] - half, screen[3] - half],
                                            radius=max(radius - half, 1), fill=255)
    ring = ImageChops.subtract(ring, inner)
    img = Image.composite(grad, img, ring)
    img.paste(BG, mask=inner)

    img.putalpha(tile)
    return img.resize((size, size), Image.LANCZOS)


def font(px: int) -> ImageFont.FreeTypeFont:
    for candidate in WORDMARK_FONTS:
        try:
            return ImageFont.truetype(candidate, px)
        except OSError:
            continue
    return ImageFont.load_default(px)


def wordmark(height: int, color, pad: int = 0) -> Image.Image:
    """Mark followed by the product name, `height` px tall, transparent background.
    `color` is an RGBA tuple or "gradient" (brand gradient, readable on light and dark backgrounds)."""
    s = SUPERSAMPLE
    h = height * s
    mark = render(height).resize((h, h), Image.LANCZOS)
    f = font(round(h * 0.62))
    left, top, right, bottom = f.getbbox(NAME)
    gap = round(h * 0.22)
    w = h + gap + (right - left) + pad * s
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    img.paste(mark, (0, 0), mark)
    origin = (h + gap - left, (h - (bottom - top)) // 2 - top)
    if color == "gradient":
        mask = Image.new("L", (w, h), 0)
        ImageDraw.Draw(mask).text(origin, NAME, font=f, fill=255)
        box = (h + gap, (h - (bottom - top)) // 2, h + gap + (right - left), (h + (bottom - top)) // 2)
        img = Image.composite(gradient(max(w, h), box).convert("RGBA").crop((0, 0, w, h)), img, mask)
    else:
        ImageDraw.Draw(img).text(origin, NAME, font=f, fill=color)
    return img.resize((round(w / s), height), Image.LANCZOS)


def fit(img: Image.Image, size: tuple[int, int], background, margin: int = 0) -> Image.Image:
    """Center `img` (scaled down to fit inside `size` minus margin) on a solid background."""
    canvas = Image.new("RGBA", size, background)
    box = (size[0] - 2 * margin, size[1] - 2 * margin)
    scale = min(box[0] / img.width, box[1] / img.height, 1.0)
    scaled = img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.LANCZOS)
    canvas.paste(scaled, ((size[0] - scaled.width) // 2, (size[1] - scaled.height) // 2), scaled)
    return canvas


def installer_images() -> None:
    out = HERE / "installer"
    out.mkdir(exist_ok=True)
    # NSIS MUI header (150x57) and welcome/finish page (164x314); BMP without alpha
    fit(wordmark(40, INK), (150, 57), WHITE, margin=6).convert("RGB").save(out / "header.bmp")
    welcome = Image.new("RGBA", (164, 314), BG)
    mark = render(112)
    welcome.paste(mark, ((164 - 112) // 2, 70), mark)
    f = font(26)
    left, top, right, bottom = f.getbbox(NAME)
    ImageDraw.Draw(welcome).text(((164 - (right - left)) // 2 - left, 200 - top), NAME, font=f, fill=WHITE)
    welcome.convert("RGB").save(out / "welcome.bmp")

def web_images() -> None:
    brand = REPO / "www" / "img" / "brand"
    brand.mkdir(parents=True, exist_ok=True)
    wordmark(83, INK, pad=4).save(brand / "logo.png")          # light backgrounds
    wordmark(122, "gradient", pad=6).save(brand / "logo-sidebar.png")  # sidebar (light and dark themes)
    render(40).save(brand / "small_menu.png")                   # collapsed sidebar
    render(32).save(REPO / "www" / "favicon.png")
    render(180).save(REPO / "www" / "apple-touch-icon.png")
    render(144).save(REPO / "www" / "mstile-144x144.png")


def main() -> None:
    out = HERE / "png"
    out.mkdir(exist_ok=True)
    for size in (16, 24, 32, 48, 64, 128, 144, 180, 192, 256, 512):
        render(size).save(out / f"overglow-{size}.png")
    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [render(sz) for sz in ico_sizes]
    frames[-1].save(HERE / "overglow.ico", sizes=[(sz, sz) for sz in ico_sizes], append_images=frames[:-1])
    installer_images()
    web_images()


if __name__ == "__main__":
    main()
