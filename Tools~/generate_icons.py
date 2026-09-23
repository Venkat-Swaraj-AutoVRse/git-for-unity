"""Renders the Git window's stroke icons to PNG.

Icons are drawn white on transparent on a 16x16 design grid and tinted at runtime
(GitIcon sets the background tint from the element's USS `color`), so one file
serves every colour and both editor themes.

Usage:  python generate_icons.py
Output: ../Editor/UI/Modern/Icons/<name>.png (64x64, anti-aliased by supersampling)
"""
import math
import os

from PIL import Image, ImageDraw

SIZE = 64            # output pixels
SUPER = 8            # supersampling factor
STROKE = 1.4         # stroke width on the 16-unit grid
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Editor", "UI", "Modern", "Icons")


class Pen:
    def __init__(self, draw, scale):
        self.d = draw
        self.s = scale
        self.w = STROKE * scale

    def _p(self, x, y):
        return (x * self.s, y * self.s)

    def _stroke(self, pts, closed=False):
        pts = [self._p(x, y) for x, y in pts]
        if closed:
            pts = pts + [pts[0]]
        r = self.w / 2
        for a, b in zip(pts, pts[1:]):
            self.d.line([a, b], fill=255, width=int(round(self.w)))
        for x, y in pts:  # round caps and joins
            self.d.ellipse([x - r, y - r, x + r, y + r], fill=255)

    def line(self, *v):
        self._stroke(list(zip(v[0::2], v[1::2])))

    def poly(self, *v):
        self._stroke(list(zip(v[0::2], v[1::2])), closed=True)

    def rect(self, x, y, w, h):
        self.poly(x, y, x + w, y, x + w, y + h, x, y + h)

    def arc(self, cx, cy, r, start, end, steps=48):
        pts = []
        for i in range(steps + 1):
            a = math.radians(start + (end - start) * i / steps)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
        self._stroke(pts)

    def circle(self, cx, cy, r):
        self.arc(cx, cy, r, 0, 360, 72)

    def dot(self, cx, cy, r):
        x, y = self._p(cx, cy)
        rr = r * self.s
        self.d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=255)

    def curve(self, x0, y0, c1x, c1y, c2x, c2y, x1, y1, steps=32):
        pts = []
        for i in range(steps + 1):
            t = i / steps
            u = 1 - t
            pts.append((u ** 3 * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t ** 3 * x1,
                        u ** 3 * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t ** 3 * y1))
        self._stroke(pts)


def gear(d):
    d.circle(8, 8, 2.2)
    for i in range(8):
        a = i * math.pi / 4
        d.line(8 + 4.2 * math.cos(a), 8 + 4.2 * math.sin(a), 8 + 6 * math.cos(a), 8 + 6 * math.sin(a))
    d.circle(8, 8, 4.2)


# Angles in degrees: 0 = +x, increasing clockwise (y points down), matching the design grid.
ICONS = {
    "check": lambda d: d.line(3, 8.5, 6.5, 12, 13, 4.5),
    "chevron-down": lambda d: d.line(4.5, 6.5, 8, 10, 11.5, 6.5),
    "chevron-right": lambda d: d.line(6.5, 4.5, 10, 8, 6.5, 11.5),
    "chevron-left": lambda d: d.line(9.5, 4.5, 6, 8, 9.5, 11.5),
    "download": lambda d: (d.line(8, 2.5, 8, 10.5), d.line(4.5, 7.5, 8, 11, 11.5, 7.5), d.line(3, 13.5, 13, 13.5)),
    "upload": lambda d: (d.line(8, 11, 8, 3), d.line(4.5, 6, 8, 2.5, 11.5, 6), d.line(3, 13.5, 13, 13.5)),
    "sync": lambda d: (d.arc(8, 8, 5.5, 200, 340), d.line(13.2, 2.8, 13.2, 6.1, 10, 6.1),
                       d.arc(8, 8, 5.5, 20, 160), d.line(2.8, 13.2, 2.8, 9.9, 6, 9.9)),
    "lock": lambda d: (d.rect(3.5, 7, 9, 6.5), d.arc(8, 5, 2.5, 180, 360), d.line(5.5, 5, 5.5, 7), d.line(10.5, 5, 10.5, 7)),
    "unlock": lambda d: (d.rect(3.5, 7, 9, 6.5), d.arc(8, 5, 2.5, 180, 330), d.line(5.5, 5, 5.5, 7)),
    "gear": gear,
    "search": lambda d: (d.circle(7, 7, 4.5), d.line(10.5, 10.5, 13.5, 13.5)),
    "plus": lambda d: (d.line(8, 3, 8, 13), d.line(3, 8, 13, 8)),
    "more": lambda d: (d.dot(3.5, 8, 1.1), d.dot(8, 8, 1.1), d.dot(12.5, 8, 1.1)),
    "warn": lambda d: (d.poly(8, 2.2, 14.2, 13, 1.8, 13), d.line(8, 6.5, 8, 9.3), d.dot(8, 11.3, 0.8)),
    "info": lambda d: (d.circle(8, 8, 5.8), d.line(8, 7.3, 8, 11), d.dot(8, 5.1, 0.8)),
    "error": lambda d: (d.circle(8, 8, 5.8), d.line(5.8, 5.8, 10.2, 10.2), d.line(10.2, 5.8, 5.8, 10.2)),
    "offline": lambda d: (d.circle(8, 8, 5.8), d.line(3.9, 3.9, 12.1, 12.1)),
    "clock": lambda d: (d.circle(8, 8, 5.8), d.line(8, 4.8, 8, 8, 10.2, 9.4)),
    "undo": lambda d: (d.line(5.5, 3, 2.5, 6, 5.5, 9), d.line(2.5, 6, 9.5, 6), d.arc(9.5, 10, 4, 270, 450), d.line(9.5, 14, 7, 14)),
    "copy": lambda d: (d.rect(5.5, 5.5, 8, 8), d.line(10.5, 5.5, 10.5, 2.5, 2.5, 2.5, 2.5, 10.5, 5.5, 10.5)),
    "external": lambda d: (d.line(9, 2.5, 13.5, 2.5, 13.5, 7), d.line(13.5, 2.5, 7.5, 8.5),
                           d.line(11.5, 9.5, 11.5, 13.5, 2.5, 13.5, 2.5, 4.5, 6.5, 4.5)),
    "branch": lambda d: (d.circle(4.5, 3.5, 1.5), d.circle(4.5, 12.5, 1.5), d.circle(11.5, 5, 1.5),
                         d.line(4.5, 5, 4.5, 11), d.curve(11.5, 6.5, 11.5, 9.5, 4.5, 8.5, 4.5, 11)),
    "repo": lambda d: (d.rect(3.5, 2, 9, 12), d.line(3.5, 11, 12.5, 11), d.line(6, 2, 6, 11)),
    "folder": lambda d: d.poly(2, 3.5, 6.3, 3.5, 7.7, 5, 14, 5, 14, 13, 2, 13),
    "shield": lambda d: (d.line(8, 1.8, 13, 3.7, 13, 7.7), d.curve(13, 7.7, 13, 10.7, 10.8, 13, 8, 14.2),
                         d.curve(8, 14.2, 5.2, 13, 3, 10.7, 3, 7.7), d.line(3, 7.7, 3, 3.7, 8, 1.8),
                         d.line(5.8, 8, 7.4, 9.6, 10.4, 6.5)),
    "close": lambda d: (d.line(4, 4, 12, 12), d.line(12, 4, 4, 12)),
    "history": lambda d: (d.arc(8, 8, 5.5, 200, 520), d.line(2.3, 3.2, 2.8, 6.1, 5.6, 5.4), d.line(8, 5, 8, 8, 10, 9.3)),
}


def render(name, shape):
    big = SIZE * SUPER
    mask = Image.new("L", (big, big), 0)
    shape(Pen(ImageDraw.Draw(mask), big / 16.0))
    mask = mask.resize((SIZE, SIZE), Image.LANCZOS)
    icon = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    icon.putalpha(mask)
    icon.save(os.path.join(OUT, name + ".png"), optimize=True)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, shape in ICONS.items():
        render(name, shape)
    print("wrote %d icons to %s" % (len(ICONS), os.path.normpath(OUT)))
