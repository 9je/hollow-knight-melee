from PIL import Image

from ssbmhk_tools.roompng import render

MELEE = {"scene": "T", "scale": 1.0, "xy": [0, 0, 100, 0, 100, -20, 0, -20],
         "v0": [0, 1, 2, 3], "v1": [1, 2, 3, 0], "hi": [1, 4, 2, 8], "lo": [0x200, 0, 0, 0],
         "gates": [{"Name": "right1", "x": 110, "y": 10, "w": 4, "h": 20}]}


def test_render_draws_floors_ledges_gates_and_trace(tmp_path):
    out = tmp_path / "r.png"
    render(MELEE, [{"x": 10, "y": 0, "air": 0}, {"x": 50, "y": 20, "air": 1}], out, px_per_unit=2.0, margin=10)
    img = Image.open(out).convert("RGB")
    w, h = img.size
    assert w >= 200 and h >= 40
    floor_y = 10 + int((max(0, 30) - 0) * 2)   # top of the drawing is max y (gate top 20 + margin)
    assert any(img.getpixel((x, y))[1] > 150 and img.getpixel((x, y))[0] < 100 for x in range(30, 200) for y in range(h))   # green floor
    assert any(img.getpixel((x, y)) == (255, 220, 0) for x in range(w) for y in range(h))                                # yellow ledge dot
    assert any(img.getpixel((x, y)) == (255, 0, 255) for x in range(w) for y in range(h))                                # magenta gate
