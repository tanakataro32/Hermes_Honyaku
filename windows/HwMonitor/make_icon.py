#!/usr/bin/env python3
"""HwMonitor のアイコン (app.ico) を作る。python3 make_icon.py で app.ico と確認用の icon_preview.png を書き出す (Pillow が要る)。

絵: 暗い地に CPU のチップ (緑の基板・金のピン・銀のふた。翻訳の画面のメーターの CPU のアイコンと同じ配色) と、
その上を走る緑の心電図の線 (= 見張っている)。16・24px は細部をつぶれないよう、ピンを減らして線を太くする。
"""
from PIL import Image, ImageDraw

BG, EDGE_HI, EDGE_SH = (0x13, 0x1a, 0x1f), (0x4a, 0x57, 0x62), (0x07, 0x0a, 0x0d)
PCB, PCB_HI, GOLD = (0x2f, 0x7d, 0x40), (0x3d, 0x99, 0x50), (0xd4, 0xaf, 0x37)
IHS, IHS_HI, IHS_SH = (0xae, 0xb9, 0xc2), (0xe4, 0xeb, 0xef), (0x6b, 0x78, 0x83)
PULSE, PULSE_EDGE = (0x35, 0xe0, 0x5a), (0x06, 0x2a, 0x10)


def draw(size):
    s = 8  # 8 倍で描いて縮める (なめらかに)
    S = 256 * s / 256 * size  # 描く大きさ
    u = S / 256
    im = Image.new("RGBA", (int(S), int(S)), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24

    def r(x0, y0, x1, y1, c, rad=0):
        if rad:
            d.rounded_rectangle([x0 * u, y0 * u, x1 * u - 1, y1 * u - 1], radius=rad * u, fill=c)
        else:
            d.rectangle([x0 * u, y0 * u, x1 * u - 1, y1 * u - 1], fill=c)

    # 地 (角を少し丸めた暗い板に、Win98 風の明暗の縁)
    r(4, 4, 252, 252, EDGE_SH, 28)
    r(4, 4, 248, 248, EDGE_HI, 28)
    r(10, 10, 246, 246, BG, 24)

    # チップ: ピン (金) → 基板 (緑) → ふた (銀)
    c0, c1 = (64, 192) if not small else (52, 204)
    pins = (5 if size >= 48 else 3) if not small else 0
    if pins:
        span = c1 - c0
        pw = span / (pins * 2 + 1)
        for i in range(pins):
            a = c0 + pw * (1 + i * 2)
            r(a, c0 - 26, a + pw, c0, GOLD)      # 上
            r(a, c1, a + pw, c1 + 26, GOLD)      # 下
            r(c0 - 26, a, c0, a + pw, GOLD)      # 左
            r(c1, a, c1 + 26, a + pw, GOLD)      # 右
    r(c0, c0, c1, c1, PCB, 6)
    r(c0 + 6, c0 + 6, c1 - 6, c1 - 6, PCB_HI, 4)
    i0, i1 = (c0 + 22, c1 - 22) if not small else (c0 + 12, c1 - 12)  # 小さいときは銀のふたを大きく (緑の線が緑の基板に埋もれないように)
    r(i0, i0, i1, i1, IHS)
    r(i0, i0, i1, i0 + 6, IHS_HI); r(i0, i0, i0 + 6, i1, IHS_HI)
    r(i0, i1 - 6, i1, i1, IHS_SH); r(i1 - 6, i0, i1, i1, IHS_SH)

    # 心電図の線 (チップの上を左から右へ)。小さいアイコンほど太く
    pts = [(14, 150), (70, 150), (90, 118), (108, 196), (134, 52), (158, 172), (174, 132), (190, 150), (242, 150)]
    if small:
        pts = [(12, 150), (78, 150), (106, 200), (138, 48), (166, 168), (184, 150), (244, 150)]
    w = 26 if small else (18 if size <= 48 else 13)
    P = [(x * u, y * u) for x, y in pts]
    d.line(P, fill=PULSE_EDGE, width=int((w + (18 if small else 12)) * u), joint="curve")   # 暗い縁取り (銀のふたの上でも見えるように)
    d.line(P, fill=PULSE, width=int(w * u), joint="curve")
    return im.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [draw(n) for n in sizes]
    imgs[-1].save("app.ico", format="ICO", sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
    # 確認用: 各大きさを横に並べ、暗い地と明るい地の 2 段
    W = sum(sizes) + 10 * (len(sizes) + 1)
    pv = Image.new("RGB", (W, 256 * 2 + 30), (0xf0, 0xf0, 0xf0))
    ImageDraw.Draw(pv).rectangle([0, 0, W, 256 + 15], fill=(0x20, 0x20, 0x20))
    x = 10
    for n, im in zip(sizes, imgs):
        pv.paste(im, (x, 5), im)
        pv.paste(im, (x, 256 + 25), im)
        x += n + 10
    pv.save("icon_preview.png")


if __name__ == "__main__":
    main()
