"""Draw a room's Melee collision (from `roomlab convert`) and an optional Fox trace (from `roomlab run`) to PNG."""
import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

KIND_COLOUR = {1: (60, 200, 60), 2: (70, 120, 255), 4: (255, 150, 40), 8: (230, 50, 50)}


def render(melee, trace, out, px_per_unit=2.0, margin=40):
    xy = melee["xy"]
    pts = [(xy[2 * i], xy[2 * i + 1]) for i in range(len(xy) // 2)]
    xs = [p[0] for p in pts] + [g["x"] + s * g["w"] / 2 for g in melee.get("gates", []) for s in (-1, 1)]
    ys = [p[1] for p in pts] + [g["y"] + s * g["h"] / 2 for g in melee.get("gates", []) for s in (-1, 1)]
    if trace:
        xs += [t["x"] for t in trace]
        ys += [t["y"] for t in trace]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    w = int((x1 - x0) * px_per_unit) + 2 * margin
    h = int((y1 - y0) * px_per_unit) + 2 * margin
    img = Image.new("RGB", (max(w, 1), max(h, 1)), (20, 20, 28))
    d = ImageDraw.Draw(img)

    def p(x, y):
        return (margin + (x - x0) * px_per_unit, margin + (y1 - y) * px_per_unit)

    for i in range(len(melee["v0"])):
        a, b = pts[melee["v0"][i]], pts[melee["v1"][i]]
        lo = melee["lo"][i]
        colour = (150, 255, 150) if lo & 0x100 else KIND_COLOUR.get(melee["hi"][i] & 0xF, (200, 200, 200))
        d.line([p(*a), p(*b)], fill=colour, width=2)
        if lo & 0x200:
            for q in (a, b):
                cx, cy = p(*q)
                d.ellipse([cx - 3, cy - 3, cx + 3, cy + 3], fill=(255, 220, 0))
    for g in melee.get("gates", []):
        d.rectangle([p(g["x"] - g["w"] / 2, g["y"] + g["h"] / 2), p(g["x"] + g["w"] / 2, g["y"] - g["h"] / 2)], outline=(255, 0, 255), width=2)
    if trace:
        for t0, t1 in zip(trace, trace[1:]):
            d.line([p(t0["x"], t0["y"]), p(t1["x"], t1["y"])], fill=(0, 230, 230) if t1["air"] else (255, 255, 255), width=1)
    img.save(out)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Render a room's Melee collision (and a Fox trace) to PNG.")
    ap.add_argument("melee", type=Path)
    ap.add_argument("--trace", type=Path)
    ap.add_argument("-o", "--out", type=Path, required=True)
    ap.add_argument("--scale", type=float, default=2.0, help="pixels per Melee unit")
    a = ap.parse_args(argv)
    trace = json.loads(a.trace.read_text()) if a.trace else None
    render(json.loads(a.melee.read_text()), trace, a.out, px_per_unit=a.scale)
    return 0
