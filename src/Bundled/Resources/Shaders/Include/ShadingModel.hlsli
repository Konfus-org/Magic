// The shading models a surface can ask for (Surface.shadingModel), and what each makes of Surface.modelData. A
// renderer keeps a pixel's model with it (two bits of a G-buffer) and lights it by Lighting/Lights.hlsli.
//
//   ShadingDefault     metallic-roughness: a Lambert diffuse and a GGX specular lobe. modelData unused.
//   ShadingClearCoat   a second, smooth dielectric lobe over the default ones: car paint, lacquer, varnish.
//                      modelData = (coat 0..1, coat roughness).
//   ShadingCloth       the default lobes plus a sheen that brightens grazing angles: velvet, fabric.
//                      modelData = (sheen 0..1, sheen roughness); the sheen is tinted by sqrt(baseColor).
//   ShadingSubsurface  thin translucency: light wraps past the terminator and passes through from behind,
//                      tinted by the base colour: leaves, paper, thin cloth.
//                      modelData = (transmission 0..1, wrap 0..1).

#ifndef MAGIC_SHADING_MODEL_HLSLI
#define MAGIC_SHADING_MODEL_HLSLI

static const uint ShadingDefault = 0u;
static const uint ShadingClearCoat = 1u;
static const uint ShadingCloth = 2u;
static const uint ShadingSubsurface = 3u;

#endif
