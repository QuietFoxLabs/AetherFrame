#!/usr/bin/env python3
"""Makes AetherFrame's runtime copies of its art sets, and the catalog data they need.

Run it from the repository root, with Python 3.10 or later and Pillow:

    python tools/art/make_runtime_art.py "E:/AetherFrame Art"

The source folder holds one folder per set, named as FAMILIES below, each with the seven pieces the
brief (docs/art/ArtSetPrompt.md) asks for. For every set this:

1. copies the Background, Plate Frame and Portrait Frame unchanged, byte for byte, and writes the
   Name Backing, Divider, Section Header and Corner Ornament at half size, averaged exactly as
   AetherFrame builds its own half-size level (BundledArtImage.BuildLevels), so every size at or
   below half draws exactly as it would from the full-size file;
2. measures, on the full-size source, where each Name Backing, Divider and Section Header can be cut
   to stretch (ArtSlices), and halves the cuts;
3. works out each piece's size on the Plate and the style's text colors from its pixels;
4. writes AetherFrame/Domain/Components/ArtSetData.g.cs, AetherFrame/Assets/ArtSets.md, and one
   preview card per style in AetherFrame/Assets/StylePreviews.

Celestial Sakura already ships: only its Section Header is new, and its style uses its shipped pieces.
Running it again on the same sources writes the same files.
"""
import hashlib
import io
import os
import shutil
import sys
from PIL import Image, ImageDraw, ImageFont

# slug (ids), display name, folder (and file prefix), theme words (descriptions), search keywords
FAMILIES = [
    ("alchemists-workshop", "Alchemist's Workshop", "AlchemistsWorkshop", "colored glass, potion bottles, copper fittings and wax seals", ["alchemist", "potion", "glass", "copper", "emerald", "amber"]),
    ("allagan-tech", "Allagan Tech", "AllaganTech", "ancient high technology, gunmetal panels and glowing circuitry", ["allagan", "tech", "magitek", "circuit", "metal", "orange", "cyan"]),
    ("ancient-amaurot", "Ancient Amaurot", "AncientAmaurot", "art deco meets an ancient city, black stone, gold lines and stars", ["amaurot", "art deco", "black", "gold", "stars", "geometric"]),
    ("anime-pop", "Anime Pop", "AnimePop", "bright colors, bold outlines, halftone dots and stars", ["anime", "pop", "comic", "bright", "stars", "halftone"]),
    ("art-nouveau", "Art Nouveau", "ArtNouveau", "flowing lines, lilies and stained-glass colors with gold", ["art nouveau", "lily", "floral", "stained glass", "gold", "cream"]),
    ("botanical-cottage", "Botanical Cottage", "BotanicalCottage", "wildflowers, vines and pressed leaves on parchment", ["botanical", "cottage", "flowers", "vines", "green", "parchment"]),
    ("corsairs-fortune", "Corsair's Fortune", "CorsairsFortune", "carved mahogany, aged brass, rope and compass ornaments", ["corsair", "pirate", "nautical", "compass", "rope", "mahogany"]),
    ("crystarium-crystal", "Crystarium Crystal", "CrystariumCrystal", "faceted crystal, white marble and blue and violet light", ["crystarium", "crystal", "marble", "blue", "violet", "white"]),
    ("cute-kawaii", "Cute Kawaii", "CuteKawaii", "pastel hearts, stars, clouds and bows", ["kawaii", "cute", "pastel", "hearts", "pink", "clouds"]),
    ("cyberpunk-neon", "Cyberpunk Neon", "CyberpunkNeon", "neon grids and holographic panels in cyan, magenta and purple", ["cyberpunk", "neon", "hologram", "purple", "magenta", "cyan"]),
    ("dark-academia", "Dark Academia", "DarkAcademia", "leather book covers, ribbon bookmarks, quills and wax seals", ["academia", "library", "books", "scholar", "green", "brass"]),
    ("dark-fantasy", "Dark Fantasy", "DarkFantasy", "blackened iron, thorns, ravens and blood-red gems", ["dark", "gothic", "thorns", "raven", "red", "iron"]),
    ("desert-oasis", "Desert Oasis", "DesertOasis", "warm sandstone, turquoise tile and pierced brass", ["desert", "oasis", "sandstone", "turquoise", "tile", "teal"]),
    ("embroidered-tapestry", "Embroidered Tapestry", "EmbroideredTapestry", "woven fabric, raised embroidery, braids and tassels", ["embroidery", "tapestry", "fabric", "tassel", "burgundy", "indigo"]),
    ("enchanted-toybox", "Enchanted Toybox", "EnchantedToybox", "painted wooden toys, puzzle pieces, beads and stars", ["toybox", "toys", "wooden", "puzzle", "playful", "stars"]),
    ("frontier-silver", "Frontier Silver", "FrontierSilver", "tooled leather, silver conchos and turquoise stones", ["frontier", "western", "leather", "silver", "turquoise", "cowboy"]),
    ("high-fantasy-royal", "High Fantasy Royal", "HighFantasyRoyal", "ornate gold, marble, royal blue velvet and gemstones", ["royal", "gold", "marble", "velvet", "blue", "lion"]),
    ("industrial-salvage", "Industrial Salvage", "IndustrialSalvage", "painted steel, bolts, vent grilles and hazard stripes", ["industrial", "salvage", "steel", "rust", "hazard", "bolts"]),
    ("ishgardian-gothic", "Ishgardian Gothic", "IshgardianGothic", "cathedral stone, silver filigree and deep blue stained glass", ["ishgard", "gothic", "cathedral", "silver", "blue", "frost"]),
    ("liquid-chrome", "Liquid Chrome", "LiquidChrome", "fluid silver forms and glossy iridescent accents", ["chrome", "liquid", "silver", "metallic", "iridescent", "y2k"]),
    ("memphis-playground", "Memphis Playground", "MemphisPlayground", "triangles, circles, squiggles and terrazzo flecks", ["memphis", "geometric", "eighties", "squiggle", "terrazzo", "playful"]),
    ("minimalist-modern", "Minimalist Modern", "MinimalistModern", "clean lines in quiet slate and sand tones", ["minimal", "modern", "clean", "slate", "grey", "simple"]),
    ("mosaic-courtyard", "Mosaic Courtyard", "MosaicCourtyard", "glazed tile fragments, geometric mosaic and terracotta", ["mosaic", "tile", "courtyard", "terracotta", "turquoise", "geometric"]),
    ("oceanic-siren", "Oceanic Siren", "OceanicSiren", "pearls, coral, seashells and deep sea blues", ["ocean", "siren", "sea", "pearl", "coral", "teal"]),
    ("paper-theater", "Paper Theater", "PaperTheater", "layered cut paper, origami and stacked silhouettes", ["paper", "theater", "origami", "papercut", "coral", "teal"]),
    ("porcelain-garden", "Porcelain Garden", "PorcelainGarden", "white porcelain with cobalt flowers and gold trim", ["porcelain", "china", "cobalt", "blue", "white", "floral"]),
    ("prehistoric-amber", "Prehistoric Amber", "PrehistoricAmber", "polished amber, fossils, ferns and carved stone", ["prehistoric", "amber", "fossil", "dinosaur", "fern", "stone"]),
    ("psychedelic-bloom", "Psychedelic Bloom", "PsychedelicBloom", "flowing waves, big daisies and bold color blocks", ["psychedelic", "groovy", "daisy", "seventies", "flower", "bold"]),
    ("racing-carbon", "Racing Carbon", "RacingCarbon", "carbon fiber, aerodynamic fins and racing stripes", ["racing", "carbon", "speed", "sport", "red", "checkered"]),
    ("retro-rpg", "Retro RPG", "RetroRPG", "16-bit menu windows, pixel crystals and gold trim", ["retro", "rpg", "pixel", "16-bit", "blue", "menu"]),
    ("retro-space-age", "Retro Space Age", "RetroSpaceAge", "rounded spacecraft panels, orbital rings and chrome", ["space age", "retro", "rocket", "orbit", "chrome", "atomic"]),
    ("steampunk-machinist", "Steampunk Machinist", "SteampunkMachinist", "brass, copper, gears and gauges", ["steampunk", "machinist", "brass", "gears", "copper", "gauge"]),
    ("sugarcraft-patisserie", "Sugarcraft Patisserie", "SugarcraftPatisserie", "piped icing, biscuit borders, candy glass and ribbons", ["sweets", "patisserie", "cake", "candy", "pastel", "dessert"]),
    ("tarot-arcana", "Tarot Arcana", "TarotArcana", "tarot card borders, suns, moons and constellations in gold", ["tarot", "arcana", "mystic", "sun", "moon", "gold"]),
    ("ukiyoe-fantasy", "Ukiyo-e Fantasy", "UkiyoeFantasy", "woodblock waves, clouds and mountains in indigo and vermilion", ["ukiyo-e", "japanese", "waves", "woodblock", "indigo", "cream"]),
    ("velvet-masquerade", "Velvet Masquerade", "VelvetMasquerade", "theater curtains, masks, feathers and gold trim", ["masquerade", "velvet", "mask", "theater", "wine", "feathers"]),
    ("void-cosmic-horror", "Void Cosmic Horror", "VoidCosmicHorror", "warped stars, violet void cracks and unsettling eyes", ["void", "cosmic", "horror", "purple", "eyes", "space"]),
    ("volcanic-forge", "Volcanic Forge", "VolcanicForge", "black basalt, hammered iron and glowing molten seams", ["volcanic", "forge", "lava", "basalt", "ember", "orange"]),
    ("watercolor-fantasy", "Watercolor Fantasy", "WatercolorFantasy", "soft watercolor washes, pale flowers and clouds", ["watercolor", "painted", "pastel", "flowers", "soft", "blue"]),
]

FULL = ("Background", "PlateFrame", "PortraitFrame")
HALVED = ("NameBacking", "Divider", "SectionHeader", "CornerOrnament")
SLICED = ("NameBacking", "Divider", "SectionHeader")
SIZES = {"Background": (1672, 941), "PlateFrame": (1672, 941), "PortraitFrame": (992, 1586), "CornerOrnament": (1254, 1254),
         "NameBacking": (2172, 724), "Divider": (2172, 724), "SectionHeader": (2172, 724)}

# Plate sizes, matched to Celestial Sakura's approved look (see AetherFrame/Assets/README.md).
SAKURA_NAME_BODY = 268     # the Sakura nameplate's plain band height, rows 208 to 476 of 724
SAKURA_NAME_FACTOR = 1.5   # its SizeFactor: a 40 px band around the starter name
DIVIDER_DRAWING = 48.0     # the Sakura Ornate divider's drawing height on the Plate, in px
DIVIDER_HEIGHT = 24.0      # ComponentPaintPlan.DividerHeight
HEADER_BODY = 22.0 / 24.0  # a Section Header's band: 22 px of the 24 px heading row
CORNER_FACTOR = 3.0        # as Celestial Sakura's Corner Ornament

# Cuts set by hand, in runtime pixels, where the measured ones leave a small ornament tip inside a
# stretching span (seen with the pieces stretched: Mosaic Courtyard's carved scroll tips, Memphis
# Playground's black arrowheads). The pixels are the measured run's; only where it stretches moves.
CUT_OVERRIDES = {
    ("MosaicCourtyard", "NameBacking"): (167, 189, 537, 537, 897, 908),
    ("MosaicCourtyard", "Divider"): (137, 137, 433, 651, 953, 953),
    ("MemphisPlayground", "NameBacking"): (152, 160, 541, 541, 925, 931),
    ("MemphisPlayground", "Divider"): (102, 104, 395, 691, 979, 984),
}

DARK_INK = (0x2A, 0x22, 0x1C)
LIGHT_INK = (0xF6, 0xF1, 0xE8)

# When neither ink reads at MIN_CONTRAST on a mid-tone (Alchemist's Workshop's background), a deeper
# one does; the tests require WCAG AA's 4.5:1, and the margin covers their slightly different averaging.
DEEP_INK = (0x14, 0x10, 0x0C)
MIN_CONTRAST = 4.6
DARK_SOFT = (0x5E, 0x54, 0x4C)
LIGHT_SOFT = (0xD3, 0xCC, 0xC2)


# ---------------------------------------------------------------- half size

def halve(img):
    """BundledArtImage.BuildLevels' halving: each texel averages its 2x2 source texels weighted by
    alpha; a texel with no coverage is transparent white."""
    src = img.convert("RGBA")
    w, h = (src.width + 1) // 2, (src.height + 1) // 2
    data = src.tobytes()
    stride = src.width * 4
    out = bytearray(w * h * 4)
    for y in range(h):
        y0 = 2 * y
        y1 = min(2 * y + 1, src.height - 1)
        for x in range(w):
            x0 = 2 * x
            x1 = min(2 * x + 1, src.width - 1)
            r = g = b = a = 0
            for sy in (y0, y1):
                row = sy * stride
                for sx in (x0, x1):
                    s = row + sx * 4
                    alpha = data[s + 3]
                    r += data[s] * alpha
                    g += data[s + 1] * alpha
                    b += data[s + 2] * alpha
                    a += alpha
            d = (y * w + x) * 4
            if (a + 2) // 4 == 0:
                out[d:d + 4] = b"\xff\xff\xff\x00"
                continue
            out[d] = (r + a // 2) // a
            out[d + 1] = (g + a // 2) // a
            out[d + 2] = (b + a // 2) // a
            out[d + 3] = (a + 2) // 4
    return Image.frombytes("RGBA", (w, h), bytes(out))


def save_png(img, path):
    buf = io.BytesIO()
    img.save(buf, "PNG", optimize=True)
    with open(path, "wb") as f:
        f.write(buf.getvalue())


def sha256(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


# ---------------------------------------------------------------- measuring cuts

def silhouette(px, x, h):
    top = bottom = None
    for y in range(h):
        if px[x, y][3] > 128:
            top = y
            break
    for y in range(h - 1, -1, -1):
        if px[x, y][3] > 128:
            bottom = y
            break
    return top, bottom


def coldiff(a, b):
    total = 0.0
    for p, q in zip(a, b):
        wa, wb = p[3] / 255.0, q[3] / 255.0
        total += abs(p[0] * wa - q[0] * wb) + abs(p[1] * wa - q[1] * wb) + abs(p[2] * wa - q[2] * wb) + abs(p[3] - q[3])
    return total / (len(a) * 4)


def measure_cuts(img):
    """Where a horizontal piece can be cut (full size): the fills are the longest runs of columns, left
    and right of the middle, whose silhouette and colors match their neighbors' and the run's middle
    column's; a run crossing the middle means no center piece. Returns ArtSlices' six values and the
    plain band's rows (top, bottom)."""
    img = img.convert("RGBA")
    px = img.load()
    w, h = img.size
    step = 2
    cols = {x: [px[x, y] for y in range(0, h, 2)] for x in range(0, w, step)}
    sils = {x: silhouette(px, x, h) for x in range(0, w, step)}
    good = {}
    for x in range(0, w - 8, step):
        s0, s1 = sils[x], sils[x + 8]
        good[x] = (s0[0] is not None and s1[0] is not None and abs(s0[0] - s1[0]) <= 3 and abs(s0[1] - s1[1]) <= 3
                   and coldiff(cols[x], cols[x + 8]) <= 6.0)

    def runs():
        found, start = [], None
        for x in range(0, w - 8, step):
            if good[x]:
                start = x if start is None else start
            elif start is not None:
                found.append((start, x))
                start = None
        if start is not None:
            found.append((start, w - 8))
        return found

    def steady(a, b):
        mid = ((a + b) // 2) // step * step
        ref, sref = cols[mid], sils[mid]
        for x in range(a, b, step * 4):
            s = sils[x]
            if s[0] is None or abs(s[0] - sref[0]) > 3 or abs(s[1] - sref[1]) > 3 or coldiff(cols[x], ref) > 9.0:
                return False
        return True

    def trim(a, b):
        a -= a % step
        b -= b % step
        while b - a >= 0.06 * w:
            if steady(a, b):
                return a, b
            a += 8
            b -= 8
        return None

    middle = w // 2
    all_runs = runs()
    crossing = [r for r in all_runs if r[0] < middle - 20 and r[1] > middle + 20]
    if crossing:
        t = trim(*crossing[0])
        if t:
            a, b = t
            mid = (a + b) // 2
            cuts = [a + 4, mid, mid, b - 4]
        else:
            raise ValueError("no steady fill across the middle")
    else:
        lefts = [trim(a, min(b, middle)) for a, b in all_runs if a < middle]
        rights = [trim(max(a, middle), b) for a, b in all_runs if b > middle]
        lefts = sorted((t for t in lefts if t), key=lambda t: t[0] - t[1])
        rights = sorted((t for t in rights if t), key=lambda t: t[0] - t[1])
        if not lefts or not rights:
            raise ValueError("no steady fill on each side")
        (la, lb), (ra, rb) = lefts[0], rights[0]
        cuts = [la + 4, lb - 4, ra + 4, rb - 4]

    # The band: the plain rows at the left fill's middle column; the text area runs out from each
    # cap's inner edge while the band's middle rows keep the fill's color.
    ref_x = (cuts[0] + cuts[1]) // 2
    top, bottom = silhouette(px, ref_x, h)
    mid_row = (top + bottom) // 2
    half = max(2, (bottom - top) // 10)
    rows = range(mid_row - half, mid_row + half + 1)

    def band(x):
        return [px[x, y] for y in rows]

    def reach(start, direction, ref):
        x = start
        while 0 <= x + direction < w and coldiff(band(x + direction), ref) <= 12.0:
            x += direction
        return x

    content_left = reach(cuts[0], -1, band(ref_x))
    content_right = reach(cuts[3], 1, band((cuts[2] + cuts[3]) // 2))
    return (content_left, cuts[0], cuts[1], cuts[2], cuts[3], content_right), (top, bottom)


def halve_cuts(cuts):
    content_left, cap_left, center_left, center_right, cap_right, content_right = cuts
    up = lambda v: -(-v // 2)
    if center_left == center_right:
        center_left = center_right = center_left // 2
    else:
        center_left, center_right = center_left // 2, up(center_right)
    # Fills shrink (caps and center grow) by at most a texel, so every fill column stays plain.
    return (content_left // 2, up(cap_left), center_left, center_right, cap_right // 2, up(content_right))


# ---------------------------------------------------------------- colors

def relative_luminance(rgb):
    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = rgb[:3]
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)


def mean_color(img, box):
    region = img.convert("RGBA").crop(box)
    r = g = b = n = 0
    for p in region.getdata():
        if p[3] > 200:
            r += p[0]
            g += p[1]
            b += p[2]
            n += 1
    return (r // max(1, n), g // max(1, n), b // max(1, n))


def band_color(img, cuts, band_rows):
    top, bottom = band_rows
    inset = (bottom - top) // 4
    return mean_color(img, (cuts[1] + 4, top + inset, cuts[2] - 4, bottom - inset))


def hexrgb(rgb):
    return "0x{:02X}{:02X}{:02X}".format(*rgb)


# ---------------------------------------------------------------- previews

def font(name, size):
    try:
        return ImageFont.truetype(os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts", name), size)
    except OSError:
        return ImageFont.load_default()


def sliced(src, cuts, width, height):
    """Draws sliced art (runtime cuts) width x height as AetherFrame does: caps and center at their
    proportions, the fills sharing the rest."""
    W, H = src.size
    content_left, cap_left, center_left, center_right, cap_right, content_right = cuts
    s = height / H
    fixed = cap_left + (center_right - center_left) + (W - cap_right)
    width = max(width, fixed * s)
    fill = (width - fixed * s) / 2
    xs = [0, cap_left * s]
    xs += [xs[-1] + fill]
    xs += [xs[-1] + (center_right - center_left) * s]
    xs += [xs[-1] + fill, width]
    us = [0, cap_left, center_left, center_right, cap_right, W]
    out = Image.new("RGBA", (max(1, round(width)), max(1, round(height))), (0, 0, 0, 0))
    for i in range(5):
        x0, x1 = round(xs[i]), round(xs[i + 1])
        if x1 > x0 and us[i + 1] > us[i]:
            out.alpha_composite(src.crop((us[i], 0, us[i + 1], H)).resize((x1 - x0, out.height), Image.LANCZOS), (x0, 0))
    return out


def preview(set_dir, prefix, pieces, data, colors, out_path, name_file=None, divider_file=None):
    """A sample Classic Plate in the style, 384 x 216: an illustration for the theme browser, made
    from the runtime pieces and placed roughly as AetherFrame places them."""
    k = 0.3
    img = lambda f: Image.open(os.path.join(set_dir, f)).convert("RGBA")
    canvas = img(f"{prefix}_Background.png").resize((round(1280 * k), round(720 * k)), Image.LANCZOS)
    draw = ImageDraw.Draw(canvas)
    px0, py0, pw, ph = 40 * k, 40 * k, 400 * k, 640 * k
    portrait = Image.new("RGBA", (round(pw), round(ph)), (30, 30, 40, 210))
    pd = ImageDraw.Draw(portrait)
    pd.ellipse((pw * 0.3, ph * 0.18, pw * 0.7, ph * 0.45), fill=(74, 74, 90, 255))
    pd.rounded_rectangle((pw * 0.15, ph * 0.47, pw * 0.85, ph * 1.05), radius=round(pw * 0.2), fill=(74, 74, 90, 255))

    # Section header backings first (behind everything), then the portrait and its frame.
    sh = img(f"{prefix}_SectionHeader.png")
    sh_cuts, sh_factor = data["SectionHeader"]
    head_font = font("segoeuib.ttf", max(6, round(16 * k)))
    value_font = font("segoeui.ttf", max(6, round(20 * k)))
    rows = [("HOME WORLD", "Phoenix", 480, 196), ("FREE COMPANY", "The Last Light", 880, 196),
            ("FAVORITE JOB", "Paladin", 480, 276), ("ACTIVE HOURS", "Evenings", 880, 276),
            ("PLAYSTYLE", "Raids, glamour, roleplay", 480, 356), ("MESSAGE", "Say hello!", 480, 460)]
    for head, _, x, y in rows:
        text_w = draw.textlength(head, font=head_font) / k
        anchor_w, anchor_h = text_w + 10 + 12, 24
        height = anchor_h * sh_factor
        s = height / sh.height
        width = anchor_w + (sh_cuts[0] + sh.width - sh_cuts[5]) * s
        tag = sliced(sh, sh_cuts, width * k, height * k)
        left = (x - 6 - sh_cuts[0] * s) * k
        canvas.alpha_composite(tag, (round(left), round((y + 12) * k - tag.height / 2)))
    canvas.alpha_composite(portrait, (round(px0), round(py0)))
    canvas.alpha_composite(img(f"{prefix}_PortraitFrame.png").resize((round(pw), round(ph)), Image.LANCZOS), (round(px0), round(py0)))
    canvas.alpha_composite(img(f"{prefix}_PlateFrame.png").resize(canvas.size, Image.LANCZOS))

    # The name on its backing, the divider under it.
    name = "Hero Example"
    name_font = font("georgiab.ttf", round(40 * k))
    name_w = draw.textlength(name, font=name_font) / k
    nb = img(name_file or f"{prefix}_NameBacking.png")
    nb_cuts, nb_factor = data["NameBacking"]
    height = (60 + 12) * nb_factor
    s = height / nb.height
    width = name_w + 10 + 32 + (nb_cuts[0] + nb.width - nb_cuts[5]) * s
    plaque = sliced(nb, nb_cuts, width * k, height * k)
    canvas.alpha_composite(plaque, (round(860 * k - plaque.width / 2), round(74 * k - plaque.height / 2)))
    draw.text((860 * k, 74 * k), name, font=name_font, fill=colors["name"], anchor="mm")
    for head, value, x, y in rows:
        draw.text(((x + 4) * k, (y + 12) * k), head, font=head_font, fill=colors["accent"], anchor="lm")
        draw.text(((x + 4) * k, (y + 26) * k), value, font=value_font, fill=colors["text"])
    dv = img(divider_file or f"{prefix}_Divider.png")
    dv_cuts, dv_factor = data["Divider"]
    if dv_cuts:
        line = sliced(dv, dv_cuts, (name_w + 10) * k, DIVIDER_HEIGHT * dv_factor * k)
    else:
        h = DIVIDER_HEIGHT * dv_factor * k
        line = dv.resize((round(h * dv.width / dv.height), round(h)), Image.LANCZOS)
    canvas.alpha_composite(line, (round(860 * k - line.width / 2), round(120 * k - line.height / 2)))

    corner = img(f"{prefix}_CornerOrnament.png").resize((round(120 * k), round(120 * k)), Image.LANCZOS)
    inset, size = round(22 * k), round(120 * k)
    W, H = canvas.size
    canvas.alpha_composite(corner, (inset, inset))
    canvas.alpha_composite(corner.transpose(Image.FLIP_LEFT_RIGHT), (W - inset - size, inset))
    canvas.alpha_composite(corner.transpose(Image.FLIP_TOP_BOTTOM), (inset, H - inset - size))
    canvas.alpha_composite(corner.transpose(Image.ROTATE_180), (W - inset - size, H - inset - size))
    save_png(canvas.convert("RGB"), out_path)


# ---------------------------------------------------------------- one set

def process(source, slug, display, folder, out_dir, only=None):
    src_dir = os.path.join(source, folder)
    os.makedirs(out_dir, exist_ok=True)
    record = {"slug": slug, "name": display, "folder": folder, "pieces": {}}
    for piece in only or FULL + HALVED:
        src_path = os.path.join(src_dir, f"{folder}_{piece}.png")
        dst_path = os.path.join(out_dir, f"{folder}_{piece}.png")
        src = Image.open(src_path)
        if src.size != SIZES[piece]:
            raise ValueError(f"{src_path} is {src.size}, not {SIZES[piece]}")
        entry = {"source_sha256": sha256(src_path), "source_size": src.size}
        if piece in FULL:
            shutil.copyfile(src_path, dst_path)
        else:
            save_png(halve(src), dst_path)
        if piece in SLICED:
            cuts, band_rows = measure_cuts(src)
            entry["cuts_full"] = cuts
            entry["cuts"] = CUT_OVERRIDES.get((folder, piece), halve_cuts(cuts))
            entry["band_rows"] = band_rows
            entry["band_color"] = band_color(src.convert("RGBA"), cuts, band_rows)
            body = band_rows[1] - band_rows[0]
            if piece == "NameBacking":
                factor = SAKURA_NAME_FACTOR * SAKURA_NAME_BODY / body
            elif piece == "SectionHeader":
                factor = HEADER_BODY * src.height / body
            else:
                bbox = src.convert("RGBA").getchannel("A").point(lambda a: 255 if a > 16 else 0).getbbox()
                factor = DIVIDER_DRAWING * src.height / (DIVIDER_HEIGHT * (bbox[3] - bbox[1]))
            entry["factor"] = round(min(4.0, max(0.25, factor)), 3)
        elif piece == "CornerOrnament":
            entry["factor"] = CORNER_FACTOR
        runtime = Image.open(dst_path)
        entry["runtime_size"] = runtime.size
        entry["runtime_mode"] = runtime.mode
        entry["runtime_sha256"] = sha256(dst_path)
        entry["runtime_bytes"] = os.path.getsize(dst_path)
        record["pieces"][piece] = entry
    return record


def contrast(a, b):
    """WCAG contrast ratio of two colors."""
    la, lb = relative_luminance(a), relative_luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def dark_ink_reads_better(behind):
    """True when dark ink contrasts with the color behind it more than light ink does."""
    return contrast(DARK_INK, behind) >= contrast(LIGHT_INK, behind)


def ink_on(behind):
    """The ink for text on behind: the one that reads better, or, when it falls short of MIN_CONTRAST,
    a deeper dark (then black) or white."""
    if dark_ink_reads_better(behind):
        for ink in (DARK_INK, DEEP_INK, (0, 0, 0)):
            if contrast(ink, behind) >= MIN_CONTRAST:
                return ink
        return (0, 0, 0)
    for ink in (LIGHT_INK, (0xFF, 0xFF, 0xFF)):
        if contrast(ink, behind) >= MIN_CONTRAST:
            return ink
    return (0xFF, 0xFF, 0xFF)


def style_colors(background_path, name_band, header_band):
    background = Image.open(background_path).convert("RGB")
    W, H = background.size
    panel = mean_color(background.convert("RGBA"), (round(W * 0.375), round(H * 0.25), round(W * 0.97), round(H * 0.95)))
    dark_panel = not dark_ink_reads_better(panel)
    light_name_band = dark_ink_reads_better(name_band)
    light_header_band = dark_ink_reads_better(header_band)
    text, accent, name = ink_on(panel), ink_on(header_band), ink_on(name_band)
    return {
        "primary": mean_color(background.convert("RGBA"), (0, 0, W // 2, H // 2)),
        "secondary": mean_color(background.convert("RGBA"), (W // 2, H // 2, W, H)),
        "text": text,
        "soft": LIGHT_SOFT if dark_panel else DARK_SOFT,
        "accent": accent,
        "name": name,
        "name_outline": LIGHT_INK if light_name_band else DARK_INK,
        "name_outline_strength": 0.2 if light_name_band else 0.35,
        "light_name_band": light_name_band,
        "light_header_band": light_header_band,
        "panel_luminance": round(relative_luminance(panel), 3),
        "name_contrast": round(contrast(name, name_band), 2),
        "header_contrast": round(contrast(accent, header_band), 2),
        "text_contrast": round(contrast(text, panel), 2),
    }


# ---------------------------------------------------------------- output

def cs_float(v):
    return f"{v:g}f" if "." in f"{v:g}" or "e" in f"{v:g}" else f"{v:g}f"


def cs_slices(c):
    return f"new ArtSlices({c[0]}, {c[1]}, {c[2]}, {c[3]}, {c[4]}, {c[5]})"


def cs_colors(c):
    return (f"new ArtStyleColors({hexrgb(c['primary'])}, {hexrgb(c['secondary'])}, {hexrgb(c['text'])}, {hexrgb(c['accent'])}, "
            f"{hexrgb(c['soft'])}, {hexrgb(c['name'])}, {hexrgb(c['name_outline'])}, {cs_float(c['name_outline_strength'])})")


def write_csharp(path, sets, sakura):
    lines = [
        "// <auto-generated>",
        "// Written by tools/art/make_runtime_art.py from the art sets' pixels: the runtime sizes, the",
        "// measured cuts (ArtSlices, in runtime pixels), the sizes on the Plate and the styles' text",
        "// colors. Edit the script and run it again rather than editing this file.",
        "// </auto-generated>",
        "",
        "namespace AetherFrame.Domain.Components;",
        "",
        "internal static partial class ArtSetData",
        "{",
        "    /// <summary>The art sets AetherFrame bundles besides Celestial Sakura, in display order.</summary>",
        "    internal static readonly ArtSetSpec[] Sets =",
        "    [",
    ]
    for s in sets:
        p = s["pieces"]
        kw = ", ".join(f'"{k}"' for k in s["keywords"])
        lines += [
            f'        new("{s["slug"]}", "{s["name"]}", "{s["folder"]}", "{s["theme"]}", [{kw}],',
            f'            NameBacking: new({p["NameBacking"]["factor"]}f, {cs_slices(p["NameBacking"]["cuts"])}),',
            f'            Divider: new({p["Divider"]["factor"]}f, {cs_slices(p["Divider"]["cuts"])}),',
            f'            SectionHeader: new({p["SectionHeader"]["factor"]}f, {cs_slices(p["SectionHeader"]["cuts"])}),',
            f'            Colors: {cs_colors(s["colors"])}),',
        ]
    sh = sakura["pieces"]["SectionHeader"]
    lines += [
        "    ];",
        "",
        "    /// <summary>Celestial Sakura's Section Header, the one piece its set gained.</summary>",
        f'    internal static readonly SlicedArtSpec CelestialSakuraSectionHeader = new({sh["factor"]}f, {cs_slices(sh["cuts"])});',
        "",
        "    /// <summary>Celestial Sakura's style colors.</summary>",
        f"    internal static readonly ArtStyleColors CelestialSakuraColors = {cs_colors(sakura['colors'])};",
        "}",
        "",
    ]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def write_markdown(path, records):
    lines = [
        "# Art sets: runtime copies",
        "",
        "Written by `tools/art/make_runtime_art.py` from the sources in the owner's art folder. The",
        "Background, Plate Frame and Portrait Frame are the sources, byte for byte. The Name Backing,",
        "Divider, Section Header and Corner Ornament are half size, averaged exactly as AetherFrame builds",
        "its own half-size level, so at or below half size they draw exactly as the full-size sources",
        "would. Cuts are `ArtSlices` in runtime pixels (ContentLeft, CapLeft, CenterLeft, CenterRight,",
        "CapRight, ContentRight); equal CenterLeft and CenterRight mean no center piece.",
        "",
        "| Set | Piece | Runtime | Bytes | Cuts | Size factor | Source SHA-256 | Runtime SHA-256 |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for r in records:
        for piece, e in r["pieces"].items():
            size = f'{e["runtime_size"][0]} x {e["runtime_size"][1]} {e["runtime_mode"]}'
            cuts = ", ".join(str(v) for v in e["cuts"]) if "cuts" in e else ""
            factor = e.get("factor", "")
            lines.append(f'| {r["name"]} | {piece} | {size} | {e["runtime_bytes"]} | {cuts} | {factor} | `{e["source_sha256"]}` | `{e["runtime_sha256"]}` |')
    lines.append("")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def main():
    if len(sys.argv) != 2 or not os.path.isfile("AetherFrame.slnx"):
        sys.exit('Run from the repository root: python tools/art/make_runtime_art.py "<art folder>"')
    source = sys.argv[1]
    assets = os.path.join("AetherFrame", "Assets")
    components = os.path.join(assets, "Components")
    previews = os.path.join(assets, "StylePreviews")
    os.makedirs(previews, exist_ok=True)

    records = []
    for slug, display, folder, theme, keywords in FAMILIES:
        out_dir = os.path.join(components, folder)
        r = process(source, slug, display, folder, out_dir)
        r["theme"], r["keywords"] = theme, keywords
        p = r["pieces"]
        r["colors"] = style_colors(os.path.join(out_dir, f"{folder}_Background.png"), p["NameBacking"]["band_color"], p["SectionHeader"]["band_color"])
        data = {k: (p[k]["cuts"], p[k]["factor"]) for k in SLICED}
        preview(out_dir, folder, p, data, r["colors"], os.path.join(previews, f"{folder}.png"))
        records.append(r)
        c = r["colors"]
        print(f'{display:<20} name band {"light" if c["light_name_band"] else "dark "} ({c["name_contrast"]}:1)'
              f'  header band {"light" if c["light_header_band"] else "dark "} ({c["header_contrast"]}:1)'
              f'  text on background {c["text_contrast"]}:1  factors NB {p["NameBacking"]["factor"]} DV {p["Divider"]["factor"]} SH {p["SectionHeader"]["factor"]}')

    sakura_dir = os.path.join(components, "CelestialSakura")
    sakura = process(source, "celestial-sakura", "Celestial Sakura", "CelestialSakura", sakura_dir, only=("SectionHeader",))
    nameplate = Image.open(os.path.join(sakura_dir, "CelestialSakura_Nameplate.png")).convert("RGBA")
    sakura_name_band = mean_color(nameplate, (512, 260, 760, 424))
    sakura["colors"] = style_colors(os.path.join(sakura_dir, "CelestialSakura_Background.png"), sakura_name_band, sakura["pieces"]["SectionHeader"]["band_color"])
    sakura_data = {
        "NameBacking": ((340, 512, 760, 1400, 1672, 1840), 1.5),
        "Divider": (None, 3.0),
        "SectionHeader": (sakura["pieces"]["SectionHeader"]["cuts"], sakura["pieces"]["SectionHeader"]["factor"]),
    }
    preview(sakura_dir, "CelestialSakura", None, sakura_data, sakura["colors"], os.path.join(previews, "CelestialSakura.png"),
            name_file="CelestialSakura_Nameplate.png", divider_file="CelestialSakura_Divider_Ornate.png")

    short = [r["name"] for r in records + [sakura]
             if min(r["colors"]["name_contrast"], r["colors"]["header_contrast"], r["colors"]["text_contrast"]) < MIN_CONTRAST]
    if short:
        sys.exit("Text doesn't reach " + str(MIN_CONTRAST) + ":1 on: " + ", ".join(short))

    write_csharp(os.path.join("AetherFrame", "Domain", "Components", "ArtSetData.g.cs"), records, sakura)
    write_markdown(os.path.join(assets, "ArtSets.md"), records + [sakura])
    total = sum(e["runtime_bytes"] for r in records + [sakura] for e in r["pieces"].values())
    total += sum(os.path.getsize(os.path.join(previews, f)) for f in os.listdir(previews))
    print(f"wrote {len(records)} sets and Celestial Sakura's Section Header: {total / 1e6:.1f} MB with the previews")


if __name__ == "__main__":
    main()
