# CelestialSakura

Seven original PNG assets generated in this task and copied unchanged into staging.
Open Preview.html to identify every asset by its final filename.

## Dimensions and transparency

| Filename | Pixels | Transparency |
| :--- | :--- | :--- |
| CelestialSakura_PlateFrame.png | 1672 × 941 | Real alpha; 77.328% fully transparent |
| CelestialSakura_PortraitFrame.png | 992 × 1586 | Real alpha; 77.472% fully transparent |
| CelestialSakura_Nameplate.png | 2172 × 724 | Real alpha; 58.634% fully transparent |
| CelestialSakura_Background.png | 1672 × 941 | Opaque |
| CelestialSakura_Divider_01.png | 2172 × 724 | Real alpha; 83.377% fully transparent |
| CelestialSakura_Divider_02.png | 2172 × 724 | Real alpha; 96.029% fully transparent |
| CelestialSakura_CornerOrnament.png | 1254 × 1254 | Real alpha; 75.934% fully transparent |

## Proportion checks

The committed AetherFrame Classic layout defines a 1280 × 720 reference canvas and a 400 × 640 portrait block. Evidence: master:AetherFrame/Domain/Basic/AdventurePlateClassicLayout.cs and master:AetherFrame/Domain/Profiles/ProfileDocument.cs, inspected without changing repository files.

PlateFrame and Background are 1672 × 941, an approximate 16:9 ratio. Both differ from exact 16:9 by 0.0531%. At this width, exact 16:9 height would be 940.5 pixels. This is half a pixel of rounding at the generated width. They are not exact 1280 × 720 exports.

PortraitFrame is 992 × 1586, approximately 5:8, differing by 0.0757%. At this width, exact 5:8 height would be 1587.2 pixels.

Nameplate and both dividers are 3:1 components; CornerOrnament is square. These are placed within a plate and are not intended as full plate backgrounds.

The exact center pixel of each frame has zero alpha. The central half of each frame has alpha values from 0 to 1 out of 255, so there are trace nearly invisible residual pixels in the opening. Six decorative assets have actual PNG alpha transparency; Background is intentionally opaque. Semitransparent pixels are preserved. Some frame and corner artwork reaches the image boundary, so additional cropping should be avoided.

Generated resolutions differ from prompt requests. No resizing, cropping, recoloring, or alpha modification was performed. All staged PNG hashes match their generated originals. The ZIP is checked against the staged files. This is an art staging set, not an installed plugin bundle; appearance in game has not been tested.

## Files

The folder contains seven CelestialSakura PNGs, Preview.html, README.md, asset_manifest.json, and GenerationPrompts.txt. Divider_01 is ornate with a crescent; Divider_02 is the slim star and blossom variant.
