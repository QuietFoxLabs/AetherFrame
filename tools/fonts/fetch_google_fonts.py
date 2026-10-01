"""Fetches AetherFrame's font library from Google Fonts (families.json).

For each family it reads which styles exist from Google Fonts' metadata, downloads the static
TrueType file of each style AetherFrame uses (Regular, and Bold, Italic and Bold Italic where the
family has them) from Google's CSS API, and its licence from github.com/google/fonts. It writes:

- AetherFrame/Fonts/Library/<Prefix>-<Style>.ttf
- tools/fonts/licenses/<Prefix>.txt
- tools/fonts/library.json: every family's id, name, category, faces, licence, sources and SHA-256

Run from the repository root:  python tools/fonts/fetch_google_fonts.py [metadata.json]
With no argument it downloads the metadata itself. It never deletes anything.
"""

import hashlib
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FONTS = os.path.join(ROOT, "AetherFrame", "Fonts", "Library")
LICENSES = os.path.join(ROOT, "tools", "fonts", "licenses")
MANIFEST = os.path.join(ROOT, "tools", "fonts", "library.json")

# A user agent Google's CSS API answers with plain TrueType files.
AGENT = "Wget/1.21"


def get(url):
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def family_id(name):
    return "gf-" + re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def prefix_of(name):
    return re.sub(r"[^A-Za-z0-9]+", "", name)


def styles_of(entry):
    """The styles AetherFrame uses, as (style name, italic, weight)."""
    fonts = entry["fonts"]
    uprights = sorted(int(key) for key in fonts if key.isdigit())
    italics = sorted(int(key[:-1]) for key in fonts if key.endswith("i"))
    if not uprights:
        raise ValueError(entry["family"] + " has no upright style")

    if 400 in uprights:
        styles = [("Regular", 0, 400)]
        if 700 in uprights:
            styles.append(("Bold", 0, 700))
        if 400 in italics:
            styles.append(("Italic", 1, 400))
        if 700 in italics and 700 in uprights and 400 in italics:
            styles.append(("BoldItalic", 1, 700))
        return styles

    # No 400: the weight nearest it is the family's one face.
    nearest = min(uprights, key=lambda weight: (abs(weight - 400), weight))
    return [("Regular", 0, nearest)]


def css_url(name, styles):
    pairs = sorted((italic, weight) for _, italic, weight in styles)
    family = name.replace(" ", "+")
    if any(italic for italic, _ in pairs):
        axes = ";".join(f"{italic},{weight}" for italic, weight in pairs)
        return f"https://fonts.googleapis.com/css2?family={family}:ital,wght@{axes}"

    axes = ";".join(str(weight) for _, weight in pairs)
    return f"https://fonts.googleapis.com/css2?family={family}:wght@{axes}"


def faces_from_css(css):
    faces = {}
    for block in re.findall(r"@font-face\s*\{(.*?)\}", css, re.S):
        style = re.search(r"font-style:\s*(\w+)", block).group(1)
        weight = int(re.search(r"font-weight:\s*(\d+)", block).group(1))
        url = re.search(r"src:\s*url\((https://fonts\.gstatic\.com/[^)]+\.ttf)\)", block).group(1)
        faces[(1 if style == "italic" else 0, weight)] = url
    return faces


def license_of(name):
    directory = re.sub(r"[^a-z0-9]", "", name.lower())
    for folder, file in (("ofl", "OFL.txt"), ("apache", "LICENSE.txt"), ("ufl", "UFL.txt")):
        url = f"https://raw.githubusercontent.com/google/fonts/main/{folder}/{directory}/{file}"
        try:
            return folder, url, get(url)
        except Exception:
            continue
    raise ValueError("No licence found for " + name)


def main():
    if len(sys.argv) > 1:
        with open(sys.argv[1], "rb") as source:
            metadata = json.loads(source.read().decode("utf-8"))
    else:
        metadata = json.loads(get("https://fonts.google.com/metadata/fonts").decode("utf-8"))

    entries = {entry["family"]: entry for entry in metadata["familyMetadataList"]}
    with open(os.path.join(ROOT, "tools", "fonts", "families.json"), encoding="utf-8") as source:
        families = json.load(source)["families"]

    os.makedirs(FONTS, exist_ok=True)
    os.makedirs(LICENSES, exist_ok=True)
    manifest = []
    for family in families:
        name = family["name"]
        entry = entries[name]
        styles = styles_of(entry)
        css = get(css_url(name, styles)).decode("utf-8")
        urls = faces_from_css(css)
        prefix = prefix_of(name)
        faces = []
        for style, italic, weight in styles:
            url = urls[(italic, weight)]
            data = get(url)
            if data[:4] not in (b"\x00\x01\x00\x00", b"true"):
                raise ValueError(f"{name} {style} isn't a TrueType file")
            path = os.path.join(FONTS, f"{prefix}-{style}.ttf")
            with open(path, "wb") as target:
                target.write(data)
            faces.append({"style": style, "weight": weight, "source": url, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})

        kind, license_url, license_text = license_of(name)
        with open(os.path.join(LICENSES, prefix + ".txt"), "wb") as target:
            target.write(license_text)

        manifest.append({
            "id": family_id(name),
            "name": name,
            "category": family["category"],
            "prefix": prefix,
            "license": kind,
            "licenseSource": license_url,
            "designers": entry.get("designers", []),
            "faces": faces,
        })
        print(f"{name}: {', '.join(face['style'] for face in faces)} ({kind}, {sum(face['bytes'] for face in faces) // 1024} KiB)")

    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as target:
        json.dump({"families": manifest}, target, indent=2)
        target.write("\n")

    total = sum(face["bytes"] for family in manifest for face in family["faces"])
    print(f"{len(manifest)} families, {sum(len(family['faces']) for family in manifest)} files, {total / 1048576:.1f} MiB")


if __name__ == "__main__":
    main()
