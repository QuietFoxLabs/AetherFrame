# Built-in Component artwork

Runtime copies of built-in graphical Components. The Art Styles' preview cards (`StylePreviews/`)
and Celestial Dream's Astrolabe are embedded into `AetherFrame.dll` as manifest resources
(`AetherFrame.Assets.<path with dots>`, see `AetherFrame.csproj`); every other runtime PNG here is
hosted and downloaded the first time it is used (below). Nothing here is
copied to the output folder, and nothing here is ever persisted: Plates, Templates and
`.aetherframe` packages store only the stable definition id (`af.corner-ornament.astrolabe-pivot`),
which the compile-time catalog maps to the logical asset id
(`af.asset.celestial-dream.corner-ornament.astrolabe-pivot`) and from there to the resource.

The full-size approved sources were kept in a separate AetherFrameAssets repository, which no
longer exists; they are never needed at runtime or build time. The `Source:` lines below record
where each runtime copy was made from.

## Hosted artwork (art on demand)

Every runtime PNG under `Components/`, except Celestial Dream's, is hosted: players download each the first time they use it, from `https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/<commit>/AetherFrame/Assets/<path>`, and the plugin uses it only when its length and SHA-256 are exactly those in `ArtFiles.txt` and the compiled `Domain/Components/ArtFiles.g.cs` ("Art on demand" in `docs/networking/DecisionRegister.md`).

- **Hosted bytes never change.** A file in the table keeps its bytes and its commit forever; new art gets a new artwork id and a new file. `tools/art/write_art_files.py` refuses a recorded file whose bytes differ, or one that is gone.
- **Adding hosted art** takes two commits on one branch: the first adds the PNGs (and whatever else the art pipeline writes); the second runs `python tools/art/write_art_files.py --write --commit <the first commit's full id>`. Merge the pull request with a merge commit, never a squash or rebase, so the first commit stays in master's history.
- **CI proves it.** The `art-pins` job checks that every pinned commit is in the branch's history and holds exactly the table's bytes, and that nothing the base branch hosted changed or went.
- **The repository stays public**, or every address answers 404.

## Requirements for every runtime PNG

- 8-bit RGBA (or RGB for fully opaque art), non-interlaced, at most 4096 px per side (what
  `BundledArtImage` decodes). Its exact size is stated in `BuiltInArtCatalog`, and it is always
  drawn at that aspect ratio.
- Real alpha transparency; no baked background or checkerboard (except an opaque Background).
- Tinted line art (Celestial Dream): square and power-of-two, so every level halves exactly;
  white/greyscale (`R = G = B`), since the Component color multiplies it at draw time; fully
  transparent texels stored white, so bilinear filtering never darkens a tint at line edges.
- Full-color art (Celestial Sakura): kept exactly as approved. When it loads, the invisible
  texels next to the drawing take its edge color (see `BundledArtImage.BleedIntoTransparentTexels`),
  so filtering never outlines strokes dark. The file itself is not changed.
- Corner Ornaments are drawn for the top-left corner. The catalog says whether the other corners
  rotate or mirror it.
- Name Backings and Dividers may be **sliced** (`ArtSlices` in `BuiltInArtCatalog`), below.
- Plate Frames and Portrait Frames are **cut to fit** (`ArtFrameSlices`), below.

`BuiltInArtTests`, `CelestialSakuraTests`, `SlicedArtTests`, `FrameFittingTests` and `ArtSetsTests` check all of this against the files (which the tests embed in their own assembly) and the hosted table's SHA-256s.

## Sliced artwork (Name Backings, Dividers and Section Headers)

A name can be two letters or twenty, so a backing drawn at one fixed ratio is either tiny behind a
short name or too short for a long one. Sliced artwork is cut at five x positions into a left cap,
a fill, a center piece, a fill and a right cap:

```
| left cap | left fill | center piece | right fill | right cap |
0       CapLeft    CenterLeft    CenterRight    CapRight     width
   ContentLeft ^                                    ^ ContentRight
```

- The caps and the center piece are always drawn at the artwork's own proportions. Only the two
  fills stretch, equally. So a fill must look the same in every column: straight rails and a plain
  band, no ornament. Artwork without a center piece sets `CenterLeft` equal to `CenterRight`.
- Height: the anchor box's height times `SizeFactor`, as for unsliced art.
- Width: the anchor box (the padded name and title, or the Divider's line) spans `ContentLeft` to
  `ContentRight`, so the plaque is the box's width plus the art outside its text area. It is never
  narrower than the caps and center piece together, and never stretches past the Plate's left or
  right edge. Scale, Offset and Rotation then apply to the whole plaque as usual.
- Each piece is its own primitive (`ComponentPrimitive.Piece`) with its own texture window. All
  pieces of one placement draw from the same level. A shared Plate names each piece by its own art
  ident, the artwork's id plus `.left-cap`, `.left-fill`, `.center`, `.right-fill` or `.right-cap`
  (`BuiltInArtCatalog.FindPiece`), so the art quad's format is unchanged.
- To measure a new piece: the fills are where every column has the same silhouette and nearly the
  same colors. Measure them on the runtime PNG, never by eye on a scaled preview.

## Frames cut to fit (Plate Frames and Portrait Frames)

A frame's corners and crests reach past its rails, and most frames' drawings stop a little short of
their image's edges (up to about 5%; a few touch them). So a frame laid on its box by its image sat
well inside the box. And the Plate or the picture it frames is not always the shape it was drawn
for. So every frame is cut into a grid (`ArtFrameSlices`: an `ArtSlices` for its columns, one for its
rows, and its drawing's bounds), October 2, 2026, at the owner's request:

- On each axis, `ContentLeft` to `ContentRight` is the outer edge of the frame's rails, laid on the
  box's edges: the canvas for a Plate Frame, the drawn picture for a Portrait Frame. What reaches
  past the rails (corner ornaments, a crest) reaches past the box, out to the drawing's bounds
  (alpha over 16); the faint halo beyond them is left out.
- The caps and the center piece keep the artwork's proportions, at the scale at which the rails fit
  inside the box; the two fills share the rest of each axis in proportion to their own lengths. A
  center piece is a mid-edge ornament: on six measured Plate Frames' top edges, and in both of
  Celestial Sakura's hand-set frames. Every other axis has one plain run across its middle, so no
  center piece.
- Only the border cells are drawn (`ArtPieces.FrameBorder`): every frame is clear between its caps.
  A shared Plate names each cell by its own ident, the artwork's id plus `.frame-r0c0` to
  `.frame-r4c4` (row, then column), so the art quad's format is unchanged.
- `tools/art/measure_frames.py` measures the cuts from the runtime PNGs (which it never changes)
  and writes `ArtFrameData.g.cs`. On each axis it takes one plain run of rail across the middle,
  or else the longest plain run on each side of it, cut 16 px inside, so even a small preview's
  smaller copy of the artwork samples only plain rail at the cuts. A rail's outer edge is where the
  drawing starts and ends across those fills (their median). Embroidered Tapestry's woven Plate
  Frame passes only its texture test; Celestial Sakura's frames are ornamented all along, so
  theirs are set by hand where least busy, and their few hand-cut pixels stretch visibly further
  than plain rail, more so on a canvas of another shape.

## Celestial Dream / Corner Ornaments / AstrolabePivot.png — 512 x 512

Source: `AetherFrameAssets/source/CelestialDream/AstrolabePivot.png`, 1254 x 1254 RGBA
(sha256 `a0d9d8a4…ea6f1`), unchanged.

**Why 512 is enough.** A Corner Ornament's box is `CornerSize (40) x SizeFactor (2) x canvasHeight/720`
logical pixels, times the Component's Size (25–400%), times the surface's fit scale. The Profile
View fits the Plate to its window, so on screen the default box is about `80 x windowHeight / 720`:

| View height | Size 100% | Size 200% | Size 400% |
|---|---|---|---|
| 720 px | 80 px | 160 px | 320 px |
| 1350 px (1440p full height) | 150 px | 300 px | 600 px |
| 2100 px (4K full height) | 233 px | 467 px | 933 px |

512 px covers every realistic setting without magnification — up to Size 200% on a full-height 4K
view, and up to ~340% at 1440p. Only extreme combinations magnify: Size 400% on a 4K full-height
view (~1.8x), or inspecting it with the Advanced editor zoomed in past fit. 1024 px would quadruple GPU memory
and file size for those edge cases only; 256 px would already magnify at Size 200% on 1440p.

**Why levels.** Dalamud textures have no mipmaps, so a 512 px texture drawn at its common 60–150 px
would skip texels and break the thin arcs. `BuiltInArtTextureCache` decodes the PNG once and builds
alpha-weighted halved levels (512, 256, 128, 64, 32 — about 1.4 MB of GPU memory in total) and
draws the smallest level at least as large as the on-screen box.

**How it was made.** Lanczos resample of the premultiplied source in float precision, luminance
(`0.2126 R + 0.7152 G + 0.0722 B`) un-premultiplied and normalized so the brightest line core is
white, alpha kept as resampled, transparent texels set to white. Regenerate the same way if the
approved source ever changes; never edit the source.

## Celestial Sakura / 7 full-color pieces

Seven original pieces created with AI assistance: champagne gold filigree, blush cherry blossoms,
pearls and a crescent moon. Each PNG is the approved file, byte for byte. Nothing was resampled,
cropped, padded, recolored or re-encoded. Only the two dividers were renamed from their original
names (`Divider_01` and `Divider_02`). `CelestialSakuraTests` checks every SHA-256 below against the
embedded resource.

| Runtime file | Pixels | Ratio | Alpha | Kind | SHA-256 |
|---|---|---|---|---|---|
| `CelestialSakura_Background.png` | 1672 x 941 | 1.7768 | none (RGB, opaque) | Background | `c7a939df…f7148` |
| `CelestialSakura_PlateFrame.png` | 1672 x 941 | 1.7768 | 77.3% clear | Plate Frame | `0c5c407d…6dad9` |
| `CelestialSakura_PortraitFrame.png` | 992 x 1586 | 0.6255 | 77.5% clear | Portrait Frame | `4278abbe…a4dde` |
| `CelestialSakura_Nameplate.png` | 2172 x 724 | 3.0000 | 58.6% clear | Name Backing | `57ef50f0…9af96` |
| `CelestialSakura_Divider_Ornate.png` (was `Divider_01`) | 2172 x 724 | 3.0000 | 83.4% clear | Divider | `099b678f…e442a` |
| `CelestialSakura_Divider_Slim.png` (was `Divider_02`) | 2172 x 724 | 3.0000 | 96.0% clear | Divider | `a1798774…f98f4` |
| `CelestialSakura_CornerOrnament.png` | 1254 x 1254 | 1.0000 | 75.9% clear | Corner Ornament | `e58f67f3…8899e` |

Because the files are unmodified, each one still carries its embedded C2PA Content Credentials
(provenance metadata recording how the image was generated). Re-encoding or stripping a file would
remove them and change its SHA-256.

**Plate-sized pieces against the canvas.** The Adventure Plate canvas is 1280 x 720 (16:9, 1.7778;
`ProfileDocument.DefaultCanvasWidth/Height` and `AdventurePlateClassicLayout.ReferenceWidth/Height`).
The Background and the Plate Frame are 1672 x 941 (1.7768). That is 0.053% off 16:9, because
exact 16:9 at this width would be 940.5 px. The difference is 0.4 px over the full 720 px height,
so both are drawn over the full canvas (`ComponentPaintPlan.ArtAspectTolerance` is 0.1%). The
frame's drawn border sits within 4 px of the top and 8 px of the bottom of its own 941 px canvas
(at most 6 logical px from any Plate edge at 1280 x 720), so it spans the full Plate height. It was
not normalized. Padding it to an exact 16:9 canvas (1680 x 945) would only add transparent margins
and slightly shrink the drawn frame. On a canvas of another shape (the Card presets are 3:2), the
art is fitted inside the canvas at its own ratio, never stretched.

**Portrait Frame against the portrait.** The Classic portrait is 400 x 640 (5:8, 0.625). The frame
is 992 x 1586 (0.6255), which is 0.076% off, so it is drawn exactly over the portrait.

**Default placements** on the Adventure Plate Classic, in logical px, before any Scale or Offset:

- Background and Plate Frame fill the canvas at (0, 0, 1280, 720). Art Plate Frames skip the
  14 px inset of the procedural borders, because the drawing carries its own margin, and paint
  between the pictures and the text: over the portrait and its frame, under the name and every text.
- Portrait Frame covers the portrait.
- Nameplate: sliced (see above), 1.5x the padded name box's height and centered on the name. With
  the starter's 60 px name box, it is 108 px tall, and as wide as the name's text plus about 100 px
  (at least 246 px, the blossom ends and the crest). Its cuts, measured on the PNG: `ContentLeft`
  340 and `ContentRight` 1840 (the ivory band's inner edges, inside the blossom ends);
  `CapLeft` 512 and `CapRight` 1672 (where the ends' scrolls finish); `CenterLeft` 760 and
  `CenterRight` 1400 (the crescent crest and the pearl below it, with their flourishes). Between
  512 and 760, and between 1400 and 1672, the plaque is only its rails and band: every column has
  the same silhouette (rows 208 to 476).
- Dividers are centered on the procedural Divider line and fitted at 3:1. The Ornate band is 3x
  the Divider height (216 x 72), which keeps the crescent clear of the name. The Slim band is 4x
  (288 x 96).
- Corner Ornament: 120 px squares (3x the 40 px corner box), inset 22 px, mirrored into the other
  corners.

All of these are starting points. Scale, Offset, Rotation and Layer order work on them exactly as
on every other Component.

**Memory and loading.** Each piece is about 1.57 megapixels. With its levels, each takes about
8.4 MB of GPU memory (about 59 MB if all seven are drawn). Each is decoded, prepared and uploaded
once, the first time a Plate draws it. That takes about 45–55 ms of CPU per piece, so it runs on
the thread pool (`BuiltInArtLoader`), not inside Draw. The piece appears a frame or a few later.
If that ever matters, the Celestial Dream route is available: approved, reduced runtime copies
made from the full-size sources.

## The art sets: 39 sets of seven pieces, and Celestial Sakura's Section Header

The first 19 came on October 1, 2026; art sets 21 to 40 (the owner's second twenty, `expansion-*`
in the source folder) the next day, with Celestial Sakura the 40th style. Where neither ink reads at
4.6:1 on a style's background (Alchemist's Workshop's details), the generator picks a deeper one, and
it stops if any style still falls short.

Made by `tools/art/make_runtime_art.py` from the owner's sources (run it from the repository root,
with the source folder as its argument). [ArtSets.md](ArtSets.md) lists every runtime file with
its source's SHA-256 and its own, its cuts and its size factor, and `ArtSetsTests` checks every
file against it. The ids, Components and Art Styles are made in `ArtSets` from the generated
`ArtSetData.g.cs`.

- The Background, Plate Frame and Portrait Frame are the sources, byte for byte (1672 x 941 and
  992 x 1586), with their Content Credentials.
- The Name Backing, Divider and Section Header (1086 x 362) and the Corner Ornament (627 x 627) are
  half size, the owner's choice of October 1, 2026, to keep the download near 77 MB instead of
  117 MB (since art on demand, each style's own download: about 2.3 to 5.5 MB). Each is averaged exactly as `BundledArtImage.BuildLevels` makes its own half-size level,
  so wherever the piece is drawn at or below half its source's size it draws exactly as the source
  would. At Size 100% with the Plate full screen, that is every piece on every screen up to 1440p.
  At 4K it is every Divider, Section Header and Corner Ornament, while 29 of the 39 Name Backings
  (size factors above 1.68) are magnified, by up to 1.45 times (Watercolor Fantasy's), and slightly
  softer.
- Where the measured cuts leave a small ornament tip inside a stretching span, the generator's
  `CUT_OVERRIDES` set them by hand (Mosaic Courtyard's and Memphis Playground's Name Backing and
  Divider).
- Cuts are measured on each source and halved (the fills shrink by at most a texel, so they stay
  plain). Pieces whose fills meet in the middle have no center piece.
- Size factors match Celestial Sakura's look: a Name Backing's plain band is 40 px around the
  starter name; a Divider's drawing about 48 px tall (most are thinner, so they stop at the largest
  factor, 4); a Section Header's band 22 px of the 24 px heading row; a Corner Ornament 3.
- A Section Header drawn from artwork is a backing: one placement behind each drawn heading,
  around its measured text with 6 px either side, at the bottom of the element stack. An end
  reaching past the heading's column tucks behind the portrait and its frame.
- Each set is also an Art Style, a theme (`af.style.<slug>`) that places its seven pieces. Its text
  colors are chosen from the pixels: dark or light ink, whichever contrasts more with the band
  behind the name, the band behind the headings, and the background behind the Details. Every
  style clears 4.5:1 (WCAG AA) on all three (`ArtSetsTests`).
- `StylePreviews/<Folder>.png` (384 x 216) is each style's card in the theme browser: a sample
  Plate the generator draws from the runtime pieces. It is an illustration, never a Component.
- Memory: a whole set drawn at once is about 33 MB of GPU memory with its levels, loaded once, the
  first time a Plate draws each piece, on the thread pool.
