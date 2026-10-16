# Shaders

HLSL every renderer shares: the binding model and conventions, the surface contract and the engine's surfaces, and a
lighting library of physically based shading. A renderer's own passes (its G-buffer, lighting, post) are written
against these: here, in a folder for the stage they run in (`Debug/` for its debug views), for a renderer that ships with Magic, and in its gem's
`Resources/Shaders/` for any other. How they are compiled, and the rules every shader follows, are in the
[code standards](../../../../CODESTANDARDS.md#hlsl).

## Lighting a pixel

A renderer reads what a surface is made of (from its G-buffer, or straight from `EvaluateSurface` in a forward pass),
then:

```hlsl
float2 dfg = DfgTable.SampleLevel(Clamp, float2(nov, roughness), 0.0).xy;
float sheen = DfgTable.SampleLevel(Clamp, float2(nov, modelData.y), 0.0).z;   // cloth only
Shading shading = PrepareShading(baseColor, metallic, specular, roughness, normal, view, model, modelData, dfg, sheen);

float3 luminance = ShadeLight(shading, DirectionalLight(shading, sunForward, sunLux * sunColor, sunAngularRadius)) * sunShadow;
for each light that reaches the pixel:
    luminance += ShadeLight(shading, SphereLight(shading, toLight, candela * color, radius, invRangeSq)) * shadow;
luminance += ShadeAmbient(shading, ShIrradiance(normal, sh), prefiltered, coatPrefiltered, visibility, specularOcclusion);

sceneColor = luminance * exposure + emissive;   // pre-exposed
```

- **The DFG table** is `IntegrateDfg` at (n·v, roughness), made by a compute pass (32 × 32, RGBA16F, a few hundred
  samples a texel are plenty): the GGX lobe's albedo split into F0 and F90 terms, and cloth's sheen albedo. It makes
  rough metals keep their energy (multiple scattering) under lights and the environment alike.
- **Units.** The sun in lux, point and spot lights in candela (lumens / 4π, and lumens / π for a spot), the sky in nits.
  `ShadeLight` and `ShadeAmbient` answer in nits; exposure, from EV100 (`1 / (1.2 · 2^EV100)`), turns that into the
  pre-exposed scene colour, where emissive surfaces already are.
- **Shading models** (`Include/ShadingModel.hlsli`): default, clear coat, cloth and thin subsurface, picked per pixel by
  a `[branch]` that is uniform across most of a tile.
- **Reflections** come from a cube prefiltered by GGX at roughness `mip / (mips - 1)` (`PrefilteredLevel`), read along
  `SpecularDominantDirection`, box-projected for a local probe; diffuse light from nine spherical-harmonic coefficients.
  Where a renderer traces reflections of the world itself, it uses them in place of the cube's for a surface smooth
  enough to have them.
- **Light from everywhere** is the sky's, from its harmonics, or where a renderer gathers it, global illumination's
  probes (`Lighting/Gi.hlsli`): `GiIrradianceAt` is the irradiance over pi, as `ShIrradiance` is, with how much of it the
  probes hold, the rest the sky's.
- **Occlusion.** `MultiBounceAo` lets a bright surface fill in its creases; `SpecularOcclusionCone` hides the reflections a
  crease's bent normal says are hidden, beyond what the open hemisphere would, `SpecularOcclusion` from visibility alone
  where there is no bent normal.
- **Shadows.** `ShadowPcf` filters a reverse-Z shadow map, a layer of an array or a single map such as an atlas, by a
  5 x 5 tent of comparisons with a sampler that compares GreaterOrEqual; `NormalOffset` keeps a receiver off itself.

## Writing a shader

Read `Include/Bindings.hlsli` first: its header is the conventions. Then:

- A pass's shader, and a surface and its template, read the frame's textures and buffers by name: the renderer
  declares every name the pass can see ahead of the shader, `HAS_Name` defined for each, and binds whichever the shader
  uses, read back from its compile. Only a shader the renderer does not compose binds through the macros itself
  (`READ(n)`, `SAMPLER(n)`, `WRITE(n)`, `UNIFORM(0)`). A written storage texture states its format for Vulkan:
  `[[vk::image_format("rgba16f")]] RWTexture2D<float4> Target : WRITE(0);`.
- A storage buffer is a `ByteAddressBuffer` (`RWByteAddressBuffer` to write), never a structured buffer, whose element
  size a raw view (Direct3D 12's) does not carry, so some GPUs read every element at 0: its elements go through
  `LOAD(Lights, GpuLight, i)` and `STORE`, and an atomic through the buffer's own method, by byte.
- A stage reads at most eight storage buffers, and a compute stage writes at most eight: tables read together are one
  buffer, each part found by an offset, rather than many.
- An integer texture cannot be sampled: it is read as a storage texture, texel by texel (`Load`), with no sampler.
- Include by path under `Shaders/`: `#include "Lighting/Lights.hlsli"`. Every header has a guard and includes what it
  uses.
- HLSL 2021: vector logic goes through `and()`, `or()` and `select()`; `?:` takes a scalar condition. Warnings are
  errors. `sample`, `point`, `line`, `triangle`, `shared` and `uniform` are keywords, so they are not names.
- In a branch, sample with explicit gradients or a level (`SampleGrad`, `SampleLevel`): implicit derivatives are
  undefined there.
- A compute group is 8 × 8 for 2D work, or 64 wide: whole waves on every GPU. Wave intrinsics are fine where the result
  cannot depend on the wave's size.
- Cite the paper a technique comes from in the comment above it, as the library does.
- What only the GPU can find wrong is drawn as the failure, magenta breathing (`Include/Failure.hlsli`'s
  `FailurePixel`), where it is, and raised as a code in the frame's `Failures` buffer (`RaiseFailure`) from a compute
  pass that lists `Failures` among its writes; the renderer says each raised code in words across the view
  (`Include/FailureBanner.hlsli`, in the text `Include/DebugText.hlsli` draws). A new code is a number here and its
  words in the renderer, in the same order.

## Adding a shader

Put it in this folder when what uses it ships with Magic, or else in the `Resources/Shaders/` of the gem that uses it,
in the folder for what it is, with a `.meta` beside it.
