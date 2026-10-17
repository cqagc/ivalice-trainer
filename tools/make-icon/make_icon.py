# 生成修改器图标：朱红印章「伊」，与界面左上角的印章一致。用法：python3 make_icon.py <输出.ico> <预览.png>
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

SEAL = (209, 92, 68)      # 与 Theme 的 Seal 同色系
INK = (25, 21, 16)        # 深色字，取自 Panel
FONT = "/System/Library/Fonts/Supplemental/Songti.ttc"

def render(size: int) -> Image.Image:
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    seal = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(seal)
    pad = int(s * 0.10)
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=int(s * 0.08), fill=SEAL + (255,))
    # 小尺寸不画内框，免得糊
    if size >= 32:
        inset = int(s * 0.17)
        d.rounded_rectangle([inset, inset, s - inset, s - inset], radius=int(s * 0.04),
                            outline=INK + (110,), width=max(scale, int(s * 0.018)))
    font = ImageFont.truetype(FONT, int(s * 0.56), index=1)  # Songti SC Bold
    text = "伊"
    box = d.textbbox((0, 0), text, font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((s - w) / 2 - box[0], (s - h) / 2 - box[1]), text, font=font, fill=INK + (255,))
    seal = seal.rotate(4, resample=Image.BICUBIC, center=(s / 2, s / 2))  # 与界面印章一样略微倾斜
    img.alpha_composite(seal)
    return img.resize((size, size), Image.LANCZOS)

sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [render(n) for n in sizes]
frames[-1].save(sys.argv[1], format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
preview = Image.new("RGBA", (sum(sizes) + 20 * len(sizes), 276), (20, 17, 13, 255))
x = 10
for f in frames:
    preview.alpha_composite(f, (x, 10)); x += f.width + 20
preview.save(sys.argv[2])
