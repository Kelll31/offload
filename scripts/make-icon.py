"""Генерация иконки Offload (src/Offload.App/Assets/app.ico + PNG).

Запуск: python scripts/make-icon.py   (нужен Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Offload.App" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
FONT_CANDIDATES = [r"C:\Windows\Fonts\consolab.ttf", r"C:\Windows\Fonts\CascadiaMono.ttf", r"C:\Windows\Fonts\courbd.ttf"]


def load_font(px: int):
    for f in FONT_CANDIDATES:
        try:
            return ImageFont.truetype(f, px)
        except OSError:
            continue
    return ImageFont.load_default()


def render(size: int) -> Image.Image:
    scale = 4  # суперсэмплинг для сглаживания
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Градиент: синий -> фиолетовый (по диагонали)
    grad = Image.new("RGBA", (s, s))
    top, bottom = (37, 99, 235), (124, 58, 237)
    px = grad.load()
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2 * (s - 1))
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)

    mask = Image.new("L", (s, s), 0)
    margin = max(1, int(s * 0.04))
    radius = int(s * 0.22)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin - 1, s - margin - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    # Глиф ">_" — хорошо читается даже в 16 px
    w = max(2, int(s * (0.11 if size <= 24 else 0.085)))
    cx, cy = s * 0.30, s * 0.47
    arm = s * 0.17
    d.line([(cx - arm * 0.55, cy - arm), (cx + arm * 0.65, cy), (cx - arm * 0.55, cy + arm)], fill="white", width=w, joint="curve")
    d.line([(s * 0.50, s * 0.68), (s * 0.78, s * 0.68)], fill="white", width=w)
    return img.resize((size, size), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    images = [render(sz) for sz in SIZES]
    images[-1].save(OUT / "app.ico", sizes=[(sz, sz) for sz in SIZES], append_images=images[:-1])
    images[-1].save(OUT / "app-256.png")
    render(64).save(OUT / "app-64.png")
    print("OK:", OUT / "app.ico")


if __name__ == "__main__":
    main()
