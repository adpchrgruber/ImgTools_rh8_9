# Changelog

## 1.3.0-beta — 2026-11-26

- **Normal pass.** `NormalCapture` saves each visible point's smooth surface normal as colour,
  in camera space (OpenGL green-up or DirectX green-down) or world space, as an 8-bit or
  16-bit RGB PNG or a float EXR (R, G, B = normal, A = coverage). `NormalView` and a toolbar
  button show it live; its options are in the options window (right-click Normal View).

## 1.2.0-beta — 2026-11-26

- **Live Ambient Occlusion view.** `AOView` and an **AO View** toolbar button switch a viewport
  to the new *Ambient Occlusion* display mode, traced live like the Shadows view.
- **Options window.** Right-click the Shadow View or AO View button (or run `ImgToolsOptions`)
  for every Shadows and AO option: light, cast/form, softness, rays, colours, AO radius,
  anti-aliasing. Changes show at once in the live views and are used by the captures.
  AOCapture's options are now remembered too; the AO radius is saved with the document.
- **Faster tracing.** Packed BVH, nearest-first traversal and adaptive soft-shadow sampling:
  soft shadows about 2× faster, AO about 12% faster, same images.
- **Fix:** the live views showed nothing on Rhino 8 for Mac (its viewport draws bitmaps from
  their bottom edge).

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
