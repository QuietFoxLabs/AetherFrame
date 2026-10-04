"""Builds the symbol faces AetherFrame draws symbols with when a Plate's font has none (issue #121).

Most of AetherFrame's fonts have no glyph for symbols such as U+2665 (the heart), which ImGui then
draws as "?". Noto Sans Symbols 2 and Noto Sans Symbols (SIL Open Font License 1.1, with no
Reserved Font Name) have them. This downloads both from Google Fonts' CSS API, as
fetch_google_fonts.py does, and keeps only the blocks AetherFrame falls back for
(SymbolFallback.Blocks: U+2190 to U+2BFF, Arrows to Miscellaneous Symbols and Arrows, and the
hexagrams U+4DC0 to U+4DFF) with fontTools' subsetter, which takes them from 1.4 MB to about
317 KB. Every glyph kept is unchanged. It writes:

- AetherFrame/Fonts/Symbols/NotoSansSymbols2-Regular.ttf
- AetherFrame/Fonts/Symbols/NotoSansSymbols-Regular.ttf
- tools/fonts/symbols.json: each face's source, its SHA-256, the fontTools version and the
  SHA-256 of the file written (SymbolFallbackTests checks the embedded files against it)

Needs fontTools (pip install fonttools). Run from the repository root:
    python tools/fonts/build_symbol_fonts.py [directory]
Given a directory holding the downloaded files (named as the faces below), it reads them from there
instead of downloading. The output is the same for the same sources and fontTools version.
"""

import hashlib
import json
import os
import re
import sys
import urllib.request

from fontTools import subset, version as fonttools_version

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUTPUT = os.path.join(ROOT, "AetherFrame", "Fonts", "Symbols")
MANIFEST = os.path.join(ROOT, "tools", "fonts", "symbols.json")

# SymbolFallback.Blocks, inclusive.
BLOCKS = [(0x2190, 0x2BFF), (0x4DC0, 0x4DFF)]

# In the order AetherFrame merges them: the first that has a symbol draws it.
FACES = [
    {"name": "Noto Sans Symbols 2", "file": "NotoSansSymbols2-Regular.ttf"},
    {"name": "Noto Sans Symbols", "file": "NotoSansSymbols-Regular.ttf"},
]

LICENSE = "ofl"
LICENSE_SOURCE = "https://github.com/notofonts/symbols/blob/main/OFL.txt"

# A user agent Google's CSS API answers with plain TrueType files.
AGENT = "Wget/1.21"


def get(url):
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def source_url(name):
    css = get("https://fonts.googleapis.com/css2?family=" + name.replace(" ", "+")).decode("utf-8")
    return re.search(r"src:\s*url\((https://fonts\.gstatic\.com/[^)]+\.ttf)\)", css).group(1)


def copyright_of(font):
    record = font["name"].getName(0, 3, 1, 0x409) or font["name"].getName(0, 1, 0, 0)
    return str(record).strip() if record else ""


def build(source_path, target_path):
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.name_languages = ["*"]
    options.notdef_outline = True
    font = subset.load_font(source_path, options)
    codepoints = [c for low, high in BLOCKS for c in range(low, high + 1)]
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=codepoints)
    subsetter.subset(font)
    font.recalcTimestamp = False  # the same sources make the same files
    subset.save_font(font, target_path, options)
    return copyright_of(font)


def main():
    local = sys.argv[1] if len(sys.argv) > 1 else None
    os.makedirs(OUTPUT, exist_ok=True)
    manifest = {"blocks": [[f"{low:04X}", f"{high:04X}"] for low, high in BLOCKS], "fontTools": fonttools_version, "faces": []}
    for face in FACES:
        url = source_url(face["name"])
        data = open(os.path.join(local, face["file"]), "rb").read() if local else get(url)
        if data[:4] not in (b"\x00\x01\x00\x00", b"true"):
            raise ValueError(face["name"] + " isn't a TrueType file")

        source_path = os.path.join(OUTPUT, face["file"] + ".source")
        target_path = os.path.join(OUTPUT, face["file"])
        with open(source_path, "wb") as target:
            target.write(data)
        try:
            notice = build(source_path, target_path)
        finally:
            os.remove(source_path)

        written = open(target_path, "rb").read()
        manifest["faces"].append({
            "name": face["name"],
            "file": face["file"],
            "source": url,
            "sourceBytes": len(data),
            "sourceSha256": hashlib.sha256(data).hexdigest(),
            "bytes": len(written),
            "sha256": hashlib.sha256(written).hexdigest(),
            "copyright": notice,
            "license": LICENSE,
            "licenseSource": LICENSE_SOURCE,
        })
        print(f"{face['name']}: {len(data)} bytes to {len(written)}")

    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as target:
        json.dump(manifest, target, indent=2)
        target.write("\n")


if __name__ == "__main__":
    main()
