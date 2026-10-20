# Shaders

HLSL every renderer shares: the binding model and conventions, the surface contract and the engine's surfaces, and a
lighting library of physically based shading. A shader is an asset like any other, an `.hlsl` or `.hlsli` with a `.meta`
beside it, a surface a `.surf.hlsl`. It goes here, in a folder for what it is (`Include/` for the headers, `Lighting/`
for the library, `Surfaces/`, and a renderer's passes in folders for the stage they run in, its debug views in
`Debug/`), when what uses it ships with Magic, and in the `Resources/Shaders/` of the gem that uses it otherwise. The
rules every shader follows, and how they are compiled, are in the
[code standards](../../../../CODESTANDARDS.md#shaders).

## Lighting a pixel

A renderer reads what a surface is made of, from its G-buffer or straight from `EvaluateSurface` in a forward pass, and
hands it to `Shade` (`Lighting/Shade.hlsli`) as a `ShadePoint`, which answers the light the surface sends toward the
viewer, in nits. `Shade` looks the pixel up in the DFG table at (n·v, roughness), prepares a `Shading` for the surface's
shading model (`PrepareShading`), adds the sun through its shadow (`DirectionalLight`, `ShadeLight`, `SunShadow`) and
each local light that reaches the pixel (`SphereLight`, walked from the frame's light bins and tiles), then light from
everywhere (`ShadeAmbient`): the sky's harmonics, or the probes of global illumination where they hold the place, for
diffuse, and the sky prefiltered by roughness for reflections, or the world's traced reflections where the renderer has
them, less what occlusion hides. The renderer multiplies by exposure and adds the surface's emissive, which is
pre-exposed already. The pieces:

- **The DFG table** (`Dfg`, sampled with `DfgSampler`) is `IntegrateDfg` at (n·v, roughness), made by a compute pass
  (32 × 32, RGBA16F, a few hundred samples a texel are plenty): the GGX lobe's albedo split into F0 and F90 terms, and
  cloth's sheen albedo. It makes rough metals keep their energy (multiple scattering) under lights and the environment
  alike.
- **Units.** The sun in lux, point and spot lights in candela (lumens / 4π, and lumens / π for a spot), the sky in nits.
  `ShadeLight` and `ShadeAmbient` answer in nits; exposure, from EV100 (`1 / (1.2 · 2^EV100)`), turns that into the
  pre-exposed scene colour, where emissive surfaces already are.
- **Shading models** (`Include/ShadingModel.hlsli`): `ShadingDefault` (metallic-roughness), `ShadingClearCoat` (coat,
  coat roughness), `ShadingCloth` (sheen, sheen roughness) and `ShadingSubsurface` (transmission, wrap), a surface's
  `shadingModel` with what the model takes in its `modelData`, picked per pixel by a `[branch]` that is uniform across
  most of a tile.
- **Reflections** come from a cube prefiltered by GGX at roughness `mip / (mips - 1)` (`PrefilteredLevel`), read along
  `SpecularDominantDirection`, box-projected for a local probe; diffuse light from nine spherical-harmonic coefficients.
  Where a renderer traces reflections of the world itself, it uses them in place of the cube's for a surface smooth
  enough to have them.
- **Light from everywhere** is the sky's, from its harmonics, or where a renderer gathers it, global illumination's
  probes (`Lighting/Gi.hlsli`): `GiIrradianceAt` is the irradiance over pi, as `ShIrradiance` is, with how much of it
  the probes hold, the rest the sky's.
- **Occlusion.** `MultiBounceAo` lets a bright surface fill in its creases; `SpecularOcclusionCone` hides the
  reflections a crease's bent normal says are hidden, beyond what the open hemisphere would, `SpecularOcclusion` from
  visibility alone where there is no bent normal.
- **Shadows.** `ShadowPcf` filters a reverse-Z shadow map, a single `Texture2D` such as an atlas whose tiles keep their
  taps inside them, by a 5 × 5 tent of comparisons gathered from nine bilinear taps with a sampler that compares
  GreaterOrEqual; `NormalOffset` keeps a receiver off itself.

## Writing a shader

Read `Include/Bindings.hlsli` first: its header is the conventions, and the
[code standards](../../../../CODESTANDARDS.md#shaders) the rules. What is specific to a shader asset:

- **How a pass finds it.** A `.pass` names its shader by id, and the kind of shader is its extension: a compute pass's
  `.comp.hlsl`, a fullscreen pass's `.frag.hlsl`, or a depth draw's `.vert.hlsl`. A draw of surfaces names none: the
  renderer composes its shaders around the surface each material names.
- **Reading the frame by name.** A pass's shader, and a surface and its template, read the frame's textures and buffers
  by name: the renderer declares every name the pass can see ahead of the shader, with `HAS_Name` defined for each, so
  a shader guards what a frame may not have (`#if defined(HAS_AmbientOcclusion)`) and does without it, and binds
  whichever the shader uses, read back from its compile. Only a shader the renderer does not compose binds through the
  macros itself.
- **Includes** name a path under `Shaders/`, `#include "Lighting/Lights.hlsli"`: the asset at that path, from whichever
  root has it, so a project's or a gem's header is included the same way. Every header has a guard and includes what
  it uses.
- **An integer texture** cannot be sampled: it is read as a storage texture, texel by texel (`Load`), with no sampler.
- **A compute group** is 8 × 8 for 2D work, or 64 wide: whole waves on every GPU.
- **Failures.** What only the GPU can find wrong is drawn as the failure, magenta breathing (`Include/Failure.hlsli`'s
  `FailurePixel`), where it is, and raised as a code in the frame's `Failures` buffer (`RaiseFailure`) from a compute
  pass that lists `Failures` among its writes; the renderer says each raised code in words across the view
  (`Include/FailureBanner.hlsli`, in the text `Include/DebugText.hlsli` draws). A new code is a number here and its
  words in the renderer, in the same order.
