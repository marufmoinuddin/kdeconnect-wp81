#!/usr/bin/env python3
"""Generate the Windows Phone 8.1 tile and logo assets.

Metro style: one flat monochrome mark, no gradients, no shadows, no text. The
mark is white on a transparent background so the system tile colour shows
through - that is what makes a tile look native rather than like a pasted
bitmap.

The mark is a phone with a connection radiating from its top-right. At 71 px and
106 px there is room for a silhouette and two arcs and nothing else, so the
detail is deliberately minimal: an earpiece slot was tried and removed, because
below ~170 px it merged with the top edge and read as noise.

Sizing and position are computed from the ink itself: the drawing is rendered on
a generous canvas, cropped to its non-transparent bounds, scaled so the ink
occupies a fixed fraction of the tile, and centred on those bounds. Geometric
centring of the drawing commands is not the same thing as optical centring of
the result, because the arcs add mass on the right.

Regenerate with:
    python3 tools/make-icons.py

Sizes are the 240-scale qualifiers the manifest expects (`*.scale-240.png`); the
unsuffixed names in Package.appxmanifest resolve to these through the resource
qualifier.
"""

import math
import os
import sys

from PIL import Image, ImageDraw

# Pillow 9.1+ moved the constants under Image.Resampling; getattr keeps both
# paths lint-clean and works on either version.
RESAMPLE = getattr(getattr(Image, "Resampling", Image), "LANCZOS", 1)

ASSETS = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "KDEConnect.WP81", "Assets")

WHITE = (255, 255, 255, 255)
SUPERSAMPLE = 4

# The splash keeps a dark bed: a fully transparent splash renders as a white
# flash on this device, which looks exactly like a crash.
SPLASH_BED = (11, 11, 11, 255)


def draw_mark(draw, cx, cy, box, colour):
    """Phone + connection arcs, drawn around cx,cy inside a box of side `box`."""
    stroke = max(1, int(round(box * 0.085)))

    pw = box * 0.44
    ph = box * 0.80
    px = cx - box * 0.17
    py = cy
    left, right = px - pw / 2, px + pw / 2
    top, bottom = py - ph / 2, py + ph / 2

    draw.rounded_rectangle([left, top, right, bottom], radius=box * 0.13,
                           outline=colour, width=stroke)

    # No home-button dot: at 106 px it collided with the bottom border and read
    # as a notch rather than a detail. Metro is subtraction.

    # Two arcs radiating from the phone's top-right. The gap between them is
    # wider than the stroke on purpose - equal spacing made the pair read as one
    # thick arc at small sizes.
    acx = right - stroke * 0.30
    acy = top + ph * 0.24
    for radius in (box * 0.21, box * 0.38):
        draw.arc([acx - radius, acy - radius, acx + radius, acy + radius],
                 start=-82, end=4, fill=colour, width=stroke)
        # Round caps: a bare arc ends in a flat butt, which looks chipped.
        for angle_deg in (-82, 4):
            rad = math.radians(angle_deg)
            ex = acx + radius * math.cos(rad)
            ey = acy + radius * math.sin(rad)
            draw.ellipse([ex - stroke / 2, ey - stroke / 2,
                          ex + stroke / 2, ey + stroke / 2], fill=colour)


def render(w, h, ink_ratio, background=None):
    """Render at w x h with the ink occupying ink_ratio of the short side."""
    scale = SUPERSAMPLE
    big_w, big_h = w * scale, h * scale

    # Generous canvas so nothing is clipped before the ink bounds are known.
    canvas = Image.new("RGBA", (big_w * 2, big_h * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    box = min(big_w, big_h) * ink_ratio
    draw_mark(draw, canvas.width / 2, canvas.height / 2, box, WHITE)

    bounds = canvas.getbbox()
    if bounds is None:                    # pragma: no cover - cannot happen
        return Image.new("RGBA", (w, h), background or (0, 0, 0, 0))

    mark = canvas.crop(bounds)

    target = min(big_w, big_h) * ink_ratio
    factor = target / float(max(mark.width, mark.height))
    if abs(factor - 1.0) > 0.001:
        mark = mark.resize((max(1, int(round(mark.width * factor))),
                            max(1, int(round(mark.height * factor)))),
                           RESAMPLE)

    out = Image.new("RGBA", (big_w, big_h), background or (0, 0, 0, 0))
    out.paste(mark, ((big_w - mark.width) // 2, (big_h - mark.height) // 2), mark)
    return out.resize((w, h), RESAMPLE)


TARGETS = [
    # name, width, height, ink ratio of the short side, background
    ("Logo.scale-240.png", 360, 360, 0.66, None),
    ("SmallLogo.scale-240.png", 106, 106, 0.84, None),
    ("Square71x71Logo.scale-240.png", 170, 170, 0.76, None),
    ("StoreLogo.scale-240.png", 120, 120, 0.80, None),
    ("WideLogo.scale-240.png", 744, 360, 0.54, None),
    ("SplashScreen.scale-240.png", 1152, 1920, 0.30, SPLASH_BED),
]


def main(argv):
    out_dir = argv[1] if len(argv) > 1 else ASSETS
    if not os.path.isdir(out_dir):
        print("no such directory: %s" % out_dir)
        return 1

    for name, w, h, ratio, bg in TARGETS:
        render(w, h, ratio, bg).save(os.path.join(out_dir, name))
        print("%-34s %dx%d" % (name, w, h))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
