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

Replace `[THEME]` with a few words about the look, for example "frost-covered silver and pale blue
crystal, Ishgard in winter", "brass, teak and rope, a pirate harbor" or "living wood, moss and
white flowers in a deep forest".

```
I want you to design a matching set of decorative artwork for a Final Fantasy XIV plugin called
AetherFrame. Players use it to build a character profile card, called a Plate, like the game's
Adventure Plate. The card is 1280 x 720: the character's portrait fills the left third, their name
runs across the top of the right two thirds, and short info sections with small headings fill the
rest.

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
right. Only those two parts are stretched, so any detail in them would smear.

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
   or sparkle.
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
- It should be a quieter, smaller relative of the Name Backing.
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
