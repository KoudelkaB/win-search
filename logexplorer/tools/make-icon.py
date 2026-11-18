"""Regenerate the Log Explorer PNG and Windows icon (requires Pillow)."""

from pathlib import Path

from PIL import Image, ImageDraw


def draw_icon():
    scale = 4
    size = 256 * scale
    icon = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(icon)

    def box(*coords):
        return tuple(coord * scale for coord in coords)

    # A document with one highlighted log entry.
    outline = "#25465D"
    document = [box(33, 13), box(154, 13), box(223, 82), box(223, 242), box(33, 242)]
    draw.polygon(document, fill="#F2F9FF")
    draw.polygon([box(154, 13), box(223, 82), box(154, 82)], fill="#A3C7D9")
    draw.line([box(154, 13), box(154, 82), box(223, 82)], fill=outline, width=9 * scale, joint="curve")
    draw.polygon(document, outline=outline, width=13 * scale)

    draw.rounded_rectangle(box(68, 92, 137, 108), radius=8 * scale, fill="#3C5E76")
    draw.rounded_rectangle(box(57, 124, 199, 164), radius=10 * scale, fill="#62D9C6")
    draw.rounded_rectangle(box(74, 136, 182, 152), radius=8 * scale, fill="#124C50")
    draw.rounded_rectangle(box(68, 181, 158, 196), radius=8 * scale, fill="#3C5E76")
    return icon.resize((256, 256), Image.Resampling.LANCZOS)


if __name__ == "__main__":
    assets = Path(__file__).resolve().parents[1] / "app" / "assets"
    assets.mkdir(parents=True, exist_ok=True)
    image = draw_icon()
    image.save(assets / "logexplorer.png", optimize=True)
    image.save(assets / "logexplorer.ico", sizes=[(size, size) for size in (16, 24, 32, 48, 64, 128, 256)])
