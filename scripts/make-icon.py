"""Renders the Backup Mirror app icon (.ico + .png) from the brand mark. Usage: python make-icon.py <out_dir>"""
import os
import sys

from PIL import Image, ImageDraw

S = 1024  # supersampled canvas


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def render():
    c1, c2 = (0x22, 0xD3, 0xEE), (0x8B, 0x5C, 0xF6)
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(c1, c2, (x + y) / (2 * S))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((32, 32, S - 32, S - 32), radius=250, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img, "RGBA")
    u = S / 64

    def folder(x, y, w, h, alpha):
        col = (255, 255, 255, alpha)
        d.rounded_rectangle((x * u, (y + 3.5) * u, (x + w) * u, (y + h) * u), radius=4 * u, fill=col)
        d.rounded_rectangle((x * u, y * u, (x + w * 0.45) * u, (y + 8) * u), radius=3 * u, fill=col)

    folder(15, 18, 26, 22, 245)
    folder(27, 27, 22, 22, 150)
    d.line([(36 * u, 38 * u), (40 * u, 41 * u), (36 * u, 44 * u)], fill=(91, 75, 214, 255), width=int(2.8 * u), joint="curve")
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out, exist_ok=True)
    img = render()
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "app.png"))
    img.resize((256, 256), Image.LANCZOS).save(
        os.path.join(out, "app.ico"), sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
