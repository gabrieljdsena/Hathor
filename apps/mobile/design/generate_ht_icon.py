"""Render the Hathor launcher icon set from one geometric HT mark.

The mark is drawn as vectors (rounded rectangles) so every density stays
crisp. Run from the repo root:

    python apps/mobile/design/generate_ht_icon.py

Outputs:
  design/ht-icon-master.png                    1024 px preview/master
  app/src/main/res/mipmap-*/ic_launcher.png    legacy square icons
  app/src/main/res/mipmap-*/ic_launcher_round.png
  app/src/main/res/drawable-*/ic_launcher_foreground.png
  app/src/main/res/drawable-*/ic_launcher_monochrome.png
  app/src/main/res/drawable-nodpi/app_logo.png in-app shell mark
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / "app" / "src" / "main" / "res"
DESIGN = ROOT / "design"

# Palette shared with HathorColors (zinc-950 + orange-500).
BG = (9, 9, 11)
GLOW_LIFT = (28, 28, 35)
GLOW_WARM = (58, 26, 10)
ORANGE_TOP = (255, 196, 110)
ORANGE_BOTTOM = (249, 115, 22)
ORANGE_GLOW = (249, 115, 22)
WHITE = (255, 255, 255)

# Mark geometry, relative to cap height.
STEM = 104 / 440
COUNTER = 150 / 440
GAP = 54 / 440
T_BAR_W = 268 / 440
BAR_H = 86 / 440
RADIUS = 26 / 440
MARK_ASPECT = 2 * STEM + COUNTER + GAP + T_BAR_W  # width / height


def _vertical_gradient(size, top, bottom):
    grad = Image.new("RGB", (1, size[1]))
    px = grad.load()
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        px[0, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return grad.resize(size, Image.Resampling.BICUBIC)


def _mark_mask(height, ss=4):
    """Transparent mask of the HT monogram at `height` pixels (pre-supersample)."""
    h = height * ss
    w = round(h * MARK_ASPECT)
    mask = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(mask)

    stem = round(STEM * h)
    counter = round(COUNTER * h)
    gap = round(GAP * h)
    bar_w = round(T_BAR_W * h)
    bar_h = round(BAR_H * h)
    radius = round(RADIUS * h)

    # The H occupies the left side; the T follows after a small gap.
    h_right = 2 * stem + counter
    crossbar_top = round((h - bar_h) * 0.55)
    t_x = h_right + gap
    t_stem_x = t_x + (bar_w - stem) // 2

    # H: two stems plus crossbar.
    d.rounded_rectangle((0, 0, stem, h), radius=radius, fill=255)
    d.rounded_rectangle((stem + counter, 0, h_right, h), radius=radius, fill=255)
    d.rounded_rectangle((0, crossbar_top, h_right, crossbar_top + bar_h), radius=radius, fill=255)

    # T: top bar plus centered stem.
    d.rounded_rectangle((t_x, 0, t_x + bar_w, bar_h), radius=radius, fill=255)
    d.rounded_rectangle((t_stem_x, 0, t_stem_x + stem, h), radius=radius, fill=255)
    return mask


def _mark_layer(height, ss=4, color=None):
    mask = _mark_mask(height, ss)
    if color is None:
        layer = Image.new("RGBA", mask.size, (0, 0, 0, 0))
        grad = _vertical_gradient(mask.size, ORANGE_TOP, ORANGE_BOTTOM).convert("RGBA")
        layer.paste(grad, (0, 0), mask)
        # A soft top bevel keeps the mark from looking flat at large sizes.
        shift = max(1, round(mask.height * 0.006))
        edge = ImageChops.subtract(mask, ImageChops.offset(mask, 0, shift))
        edge = edge.filter(ImageFilter.GaussianBlur(max(0.5, shift / 2)))
        bevel = Image.new("RGBA", mask.size, (255, 229, 190, 80))
        layer = Image.alpha_composite(layer, Image.composite(bevel, Image.new("RGBA", mask.size, (0, 0, 0, 0)), edge))
    else:
        layer = Image.new("RGBA", mask.size, color + (255,))
        layer.putalpha(mask)
    return layer


def _background(size):
    bg = Image.new("RGB", (size, size), BG)

    lift = Image.new("L", (size, size), 0)
    ImageDraw.Draw(lift).ellipse(
        (-0.3 * size, -0.4 * size, 1.3 * size, 1.2 * size),
        fill=28,
    )
    lift = lift.filter(ImageFilter.GaussianBlur(size * 0.18))
    bg = Image.composite(Image.new("RGB", (size, size), GLOW_LIFT), bg, lift)

    warm = Image.new("L", (size, size), 0)
    ImageDraw.Draw(warm).ellipse(
        (0.05 * size, 0.55 * size, 0.95 * size, 1.2 * size),
        fill=44,
    )
    warm = warm.filter(ImageFilter.GaussianBlur(size * 0.16))
    bg = Image.composite(Image.new("RGB", (size, size), GLOW_WARM), bg, warm)
    return bg.convert("RGBA")


def _squircle(size, radius_ratio=0.225):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=round(size * radius_ratio), fill=255
    )
    return mask


def _circle(size):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, size - 1, size - 1), fill=255)
    return mask


def _compose(size, mark_width_ratio, shape, ss=4):
    """Render one icon at `size` pixels, supersampled by `ss`."""
    canvas = size * ss
    base = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0)) if shape == "transparent" else _background(canvas)

    mark_h = round(canvas * mark_width_ratio / MARK_ASPECT)
    mark = _mark_layer(mark_h, ss=1)
    x = (canvas - mark.width) // 2
    y = (canvas - mark.height) // 2

    # Soft orange halo behind the mark.
    glow_mask = Image.new("L", (canvas, canvas), 0)
    glow_mask.paste(mark.getchannel("A"), (x, y))
    blur = max(1, round(canvas * 0.035))
    glow_mask = glow_mask.filter(ImageFilter.GaussianBlur(blur)).point(lambda v: round(v * 0.42))
    glow = Image.new("RGBA", (canvas, canvas), ORANGE_GLOW + (255,))
    glow.putalpha(glow_mask)
    out = Image.alpha_composite(base, glow)
    out.alpha_composite(mark, (x, y))

    if shape == "squircle":
        mask = _squircle(canvas)
        # Hairline rim catches the light like a physical icon tile.
        rim = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
        ImageDraw.Draw(rim).rounded_rectangle(
            (1, 1, canvas - 2, canvas - 2),
            radius=round(canvas * 0.225),
            outline=(255, 255, 255, 18),
            width=max(1, round(canvas * 0.002)),
        )
        out = Image.alpha_composite(out, rim)
        out.putalpha(mask)
    elif shape == "circle":
        mask = _circle(canvas)
        rim = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
        ImageDraw.Draw(rim).ellipse(
            (1, 1, canvas - 2, canvas - 2),
            outline=(255, 255, 255, 18),
            width=max(1, round(canvas * 0.002)),
        )
        out = Image.alpha_composite(out, rim)
        out.putalpha(mask)
    elif shape == "transparent":
        pass

    return out.resize((size, size), Image.Resampling.LANCZOS)


def _save(img, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, "PNG", optimize=True)
    print(f"{path.relative_to(ROOT)}  {img.width}x{img.height}")


def main():
    # Master artwork (opaque squircle, 1024 px).
    master = _compose(1024, 0.70, "squircle", ss=2)
    _save(master, DESIGN / "ht-icon-master.png")

    legacy = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
    for dpi, size in legacy.items():
        _save(_compose(size, 0.70, "squircle"), RES / f"mipmap-{dpi}" / "ic_launcher.png")
        _save(_compose(size, 0.62, "circle"), RES / f"mipmap-{dpi}" / "ic_launcher_round.png")

    # Adaptive foreground: 108 dp canvas, mark inside the 66 dp safe circle.
    foreground = {"mdpi": 108, "hdpi": 162, "xhdpi": 216, "xxhdpi": 324, "xxxhdpi": 432}
    for dpi, size in foreground.items():
        _save(
            _compose(size, 0.54, "transparent"),
            RES / f"drawable-{dpi}" / "ic_launcher_foreground.png",
        )
        _save(_monochrome(size), RES / f"drawable-{dpi}" / "ic_launcher_monochrome.png")

    # In-app shell mark: transparent, fills the little 44 dp brand tile.
    _save(_compose(512, 0.90, "transparent", ss=2), RES / "drawable-nodpi" / "app_logo.png")


def _monochrome(size, ss=4):
    canvas = size * ss
    mark_h = round(canvas * 0.54 / MARK_ASPECT)
    mark = _mark_layer(mark_h, ss=1, color=WHITE)
    layer = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    layer.alpha_composite(mark, ((canvas - mark.width) // 2, (canvas - mark.height) // 2))
    return layer.resize((size, size), Image.Resampling.LANCZOS)


if __name__ == "__main__":
    main()
