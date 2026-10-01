"""Builds AetherFrame's font library catalog from the fetched fonts (tools/fonts/library.json).

For every family it measures each face the way ImGui's builder rasterizes it (as
FontTierPolicyTests' TrueTypeFace does): every glyph the face maps in U+0001 to U+FFFE, plus the
AetherFrame Sans (PT Sans) glyphs merged in for the fallback ranges where the face has none. From
the heaviest face it fits the surface model FontTierPolicy uses (a glyph count and a scale), so
the estimate is never below any face's real surface at any ladder size.

It also measures where each face puts its capitals in the line box ImGui draws it in (hhea's
ascent to descent, the font size tall), and records how far, as a fraction of the font size, the
face must move down so that the middle of its capitals (baseline to the top of 'H') sits where
AetherFrame Sans's does in the same style. Faces whose metrics leave a tall space above the
capitals would otherwise sit high in a name box. It writes:

- AetherFrame/Domain/Rendering/FontLibrary.Families.cs (generated: never edit by hand)
- AetherFrame/Fonts/Library/THIRD-PARTY-FONT-LICENSES.txt

Run from the repository root:  python tools/fonts/build_font_catalog.py
"""

import json
import math
import os
import struct

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FONTS = os.path.join(ROOT, "AetherFrame", "Fonts")
LIBRARY = os.path.join(FONTS, "Library")
MANIFEST = os.path.join(ROOT, "tools", "fonts", "library.json")
OUTPUT = os.path.join(ROOT, "AetherFrame", "Domain", "Rendering", "FontLibrary.Families.cs")
LICENSES_OUT = os.path.join(LIBRARY, "THIRD-PARTY-FONT-LICENSES.txt")
LICENSES_IN = os.path.join(ROOT, "tools", "fonts", "licenses")

# FontTierPolicy.SizeLadder, and FontTierPolicy.FallbackGlyphRanges (inclusive pairs): the tests
# check the generated models against these same values.
LADDER = [10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 84, 96, 110, 120, 140, 160, 180, 210, 240, 280, 330, 390, 460]
FALLBACK = [(0x0020, 0x024F), (0x0370, 0x03FF), (0x0400, 0x04FF), (0x2000, 0x206F), (0x20A0, 0x20CF), (0x2100, 0x214F), (0x2190, 0x21FF)]
CATEGORIES = ["Fantasy", "Script", "Serif", "Sans", "Display", "Mono"]


def in_fallback(codepoint):
    return any(low <= codepoint <= high for low, high in FALLBACK)


class Face:
    def __init__(self, path):
        data = open(path, "rb").read()
        tables = {}
        (num_tables,) = struct.unpack(">H", data[4:6])
        for i in range(num_tables):
            record = 12 + 16 * i
            tag = data[record:record + 4].decode("ascii")
            (offset,) = struct.unpack(">I", data[record + 8:record + 12])
            tables[tag] = offset
        if "glyf" not in tables:
            raise ValueError(path + " has no glyf table")

        head = tables["head"]
        (index_to_loc,) = struct.unpack(">h", data[head + 50:head + 52])
        hhea = tables["hhea"]
        ascent, descent = struct.unpack(">hh", data[hhea + 4:hhea + 8])
        (num_glyphs,) = struct.unpack(">H", data[tables["maxp"] + 4:tables["maxp"] + 6])
        loca, glyf = tables["loca"], tables["glyf"]
        boxes = []
        for g in range(num_glyphs):
            if index_to_loc == 0:
                start = 2 * struct.unpack(">H", data[loca + 2 * g:loca + 2 * g + 2])[0]
                end = 2 * struct.unpack(">H", data[loca + 2 * g + 2:loca + 2 * g + 4])[0]
            else:
                start = struct.unpack(">I", data[loca + 4 * g:loca + 4 * g + 4])[0]
                end = struct.unpack(">I", data[loca + 4 * g + 4:loca + 4 * g + 8])[0]
            if end > start:
                o = glyf + start
                boxes.append(struct.unpack(">hhhh", data[o + 2:o + 10]))
            else:
                boxes.append(None)

        self.ascent = ascent
        self.height = ascent - descent
        self.boxes = boxes
        self.map = self._cmap(data, tables["cmap"], num_glyphs)

    @staticmethod
    def _cmap(data, cmap, num_glyphs):
        (count,) = struct.unpack(">H", data[cmap + 2:cmap + 4])
        best = None
        for i in range(count):
            platform, encoding, offset = struct.unpack(">HHI", data[cmap + 4 + 8 * i:cmap + 12 + 8 * i])
            sub = cmap + offset
            (fmt,) = struct.unpack(">H", data[sub:sub + 2])
            if (platform == 3 and encoding in (1, 10)) or platform == 0:
                rank = 2 if fmt == 12 else 1 if fmt == 4 else 0
                if rank and (best is None or rank > best[0]):
                    best = (rank, sub, fmt)
        if best is None:
            raise ValueError("no Unicode cmap")

        _, sub, fmt = best
        mapping = {}
        if fmt == 4:
            (seg_x2,) = struct.unpack(">H", data[sub + 6:sub + 8])
            segs = seg_x2 // 2
            ends_at, starts_at = sub + 14, sub + 16 + seg_x2
            deltas_at, ranges_at = sub + 16 + 2 * seg_x2, sub + 16 + 3 * seg_x2
            for s in range(segs):
                (end,) = struct.unpack(">H", data[ends_at + 2 * s:ends_at + 2 * s + 2])
                (start,) = struct.unpack(">H", data[starts_at + 2 * s:starts_at + 2 * s + 2])
                (delta,) = struct.unpack(">h", data[deltas_at + 2 * s:deltas_at + 2 * s + 2])
                (range_offset,) = struct.unpack(">H", data[ranges_at + 2 * s:ranges_at + 2 * s + 2])
                for c in range(start, end + 1):
                    if c == 0xFFFF:
                        break
                    if range_offset == 0:
                        glyph = (c + delta) & 0xFFFF
                    else:
                        at = ranges_at + 2 * s + range_offset + 2 * (c - start)
                        (glyph,) = struct.unpack(">H", data[at:at + 2])
                        if glyph:
                            glyph = (glyph + delta) & 0xFFFF
                    if glyph and glyph < num_glyphs:
                        mapping[c] = glyph
        else:
            (groups,) = struct.unpack(">I", data[sub + 12:sub + 16])
            for i in range(groups):
                start, end, first = struct.unpack(">III", data[sub + 16 + 12 * i:sub + 28 + 12 * i])
                for c in range(start, end + 1):
                    glyph = first + (c - start)
                    if glyph and glyph < num_glyphs:
                        mapping[c] = glyph
        return {c: g for c, g in mapping.items() if 1 <= c <= 0xFFFE}

    def rect_area(self, glyph, size):
        box = self.boxes[glyph]
        if box is None:
            return 1
        scale = size / self.height
        x0 = math.floor(box[0] * scale)
        y0 = math.floor(-box[3] * scale)
        x1 = math.ceil(box[2] * scale)
        y1 = math.ceil(-box[1] * scale)
        return (x1 - x0 + 1) * (y1 - y0 + 1)


def copyright_of(path):
    """The font's copyright notice (name ID 0), as its own name table records it."""
    data = open(path, "rb").read()
    (num_tables,) = struct.unpack(">H", data[4:6])
    name = None
    for i in range(num_tables):
        record = 12 + 16 * i
        if data[record:record + 4] == b"name":
            (name,) = struct.unpack(">I", data[record + 8:record + 12])
    if name is None:
        return ""
    count, strings = struct.unpack(">HH", data[name + 2:name + 6])
    found = {}
    for i in range(count):
        platform, encoding, language, name_id, length, offset = struct.unpack(">HHHHHH", data[name + 6 + 12 * i:name + 18 + 12 * i])
        if name_id != 0:
            continue
        raw = data[name + strings + offset:name + strings + offset + length]
        if platform == 3 and encoding in (1, 10):
            found.setdefault("windows", raw.decode("utf-16-be", errors="replace"))
        elif platform == 1 and encoding == 0:
            found.setdefault("mac", raw.decode("mac_roman", errors="replace"))
    return (found.get("windows") or found.get("mac") or "").strip()


def measure(face, fallback):
    """The glyphs a tier rasterizes, and its surface at each ladder size."""
    added = [c for c, _ in fallback.map.items() if in_fallback(c) and c not in face.map]
    glyphs = len(face.map) + len(added)
    surfaces = []
    for size in LADDER:
        total = sum(face.rect_area(g, size) for g in face.map.values())
        total += sum(fallback.rect_area(fallback.map[c], size) for c in added)
        surfaces.append(total)
    return glyphs, surfaces


def cap_centre(face):
    """The middle of the capitals (baseline to the top of 'H'), from the line box's top, as a fraction of its height."""
    glyph = face.map.get(ord("H"))
    if glyph is None or face.boxes[glyph] is None:
        return None
    return (face.ascent - face.boxes[glyph][3] / 2) / face.height


def shift(face, fallback):
    """How far down the face draws, as a fraction of the font size, to centre its capitals as AetherFrame Sans does."""
    centre = cap_centre(face)
    return 0.0 if centre is None else round(cap_centre(fallback) - centre, 3)


def fit(measures):
    glyphs = max(g for g, _ in measures)
    scale = 0.0
    for _, surfaces in measures:
        for size, actual in zip(LADDER, surfaces):
            scale = max(scale, (math.sqrt(actual / glyphs) - 2.0) / size)
    return glyphs, math.ceil(scale * 1000) / 1000


def main():
    with open(MANIFEST, encoding="utf-8") as source:
        families = json.load(source)["families"]

    fallbacks = {style: Face(os.path.join(FONTS, f"PTSans-{style}.ttf")) for style in ("Regular", "Bold", "Italic", "BoldItalic")}
    rows = []
    for family in families:
        styles = [face["style"] for face in family["faces"]]
        measures = []
        shifts = {}
        for style in styles:
            face = Face(os.path.join(LIBRARY, f"{family['prefix']}-{style}.ttf"))
            measures.append(measure(face, fallbacks[style]))
            shifts[style] = shift(face, fallbacks[style])
        glyphs, scale = fit(measures)
        rows.append((family, styles, glyphs, scale, shifts))
        print(f"{family['name']}: {glyphs} glyphs, scale {scale:.3f}")

    order = sorted(rows, key=lambda row: (CATEGORIES.index(row[0]["category"]), row[0]["name"].lower()))
    lines = [
        "// <auto-generated>",
        "// Generated by tools/fonts/build_font_catalog.py from tools/fonts/library.json. Don't edit by hand:",
        "// run the script again. Each family's surface model is fitted to its fonts as FontTierPolicy's are,",
        "// and each face's shift centres its capitals as AetherFrame Sans's are (FaceShifts).",
        "// </auto-generated>",
        "",
        "namespace AetherFrame.Domain.Rendering;",
        "",
        "internal static partial class FontLibrary",
        "{",
        "    /// <summary>The library's families, by category, then name.</summary>",
        "    internal static readonly LibraryFontFamily[] Families =",
        "    [",
    ]
    for family, styles, glyphs, scale, shifts in order:
        name = family["name"].replace('"', '\\"')
        faces = ", ".join(f'{shifts.get(style, 0.0):.3f}f' for style in ("Regular", "Bold", "Italic", "BoldItalic"))
        lines.append(
            f'        new("{family["id"]}", "{name}", FontCategory.{family["category"]}, "{family["prefix"]}", '
            f'{str("Bold" in styles).lower()}, {str("Italic" in styles).lower()}, {str("BoldItalic" in styles).lower()}, {glyphs}, {scale:.3f}, '
            f'new({faces})),')
    lines += ["    ];", "}", ""]
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as target:
        target.write("\n".join(lines))

    notices = [
        "AetherFrame's font library: Google Fonts families, each under its own licence below (SIL Open Font",
        "License 1.1, or Apache License 2.0). Fetched by tools/fonts/fetch_google_fonts.py; sources and",
        "checksums are in tools/fonts/library.json.",
        "",
    ]
    for family, styles, _, _, _ in order:
        notices.append("=" * 78)
        notices.append(f"{family['name']} ({', '.join(styles)}), {family['license'].upper()}: {family['licenseSource']}")
        notices.append("=" * 78)
        notice = copyright_of(os.path.join(LIBRARY, f"{family['prefix']}-Regular.ttf"))
        if notice:
            notices.append(notice)
            notices.append("")
        with open(os.path.join(LICENSES_IN, family["prefix"] + ".txt"), encoding="utf-8", errors="replace") as source:
            notices.append(source.read().replace("\r\n", "\n").strip())
        notices.append("")
    with open(LICENSES_OUT, "w", encoding="utf-8", newline="\n") as target:
        target.write("\n".join(notices))

    print(f"{len(rows)} families written to {os.path.relpath(OUTPUT, ROOT)}")


if __name__ == "__main__":
    main()
