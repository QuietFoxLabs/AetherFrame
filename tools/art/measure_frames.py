"""Measures where every Plate Frame and Portrait Frame is cut so it fits any box, and writes
AetherFrame/Domain/Components/ArtFrameData.g.cs.

A frame is drawn as a grid (ArtFrameSlices: one ArtSlices for its columns, one for its rows). On
each axis its drawing (the opaque bounds, alpha > 16) is laid edge to edge on the box it frames,
the caps and any mid-edge ornament keep the artwork's proportions, and only the two fills stretch.
A fill must therefore look the same in every line: a stretch of plain rail. This reads the runtime
PNGs (frames are bundled byte for byte from their sources) and changes none of them, so the hosted
files and their pins stay as they are.

Per axis (x: columns; y: rows, through a transpose) a position p is plain when the whole line at p
and the line STEP px further in (premultiplied RGBA) differ by more than BIG in at most MAX_BIG
pixels, and their mean max-channel difference over the pixels either covers is at most MAX_TEX. A
fill is a run of plain positions whose silhouette also stays within SHIFT px of the run's middle
line on each border (catching tapering tips and slow bumps a neighbor test misses). The fills are
the longest such run on each side of the middle, or one run across the middle (no center piece),
cut MARGIN px inside the run: far enough that the smaller copies of the artwork a small preview
draws from (each level halves it) still sample only plain rail at the cuts.

Modes, tried in order until each fill is at least ACCEPT of the axis:
  strict    the test above;
  bridged   strict, bridging gaps of up to GAP positions that hold only specks or veins;
  textured  a woven or grained rail (stretching smears the texture a little; fine for small
            stretches, as on the Plate's own shape).

FRAME_OVERRIDES holds hand-set cuts for frames with no plain run on an axis (Celestial Sakura's,
ornamented all along), at the least busy windows. The script stops if any frame's middle (between
its caps on both axes) is not clear, since the plugin draws only the border cells.

Usage: python tools/art/measure_frames.py   (from the repository root; Pillow only)
"""
import os
import sys

from PIL import Image, ImageChops

ROOT = os.path.join("AetherFrame", "Assets", "Components")
OUT = os.path.join("AetherFrame", "Domain", "Components", "ArtFrameData.g.cs")
PIECES = ("PlateFrame", "PortraitFrame")

STEP = 6
BIG = 48
MAX_BIG = 4
MAX_TEX = 14.0
GAP = 12
GAP_BIG = 12
GAP_TEX = 20.0
TEXT_BIG = 24
TEXT_TEX = 30.0
TEXT_GAP_BIG = 16
TEXT_GAP_TEX = 40.0
SHIFT = 3
STEADY_ALPHA = 64
STEADY_BIG = 10
MARGIN = 16
ACCEPT = 0.08
PROTRUDE = 4
BOUNDS_ALPHA = 16
CLEAR_ALPHA = 8

# Hand-set cuts, in runtime pixels, for an axis with no plain run: (capLeft, centerLeft, centerRight,
# capRight) per axis, x then y. Celestial Sakura's frames carry ornaments all along every edge; these
# are the least busy windows (about 2% of the axis each). On the Plate's and the Classic portrait's
# own shapes they stretch by under a third, over a few pixels; on other shapes they lengthen visibly.
FRAME_OVERRIDES = {
    ("CelestialSakura", "PlateFrame"): {"x": (513, 552, 1120, 1159), "y": (294, 318, 526, 550)},
    ("CelestialSakura", "PortraitFrame"): {"x": (344, 369, 629, 654), "y": (415, 452, 1025, 1062)},
}


def premultiplied(img):
    return Image.frombytes("RGBA", img.size, img.convert("RGBa").tobytes())


def maxchannel(diff):
    r, g, b, a = diff.split()
    return ImageChops.lighter(ImageChops.lighter(r, g), ImageChops.lighter(b, a))


def columns(gray):
    return gray.transpose(Image.Transpose.TRANSPOSE).tobytes()


class Axis:
    """One axis of a frame: x as given, y through a transpose."""

    def __init__(self, img):
        self.W, self.H = img.size
        W, H = self.W, self.H
        p = premultiplied(img)
        self.alpha = img.getchannel("A")
        mask = self.alpha.point(lambda v: 255 if v > BOUNDS_ALPHA else 0)
        m = maxchannel(ImageChops.difference(p.crop((0, 0, W - STEP, H)), p.crop((STEP, 0, W, H))))
        big = columns(m.point(lambda v: 255 if v > BIG else 0))
        cover = columns(ImageChops.lighter(mask.crop((0, 0, W - STEP, H)), mask.crop((STEP, 0, W, H))))
        mc = columns(m)
        self.big, self.tex = [], []
        for x in range(W - STEP):
            s = slice(x * H, (x + 1) * H)
            covered = cover[s].count(255)
            self.big.append(big[s].count(255))
            self.tex.append(sum(mc[s]) / covered if covered else 0.0)
        self.strict = [b <= MAX_BIG and t <= MAX_TEX for b, t in zip(self.big, self.tex)]
        maskcols = columns(mask)
        half = H // 2
        self.near_out, self.near_in, self.far_in, self.far_out = [], [], [], []
        for x in range(W):
            col = maskcols[x * H:(x + 1) * H]
            self.near_out.append(col.find(255, 0, half))
            self.near_in.append(col.rfind(255, 0, half))
            self.far_in.append(col.find(255, half))
            self.far_out.append(col.rfind(255, half))
        self._steady = {}

    def flags(self, mode):
        if mode == "strict":
            return list(self.strict)
        if mode == "textured":
            return self.bridge([b <= TEXT_BIG and t <= TEXT_TEX for b, t in zip(self.big, self.tex)], TEXT_GAP_BIG, TEXT_GAP_TEX)
        return self.bridge(list(self.strict), GAP_BIG, GAP_TEX)

    def bridge(self, flags, gap_big, gap_tex):
        n, i = len(flags), 0
        while i < n:
            if flags[i]:
                i += 1
                continue
            j = i
            while j < n and not flags[j]:
                j += 1
            if i > 0 and j < n and j - i <= GAP and max(self.big[i:j]) <= gap_big and max(self.tex[i:j]) <= gap_tex:
                for k in range(i, j):
                    flags[k] = True
            i = j
        return flags

    def steady(self, a, b):
        """Whether every line in [a, b) keeps the silhouette of the run's middle line (within SHIFT px)."""
        if (a, b) in self._steady:
            return self._steady[(a, b)]
        mid, H, half, n = (a + b) // 2, self.H, self.H // 2, b - a
        per_line = [0] * n
        for r0, r1 in ((0, half), (half, H)):
            hh = r1 - r0
            block = self.alpha.crop((a, r0, b, r1))
            best = [10 ** 9] * n
            for dy in range(-SHIFT, SHIFT + 1):
                ref = self.alpha.crop((mid, r0 + dy, mid + 1, r1 + dy)).resize((n, hh), Image.Resampling.NEAREST)
                bc = columns(ImageChops.difference(block, ref).point(lambda v: 255 if v > STEADY_ALPHA else 0))
                for i in range(n):
                    best[i] = min(best[i], bc[i * hh:(i + 1) * hh].count(255))
            for i in range(n):
                per_line[i] += best[i]
        self._steady[(a, b)] = max(per_line) <= STEADY_BIG
        return self._steady[(a, b)]

    def runs(self, flags, lo, hi):
        found, start = [], None
        for x in range(lo, hi):
            if flags[x]:
                start = x if start is None else start
            elif start is not None:
                found.append((start, x + STEP))
                start = None
        if start is not None:
            found.append((start, hi + STEP))
        return found

    def trim(self, a, b, minimum):
        while b - a >= minimum:
            if self.steady(a, b):
                return a, b
            a += 8
            b -= 8
        return None


def cuts_from(ax, flags, lo, hi):
    middle = ax.W // 2
    minimum = max(16, int(0.03 * ax.W)) + 2 * MARGIN
    runs = ax.runs(flags, lo, hi)
    crossing = [r for r in runs if r[0] < middle - 20 and r[1] > middle + 20]
    if crossing:
        t = ax.trim(crossing[0][0], crossing[0][1], minimum)
        if t and t[0] < middle - 20 and t[1] > middle + 20:
            return [t[0] + MARGIN, middle, middle, t[1] - MARGIN]
    lefts = [ax.trim(a, min(b, middle), minimum) for a, b in runs if a < middle and min(b, middle) - a >= minimum]
    rights = [ax.trim(max(a, middle), b, minimum) for a, b in runs if b > middle and b - max(a, middle) >= minimum]
    lefts = sorted((t for t in lefts if t), key=lambda t: t[0] - t[1])
    rights = sorted((t for t in rights if t), key=lambda t: t[0] - t[1])
    if not lefts or not rights:
        return None
    (la, lb), (ra, rb) = lefts[0], rights[0]
    return [la + MARGIN, lb - MARGIN, ra + MARGIN, rb - MARGIN]


def flat_center(ax, cuts):
    """True when a center span protrudes nowhere past the rail: texture, not an ornament."""
    c0, c1, c2, c3 = cuts
    fill = list(range(c0, c1)) + list(range(c2, c3))

    def median(vals):
        v = sorted(vals)
        return v[len(v) // 2]

    rail = {k: median([getattr(ax, k)[x] for x in fill]) for k in ("near_out", "near_in", "far_in", "far_out")}
    span = range(c1, c2)
    near = [ax.near_out[x] for x in span if ax.near_out[x] >= 0]
    far = [ax.far_in[x] for x in span if ax.far_in[x] >= 0]
    protrusions = [
        rail["near_out"] - min(near) if near else 0,
        max(ax.near_in[x] for x in span) - rail["near_in"],
        max(ax.far_out[x] for x in span) - rail["far_out"],
        rail["far_in"] - min(far) if far else 0,
    ]
    return all(p <= PROTRUDE for p in protrusions)


def measure_axis(img, lo, hi):
    ax = Axis(img)
    lo, hi = max(0, lo), min(ax.W - STEP, hi)
    results = {}
    for mode in ("strict", "bridged", "textured"):
        cuts = cuts_from(ax, ax.flags(mode), lo, hi)
        if cuts:
            results[mode] = cuts

    def fills(c):
        return (c[1] - c[0], c[3] - c[2])

    def acceptable(c):
        return c is not None and min(fills(c)) >= ACCEPT * ax.W

    s, b, t = results.get("strict"), results.get("bridged"), results.get("textured")
    if acceptable(s) and not (acceptable(b) and sum(fills(b)) > 1.15 * sum(fills(s))):
        chosen, mode = s, "strict"
    elif acceptable(b):
        chosen, mode = b, "bridged"
    elif acceptable(t):
        chosen, mode = t, "textured"
    else:
        return None, None
    if chosen[1] < chosen[2] and flat_center(ax, chosen):
        merged = [chosen[0] - MARGIN, ax.W // 2, ax.W // 2, chosen[3] + MARGIN]
        span_ok = all(ax.flags("textured")[x] for x in range(chosen[1] - MARGIN, min(chosen[2] + MARGIN, ax.W - STEP)))
        if span_ok and ax.steady(merged[0], merged[3]):
            chosen = [chosen[0], ax.W // 2, ax.W // 2, chosen[3]]
    return chosen, mode


def measure(folder, piece):
    img = Image.open(os.path.join(ROOT, folder, f"{folder}_{piece}.png")).convert("RGBA")
    w, h = img.size
    alpha = img.getchannel("A")
    left, top, right, bottom = alpha.point(lambda v: 255 if v > BOUNDS_ALPHA else 0).getbbox()
    override = FRAME_OVERRIDES.get((folder, piece))
    if override:
        x, y, modes = list(override["x"]), list(override["y"]), ("hand-set", "hand-set")
    else:
        x, mx = measure_axis(img, left, right)
        y, my = measure_axis(img.transpose(Image.Transpose.TRANSPOSE), top, bottom)
        modes = (mx, my)
        if x is None or y is None:
            sys.exit(f"{folder} {piece}: no plain run on the {'x' if x is None else 'y'} axis; add FRAME_OVERRIDES")
    columns_ = (left, x[0], x[1], x[2], x[3], right)
    rows = (top, y[0], y[1], y[2], y[3], bottom)
    for name, c, n in (("columns", columns_, w), ("rows", rows, h)):
        if not (0 <= c[0] <= c[1] < c[2] <= c[3] < c[4] <= c[5] <= n):
            sys.exit(f"{folder} {piece}: {name} {c} out of order")
    # The middle, between the caps on both axes, is never drawn: it must be clear.
    middle = alpha.crop((x[0], y[0], x[3], y[3])).getextrema()[1]
    if middle > CLEAR_ALPHA:
        sys.exit(f"{folder} {piece}: the middle between the caps reaches alpha {middle}; it must be clear")
    return columns_, rows, modes


def cs_slices(c):
    return f"new ArtSlices({c[0]}, {c[1]}, {c[2]}, {c[3]}, {c[4]}, {c[5]})"


def main():
    folders = sorted(f for f in os.listdir(ROOT)
                     if all(os.path.exists(os.path.join(ROOT, f, f"{f}_{p}.png")) for p in PIECES))
    lines = []
    for folder in folders:
        entry = []
        for piece in PIECES:
            cols, rows, modes = measure(folder, piece)
            print(f"{folder:22} {piece:14} x {modes[0]:9} {cols}  y {modes[1]:9} {rows}", flush=True)
            entry.append(f"new ArtFrameSlices({cs_slices(cols)}, {cs_slices(rows)})")
        lines.append(f'            ["{folder}"] = (\n                {entry[0]},\n                {entry[1]}),')
    body = "\n".join(lines)
    text = (
        "// <auto-generated>\n"
        "// Written by tools/art/measure_frames.py from the frames' pixels: where each art set's Plate Frame\n"
        "// and Portrait Frame is cut so it fits any box (ArtFrameSlices: columns, then rows, in runtime\n"
        "// pixels). Edit the script and run it again rather than editing this file.\n"
        "// </auto-generated>\n"
        "\n"
        "using System;\n"
        "using System.Collections.Generic;\n"
        "\n"
        "namespace AetherFrame.Domain.Components;\n"
        "\n"
        "internal static class ArtFrameData\n"
        "{\n"
        "    /// <summary>Each art set's Plate Frame and Portrait Frame cuts, by the set's folder (Celestial Sakura's included).</summary>\n"
        "    internal static readonly IReadOnlyDictionary<string, (ArtFrameSlices PlateFrame, ArtFrameSlices PortraitFrame)> ByFolder =\n"
        "        new Dictionary<string, (ArtFrameSlices PlateFrame, ArtFrameSlices PortraitFrame)>(StringComparer.Ordinal)\n"
        "        {\n"
        f"{body}\n"
        "        };\n"
        "}\n"
    )
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print(f"wrote {OUT} ({len(folders)} sets)")


if __name__ == "__main__":
    main()
