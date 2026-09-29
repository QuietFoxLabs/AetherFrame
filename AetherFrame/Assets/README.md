# Bundled Component artwork

Runtime copies of built-in graphical Components, embedded into `AetherFrame.dll` as manifest
resources (`AetherFrame.Assets.<path with dots>`, see `AetherFrame.csproj`). Nothing here is
copied to the output folder, and nothing here is ever persisted: Plates, Templates and
`.aetherframe` packages store only the stable definition id (`af.corner-ornament.astrolabe-pivot`,
`af.divider.astral-gold-equator-line`...), which the compile-time catalog maps to the logical asset id
(`af.asset.celestial-dream.corner-ornament.astrolabe-pivot`, `af.asset.astral-gold.divider.equator-line`...)
and from there to the resource.

The full-size approved sources live in the separate AetherFrameAssets repository and are never
needed at runtime or build time.

## Requirements for every runtime PNG

- 8-bit RGBA, non-interlaced, with sides divisible by every halving the level chain makes (until
  the shorter side reaches 32 px): 512 x 512, 1536 x 512, 1536 x 864, 800 x 1280, 1152 x 384... —
  so every level is an exact 2x2 reduction (the only format `BundledArtImage` decodes). Drawn at
  exactly this aspect ratio: the paint plan fits the artwork inside its placement box, never
  stretching it.
- Real alpha transparency; no baked background or checkerboard.
- **Tintable** artwork (Celestial Dream): white/greyscale (`R = G = B`), the Component color
  multiplies it at draw time; fully transparent texels stored white, so bilinear filtering never
  darkens a tint at line edges.
- **Authored color** artwork (Astral Gold): full color, drawn as authored — the Component color is
  ignored, only Opacity applies; fully transparent texels carry the artwork's own nearby color, so
  bilinear filtering never adds a dark or white fringe.
- Drawn for the top-left corner (Corner Ornaments); the other corners are rotations of it.
- Centered on the line (Dividers): the Divider's line runs through the image's vertical center.

`BuiltInArtTests`, `EquatorLineTests` and `AstralGoldTests` check all of this against the embedded bytes.

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

## Celestial Dream / Dividers / EquatorLine.png — 1536 x 512

Source: `AetherFrameAssets/source/CelestialDream/EquatorLine.png`, 2172 x 724 RGBA, exactly 3:1
(sha256 `14293d1fc5b8c00c557c093aae60620a59d103868031062b4a1f48aa5843eacb`), unchanged.
Runtime: 222,097 bytes, sha256 `1dcff1228a55ffb125f8656777a53d283d4a5aca220bcc8f106cd187e65c6253`.

**Where it is drawn.** A Divider sits under the name and title. The procedural styles fill a band
`name width x 24` reference pixels; the artwork's band is 4x taller (`SizeFactor`, 96 px) around the
same center line, and the 3:1 drawing is fitted inside it. Under a name wider than 288 px it is
288 x 96 logical pixels (at 720 canvas height); under a shorter name it narrows with the name.
On screen that is about `288 x windowHeight / 720` wide, times the Component's Scale (25–400%):

| View height | Scale 100% | Scale 200% | Scale 400% |
|---|---|---|---|
| 720 px | 288 px | 576 px | 1152 px |
| 1350 px (1440p full height) | 540 px | 1080 px | 2160 px |
| 2100 px (4K full height) | 840 px | 1680 px | 3360 px |

**Why 1536 x 512.** The next 3:1 sizes with exact halving are 768 x 256 and 3072 x 1024. 768 would
already magnify at Scale 100% on a full-height 4K view and at Scale 150% on 1440p. 1536 covers
Scale 100% everywhere and up to ~280% at 1440p, and only magnifies slightly (~1.1x) at Scale 200%
on 4K — where the soft glow hides it. 3072 would exceed the 2048 px limit and quadruple memory for
extreme settings only. Levels (1536, 768, 384, 192, 96 wide) cost about 4.2 MB of GPU memory.

**How it was made.** As Astrolabe Pivot — Lanczos resample in float of the alpha-premultiplied
luminance and of alpha, un-premultiplied, normalized so the brightest line core is white, alpha
kept as resampled, transparent texels white — with one extra rule: the source's nearly invisible
texels (alpha < 16) carry arbitrary, often black, RGB, so they count toward coverage but not color;
a texel with no other support takes the drawing's median grey. No texel with real coverage is dark,
so tinted glow edges never pick up a dark fringe. The source's faint cool cast is dropped (the
artwork is stored grey and takes the Component color exactly, like every tintable artwork).

## Astral Gold — authored full-color family

Seven full-color gold, navy and luminous blue drawings, kept as authored (never greyscaled or
tinted). Sources: `AetherFrameAssets/source/CelestialDream/BlueGold/`, all unchanged. Each runtime
PNG was made the same way: contain-fit into its runtime size by a Lanczos resample in float
precision of the alpha-premultiplied color and of alpha, un-premultiplied (RGB kept), placed with
transparent padding where noted, then the RGB of fully transparent texels filled from the nearby
artwork (alpha-weighted averages at growing radii). Every source aspect ratio is kept.

| Component (kind) | Runtime PNG (`Components/AstralGold/...`) | Size | Bytes | Runtime sha256 | Source (sha256) |
|---|---|---|---|---|---|
| Orbital Ring (Plate Frame) | `PlateFrames/OrbitalRing.png` | 1536 x 864 | 1,457,328 | `cb33dfe1…5dbfe` | 1672 x 941 (`0030721e…cc9504`) |
| Crescent Cradle (Portrait Frame) | `PortraitFrames/CrescentCradle.png` | 800 x 1280 | 1,856,935 | `9fd41f24…d832cf` | 992 x 1586 (`f8f9f720…217cbc`) |
| Falling Stardust (Portrait Overlay) | `PortraitOverlays/FallingStardust.png` | 640 x 1024 | 613,196 | `59174d5c…58cb2a` | 1086 x 1448 (`1e75097e…9982d`) |
| Astrolabe Pivot (Corner Ornament) | `CornerOrnaments/AstrolabePivot.png` | 512 x 512 | 277,219 | `e89e508c…946534` | 1254 x 1254 (`82c61481…c691f`) |
| Orbital Constellation Underlay (Name Backing) | `NameBackings/OrbitalConstellationUnderlay.png` | 1536 x 512 | 840,910 | `df845aea…6334d6e` | 2172 x 724 (`629864b4…403a5b`) |
| Equator Line (Divider) | `Dividers/EquatorLine.png` | 1536 x 512 | 356,963 | `52cef713…751473` | 2172 x 724 (`69a8ae90…9614a4`) |
| Star Pinned Underline (Section Header) | `SectionHeaders/StarPinnedUnderline.png` | 1152 x 384 | 363,980 | `f937ca13…40c` | 2172 x 724 (`399e84a3…65fd`) |

**Per-image preparation.**
- *Falling Stardust*: the 3:4 drawing across the full width, top-aligned, transparent below to the
  portrait's 5:8, so it arches over the portrait's top.
- *Astrolabe Pivot*: the source is drawn for the top-right corner; the runtime PNG is it turned a
  quarter counter-clockwise (the top-left drawing the corner placement expects). Rotated, not
  mirrored, so the top-right corner shows the source exactly as authored.

**Where each is drawn.**
- *Orbital Ring*: over the whole Plate (not the procedural frames' inset); 16:9 like the Plate.
- *Crescent Cradle*, *Falling Stardust*: exactly over the portrait (5:8), following its rotation.
- *Astrolabe Pivot*: a 100 px corner square (2.5x the procedural 40 px), rotated per corner.
- *Orbital Constellation Underlay*: centered on the name and title, 1.15x the Name Backing's width
  up to twice its height, painted behind the text.
- *Equator Line*: centered on the Divider's line, in a band 5x the procedural Divider's height (the
  drawing fills only ~40% of its height).
- *Star Pinned Underline*: 2.5x the heading's height, its star medallion just before the heading
  text and its line along the heading's bottom edge (pinned there by `Pivot`, so Scale grows it
  around that point).

**Why these sizes.** On-screen sizes at the default Scale are about the Plate (1280 x 720 logical),
the portrait (400 x 640), a 100 px corner, a ~430 px underlay, a ~350 px divider and a 180 px
heading underline, times `windowHeight / 720` and the Component's Scale. The Plate frame and
portrait frame hold their detail to roughly 1.2x the Plate's reference size (sharp through a
full-height 1440p view); the soft overlay, the 3:1 lines and the corner cover their smaller boxes
with the same margin. Level chains cost about 4/3 of the top level: 7 MB for the Plate frame,
5.5 MB for the portrait frame, 3.5 MB for the overlay, 4.2 MB for each 1536 x 512, 2.4 MB for the
heading underline and 1.4 MB for the corner — each loaded only when a Plate first draws it.
