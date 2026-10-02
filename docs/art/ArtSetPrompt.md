# Making an art set with ChatGPT

An art set is seven matching pieces of artwork that players can put on a Plate. This page has the
prompts to give ChatGPT, with the size and the rules for each piece. The sizes match the Celestial
Sakura set, which ChatGPT made at exactly these sizes.

## How to use it

1. Open a **new** ChatGPT chat for each set, so every piece is made in the same conversation and
   matches the others.
2. Paste the **set prompt** below, with your theme filled in. ChatGPT replies with a style sheet,
   one picture of the whole set, so you can check the look first. Ask for changes until you like it.
3. Paste the **piece prompts** one at a time, in the order given. Wait for each picture before you
   paste the next prompt.
4. Download each piece with ChatGPT's download button. Don't screenshot, crop, resize or re-save
   it. Name each file `<SetName>_<Piece>.png`, for example `FrostHollow_NameBacking.png`.
5. Check each picture against the checklist at the end. If one fails, tell ChatGPT what's wrong
   and ask it to make that piece again.
6. Put the seven PNGs in one folder and give Claude the path. Claude measures each piece, checks
   its transparency, and adds the set to AetherFrame.

## What each piece is for

| Piece | Size (pixels) | Shape | Background | Where it goes |
|---|---|---|---|---|
| Background | 1672 x 941 | 16:9 | opaque | fills the whole Plate |
| Plate Frame | 1672 x 941 | 16:9 | transparent | a border around the whole Plate |
| Corner Ornament | 1254 x 1254 | square | transparent | the top-left corner, mirrored into the other three |
| Portrait Frame | 992 x 1586 | 5:8 | transparent | a border around the character portrait |
| Name Backing | 2172 x 724 | 3:1 | transparent | a plaque behind the character's name; stretches to fit the name |
| Divider | 2172 x 724 | 3:1 | transparent | an ornamental line under the name; stretches to fit |
| Section Header | 2172 x 724 | 3:1 | transparent | a slim label behind each section's heading; stretches to fit |

The Plate is 1280 x 720. The portrait fills the left third: it is 400 x 640, 40 px from the top
and left edges. The name is across the top of the right two thirds. The info sections ("HOME
WORLD", "FREE COMPANY", "PLAYSTYLE" and so on) fill the rest of the right two thirds, each with a
small heading above its text.

**Name backings stretch.** You only need one Name Backing. AetherFrame cuts it into five parts:
the left end, a plain part, the center crest, another plain part, and the right end. The ends and
the crest never change shape. Only the two plain parts stretch, so a short name gets a short plaque
and a long name a long one. That only works if those two plain parts look the same all the way
along. The Name Backing, Divider and Section Header prompts below ask for exactly that.

## The set prompt

Replace `[SET NAME]` and `[THEME]` with one of the [themes](#themes) below, or your own.

```
I want you to design a matching set of decorative artwork for a Final Fantasy XIV plugin called
AetherFrame. Players use it to build a character profile card, called a Plate, like the game's
Adventure Plate. The card is 1280 x 720: the character's portrait fills the left third, their name
runs across the top of the right two thirds, and short info sections with small headings fill the
rest.

Set name: [SET NAME].
Theme: [THEME].

The set has 7 pieces: a Background, a Plate Frame, a Corner Ornament, a Portrait Frame, a Name
Backing, a Divider and a Section Header. I'll ask for each piece separately, at an exact size. Every
piece must follow these rules:

1. Original artwork. Don't copy Square Enix logos, job icons, game UI or any other existing design.
2. No text, letters, numbers, runes that look like letters, logos, signatures or watermarks.
3. A flat, straight-on front view, perfectly level and centered: no perspective, tilt or camera
   angle.
4. Left-right symmetrical, except the Corner Ornament.
5. Soft, even lighting from the front, with no strong shadow on one side, because some pieces are
   mirrored.
6. The same palette, materials, line weight and motifs on every piece, so the set looks like one
   design.
7. A real transparent background (PNG with an alpha channel) on every piece except the Background.
   No checkerboard pattern, no white or black backdrop, no drop shadow cast onto a backdrop, no
   border around the picture.
8. Nothing touches or is cut off by the edge of the picture: leave a small empty margin, about 2%
   of the width, on every side. (The Background and the Plate Frame are the exceptions; I'll
   explain them in their own prompts.)
9. Text sits on top of this artwork in the game, so every area where text goes must be calm, with
   a single even color and no busy detail.
10. The exact pixel size I give for each piece. If you can't make that exact size, make that exact
    shape (the same width-to-height ratio) as large as you can.
11. Bold details. The game shows these pieces much smaller than you make them, often a sixth of the
    size, so the thinnest line should be at least 8 pixels thick and nothing important should be
    tiny.
12. The plugin draws all text itself, in fonts the player picks, so any typography in the theme
    just means calm, clear areas where text will go, never actual lettering.

First, make one style sheet image showing all 7 pieces together on a neutral grey background, so
I can check the look before we start. After that, make one piece per message, in the order I ask.
```

## The piece prompts

Paste these one at a time, in this order. The Plate Frame comes first because it sets the
ornament style the others follow.

### 1. Plate Frame (1672 x 941)

```
Piece 1 of 7: the Plate Frame. 1672 x 941 pixels (16:9), transparent background.

A decorative border around the whole card, following the style sheet.
- The border runs close to the outer edge: every part of it is within about 4% of the width from
  the edge, and it may come within a few pixels of the edge. Everything inside the border is fully
  transparent.
- Each corner can have an ornament reaching up to about 15% of the width and height in from the
  corner.
- You may add one small crest at the top center, within the middle 20% of the width, no deeper than
  5% of the height (the character's name is just below it).
- Between the corner ornaments and the crest, the straight runs of the border must be plain and
  identical all along: the same thickness, color and pattern in every column (top and bottom) and
  every row (left and right sides), with no gems, knots or flourishes. They may be stretched later
  to fit other card shapes.
```

### 2. Corner Ornament (1254 x 1254)

```
Piece 2 of 7: the Corner Ornament. 1254 x 1254 pixels (square), transparent background.

One ornament for the TOP-LEFT corner of the card, in the same style as the Plate Frame's corners
but richer, since it can be placed on top of the frame or used on its own.
- It hugs the top edge and the left edge of the picture, like an L or a curl in the corner, and
  thins out toward the center.
- The bottom-right half of the picture is empty and transparent.
- The game mirrors this picture into the other three corners, so nothing in it may look wrong when
  flipped: no one-handed objects, no lighting from one side.
- This piece is NOT symmetrical left to right, but it should be symmetrical along the diagonal from
  the top-left corner to the bottom-right corner, so its top arm and left arm match.
```

### 3. Portrait Frame (992 x 1586)

```
Piece 3 of 7: the Portrait Frame. 992 x 1586 pixels (5:8, taller than wide), transparent
background.

A frame around the character's portrait, matching the Plate Frame.
- A slim border along the edge: no more than about 6% of the width on each side.
- Ornaments at the corners, reaching no more than about 20% of the width in from each corner.
- The whole inside is fully transparent, because the portrait shows through it. Nothing may hang
  into the middle.
- Between the corner ornaments, the straight runs of the border are plain and identical all along,
  as on the Plate Frame.
```

### 4. Name Backing (2172 x 724)

```
Piece 4 of 7: the Name Backing. 2172 x 724 pixels (3:1, wide), transparent background.

A horizontal plaque, like a nameplate, that the character's name is written on. The game stretches
it to fit names of any length, so it has to be built in five parts, left to right:
1. Left end: an ornament, inside the leftmost 25% of the width.
2. A plain stretch: straight parallel rails along the top and bottom, and a smooth, even band
   between them.
3. Center crest: an ornament in the middle, inside the middle 30% of the width. It may rise above
   the band and hang below it, but it must not cover the middle of the band, where the name goes.
4. A second plain stretch, the same as part 2.
5. Right end: a mirror image of the left end, inside the rightmost 25%.

Each plain stretch must be at least 10% of the width, and it must look exactly the same in every
column: no gems, studs, cracks, veins, sparkles, gradients or shading that changes from left to
right. Shading that changes from top to bottom is fine. Only those two parts are stretched, so any
detail in them would smear. If the theme is better without a center crest, leave it out and tell
me; then the two plain stretches simply meet in the middle.

The band where the name is written runs the full length between the two ends. It is centered
vertically and about one third of the picture's height. Make it one calm color, either clearly
light (for a dark name) or clearly dark (for a light name), and tell me which you chose.
```

### 5. Divider (2172 x 724)

```
Piece 5 of 7: the Divider. 2172 x 724 pixels (3:1, wide), transparent background.

An ornamental horizontal line that sits under the character's name. Like the Name Backing, the game
stretches it to fit, so build it in five parts, left to right:
1. Left end: a small finial or flourish, inside the leftmost 15% of the width.
2. A plain straight line: the same thickness and color all along, with no beads, taper, gradient
   or sparkle. (A glow or shading above and below the line is fine if it's the same all along.)
3. Center ornament: inside the middle 30% of the width.
4. The same plain straight line as part 2.
5. Right end: a mirror image of the left end, inside the rightmost 15%.

The whole drawing sits in the middle half of the picture's height. The top quarter and bottom
quarter are empty.
```

### 6. Section Header (2172 x 724)

```
Piece 6 of 7: the Section Header. 2172 x 724 pixels (3:1, wide), transparent background.

A slim label ribbon or plaque that a short heading sits on, such as "HOME WORLD" or "PLAYSTYLE",
written in small capital letters. The game stretches it to fit each heading.
- The ribbon is about 40% of the picture's height, centered vertically. The rest is empty.
- Small end ornaments, such as folded ribbon tails or small flourishes, inside the outer 15% of the
  width on each side.
- Everything between the two ends is plain and identical in every column, with no center ornament,
  because the heading text sits there.
- Make the ribbon a calm color that small text reads well on, and tell me whether it's light or
  dark.
- It should be a quieter, smaller relative of the Name Backing. The game shows it only about 20
  pixels tall, so keep it bold and simple: a clear shape and a clear outline, no fine detail.
```

### 7. Background (1672 x 941)

```
Piece 7 of 7: the Background. 1672 x 941 pixels (16:9). This is the only piece that is fully
opaque, with no transparency at all, and it fills the picture edge to edge.

A scenic or textured backdrop in the set's palette, drawn behind everything else on the card.
- Keep it calm and low in contrast, especially in the right two thirds, where the name and the info
  sections are written. No strong focal point, bright highlight or busy detail there.
- The left third is mostly covered by the character's portrait.
- No characters, creatures, text or symbols.
- It should look good with the Plate Frame from piece 1 on top of it.
```

## Themes

Each theme gives the set name (also the start of each file name) and the theme text to paste in
place of `[THEME]`. The themes describe a look, not a place in the game, so ChatGPT draws something
new instead of copying the game's own emblems. Each theme also has a note on what to watch for,
mostly what must stay out of the plain stretches. "Band" is the Name Backing's band. A light band
takes a dark name, and a dark band a light name.

### 1. Celestial Sakura

AetherFrame already has this set, Section Header included (a half-size runtime copy of the owner's
full-size piece, added on 2026-10-01 by tools/art/make_runtime_art.py; its two Dividers don't
stretch). To extend it, open a new chat, attach `CelestialSakura_Nameplate.png` and
`CelestialSakura_Divider_Ornate.png` (in `AetherFrame/Assets/Components/CelestialSakura/`), paste
the style-matching prompt below, then the piece prompt you need.

```
These two images are from an existing art set: champagne gold filigree, blush cherry blossoms,
pearls and a crescent moon. Make one new piece that matches them exactly in style, palette,
materials and line weight. Transparent background (PNG with an alpha channel), no text, no
checkerboard, flat front view, left-right symmetrical.
```

### 2. Ishgardian Gothic: `IshgardianGothic`

```
Gothic cathedral architecture: pointed arches and stone tracery, stained glass in deep blues with
touches of gold, silver filigree, pale carved stone, original heraldic shields and banners (no real
coats of arms), frost and icy blue-white highlights.
```

Watch: stained glass and tracery go in the ends, the crest and the Background. The plain stretches
are plain silver rails and smooth stone. Band: dark blue enamel.

### 3. Allagan Tech: `AllaganTech`

```
Ancient high technology: dark gunmetal and aged bronze panels with geometric seams, glowing
circuitry lines in orange and cyan, hexagon and triangle motifs, small glowing energy cores.
```

Watch: in the plain stretches, a glowing line must run perfectly straight from left to right.
Junctions and cores go in the ends and the crest. Glows must fade into real transparency, never
into a black backdrop. Band: dark.

### 4. Ancient Amaurot: `AncientAmaurot`

```
Art deco meets a lost ancient civilization: polished black stone, gold inlay lines, stepped
geometric ornaments, sunbursts, stars, and faint glowing sigils of creation magic (original
shapes, not real or in-game symbols).
```

Watch: gold lines in the stretches run straight and level. Band: black stone.

### 5. Crystarium Crystal: `CrystariumCrystal`

```
Faceted crystal clusters, translucent glass, white marble, refracted light, blue and violet
gradients, slim silver accents.
```

Watch: crystal clusters only in the ends and the crest. In the stretches, gradients may only run
from top to bottom. Band: white marble.

### 6. Dark Fantasy: `DarkFantasy`

```
Blackened wrought iron, thorned vines, raven feathers and raven silhouettes, blood-red gems and
accents, worn and distressed metal, restrained gothic ornament.
```

Watch: put smoke only in the Background, because on a transparent piece it tends to turn into a
grey haze. Thorns and feathers go in the ends and the crest. The rails are plain iron, and the
band is smooth, not distressed. Band: dark.

### 7. High Fantasy Royal: `HighFantasyRoyal`

```
Ornate polished gold, white and cream marble, royal blue velvet, faceted gemstones, original
heraldic lions and crowns (no real coats of arms), strictly symmetrical and regal.
```

Watch: gems only in the ends and the crest. Band: royal blue velvet.

### 8. Watercolor Fantasy: `WatercolorFantasy`

```
Soft watercolor painting with visible brush and paper texture, pastel gradients, loosely painted
flowers, clouds and gentle sparkles of magic, edges that look hand-painted.
```

Watch: around each shape the background must still be truly transparent, with no paper texture.
The stretches are one flat, even wash with clean edges. Band: light.

### 9. Anime Pop: `AnimePop`

```
Bright saturated colors, thick black outlines, halftone dots, speed lines, sticker-style shapes
with white borders, stars and comic bursts.
```

Watch: no lettering at all (rule 12). Halftone and speed lines go only in the ends, the crest and
the Background. The stretches are flat color with a bold outline. Band: white.

### 10. Cyberpunk Neon: `CyberpunkNeon`

```
Neon grid lines, translucent holographic panels, glowing cyan, magenta and purple edges, small
glitch artifacts, sleek angular futuristic frames.
```

Watch: glitches only in the ends, the crest and the Background. Neon glows fade into real
transparency. Band: dark translucent panel.

### 11. Minimalist Modern: `MinimalistModern`

```
Clean geometry, lots of empty space, precise lines, subtle soft gradients, muted slate, sand and
grey with one small accent color.
```

Watch: "precise lines" still follow rule 11 (8 pixels at least), or they vanish in the game. The
Name Backing may well skip its center crest. Band: light.

### 12. Tarot and Arcana: `TarotArcana`

```
Ornate tarot card borders in gold on midnight black and deep purple, celestial symbols,
constellations, crescent moons, suns with faces, all-seeing eyes, mystical geometric diagrams,
engraved metal.
```

Watch: constellations and diagrams in the ends, the crest and the Background. Band: midnight.

### 13. Botanical Cottage Fantasy: `BotanicalCottage`

```
Wildflowers, climbing vines, little mushrooms, butterflies, pressed leaves on parchment, soft sage
greens with warm cream and brown.
```

Watch: vines only in the ends and the crest. The stretch is a plain twig or ribbon rail on smooth
parchment, with no stains. Band: parchment.

### 14. Oceanic / Siren: `OceanicSiren`

```
Deep ocean blues and teals, pearls, branching coral, seashell ornaments, flowing water curls,
bioluminescent glows in aqua and soft pink.
```

Watch: bubbles and coral only in the ends, the crest and the Background. Band: pearl.

### 15. Void and Cosmic Horror: `VoidCosmicHorror`

```
Black space with warped stars, cracks of glowing purple energy, floating stone shards, unsettling
eyes, impossible geometry.
```

Watch: cracks and eyes only in the ends, the crest and the Background. Floating shards stay inside
the picture's margin. The stretches are smooth obsidian. Band: dark.

### 16. Retro RPG: `RetroRPG`

```
16-bit era fantasy RPG: chunky pixel art where every art pixel is a clean square block of about
8 x 8 image pixels, classic menu windows with a blue top-to-bottom gradient and a white border,
small original crystals, parchment map details. Original designs only: no characters, creatures,
logos or crystals from any existing game.
```

Watch: the stretches must be flat colors only, because stretched pixel blocks turn into rectangles.
Shown small in the game, the pixels soften a little; that's expected. Band: the blue window.

### 17. Art Nouveau: `ArtNouveau`

```
Art Nouveau: flowing whiplash curves, stylized lilies and irises, elegant arched portrait framing,
stained-glass colors (amber, teal, plum) with gold outlines, in the spirit of 1900s posters but
entirely original.
```

Watch: the band must be straight; curves belong in the ends and the crest. Band: cream.

### 18. Japanese Ukiyo-e Fantasy: `UkiyoeFantasy`

```
Japanese woodblock print style: bold ink outlines, flat limited colors (indigo, vermilion, cream),
stylized waves, clouds and mountains, traditional patterns such as seigaiha waves and asanoha
stars, original compositions.
```

Watch: repeating patterns go only in the ends, the crest and the Background. The stretches are
flat color with an ink outline. Band: cream paper.

### 19. Steampunk / Machinist: `SteampunkMachinist`

```
Polished brass and copper, interlocking gears, pressure gauges, rivets, dark leather, blueprint
style mechanical line drawings.
```

Watch: rivets and stitching repeat, so they go only in the ends. The rails are plain brass and the
band is plain leather. Band: dark leather.

### 20. Cute Kawaii: `CuteKawaii`

```
Pastel pink, lavender, mint and baby blue, puffy rounded shapes, stars, hearts, clouds, bows and
ribbons, sticker-style white outlines, little sparkles.
```

Watch: hearts and sparkles only in the ends and the crest. Band: white.

## Checklist for each piece

Before you send a set, check each picture:

- [ ] It's the size asked for, or at least exactly that shape.
- [ ] The background is truly transparent: in an image viewer you see your viewer's own
      background through it, not a checkerboard, white or black (except the Background piece).
- [ ] There's no text, lettering or watermark anywhere.
- [ ] Nothing is cut off at the picture's edge.
- [ ] It's straight-on and level, and symmetrical where asked.
- [ ] Name Backing, Divider and Section Header: the plain stretches really are plain. Imagine
      pulling that part twice as long: would anything smear?
- [ ] It matches the rest of the set.

ChatGPT's PNGs carry hidden Content Credentials (a record that the image was made with AI).
AetherFrame keeps the files exactly as downloaded, so leave them unedited.
