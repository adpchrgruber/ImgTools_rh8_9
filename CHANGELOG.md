# Changelog

## 1.1.0-beta — 2026-11-26

- **Live Shadows view.** `ShadowView` (and a toolbar button) switches a viewport to the new
  *Shadows* display mode, which shows the shadow pass live. It is traced in the background
  with the same code and options as `ShadowCapture`: a quick image while you orbit, then
  full resolution when the camera stops. It updates when the model, sun, lights or options
  change.
- `ShadowCapture` options are now remembered between Rhino sessions and shared with the view.

## 1.0.0-beta — 2026-11-26

First beta of **ImgTools**, which replaces the separate **ClownID** (0.9.3-beta) and **ZDepth**
(0.9.1-beta) packages.

- **One package** with the Clown ID and Z Depth passes, unchanged: `ClownID`,
  `ClownIDCapture`, `ZDepth`, `ZDepthCapture`, `ZDepthRange`. One **ImgTools** toolbar.
- **New: `ShadowCapture`**, the raw shadows of the sun and/or the document's lights as a
  greyscale mask, ray traced on the CPU so it is exact at any size. Hard shadows, or soft ones
  with `Softness` (the light's size in degrees) for penumbras that blur with distance. Respects
  *Casts/Receives shadows*, clipping planes, spot cones, and casters outside the frame.
- **New: `AOCapture`**, an ambient occlusion pass with an adjustable radius and smooth falloff.
- Uninstall ClownID and ZDepth before installing ImgTools: the command names are the same.
- Tested on Rhino 9 for Mac.

## Earlier

For the history of the Clown ID and Z Depth passes, see the changelogs of
[ClownID](https://github.com/adpchrgruber/ClownID_Rh8) and
[ZDepth](https://github.com/adpchrgruber/ZDepth_Rh8_Mac).
