# Materials

What a surface looks like: a `.mat` file names a surface shader and gives values to the parameters it declares.

```json
{
    "shader": { "id": 10001 },
    "params": {
        "color": { "r": 1, "g": 1, "b": 1, "a": 1 },
        "roughness": { "x": 0.8 },
        "colorMap": { "texture": { "id": 36 } }
    }
}
```

| Field | Default | What it is |
| --- | --- | --- |
| `shader` | none | `{ "id" }` of a surface shader, a `.surf.hlsl`. |
| `type` | `opaque` | `opaque`, `masked` (pixels below the surface's alpha cutoff are discarded) or `transparent` (blended, drawn after everything opaque). |
| `doubleSided` | false | Draw the back faces too. |
| `params` | `{}` | Values by the surface's `MaterialParams` member names. One left out takes the shader's default. |

A param is one of:

| Form | For |
| --- | --- |
| `{ "x": 0.8 }` | a `float`, `int`, `uint` or `bool` (non-zero is true) |
| `{ "x": 1, "y": 0, "z": 0, "w": 0 }` | a vector (`float2`..`float4`, `int2`.., `uint2`..) |
| `{ "r": 1, "g": 0.5, "b": 0, "a": 1 }` | a `Color` (linear) |
| `{ "texture": { "id": 36 } }` | a `TextureRef`: a texture |
| `{ "renderTexture": { "id": 740 } }` | a `TextureRef`: a render texture, which a camera draws into |

What a material can set is its surface's `MaterialParams` declaration, so the surface is where to look.

## Writing a surface

A surface is written against the contract, [Shaders/Include/Surface.hlsli](../Shaders/Include/Surface.hlsli), and
nothing else: no bindings, no frame block, so any renderer can draw it. It says what it is made of and never lights
itself: the renderer lights it, its reflections included ([Shaders](../Shaders/README.md#lighting-a-pixel)).

```hlsl
#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color : GiColor = Color(0.8, 0.8, 0.8, 1.0);
    TextureRef colorMap : GiColorMap;
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);

    float4 color = material.color;
    [branch] if (HasTexture(material.colorMap))
    {
        color *= SampleTexture(input, material.colorMap);
    }

    surface.baseColor = color.rgb;
    surface.alpha = color.a;
    return surface;
}
```

Defaults and `Gi*` roles (which members are the surface's colour, how metal it is and its light, for global
illumination) are read by the renderer and stripped before compilation. `SampleTexture(input, texture)` samples at the
mesh uv; for any other uv pass its own derivatives to `SampleTextureGrad`, which the renderer defines.
`SampleNormal(input, map, scale)` reads a normal map into world space, rebuilding z, so two-channel (BC5) maps serve as
well as three-channel ones, through the vertex frame as MikkTSpace bakers expect.

What a surface hands back (`Surface`):

| Member | |
| --- | --- |
| `baseColor`, `alpha` | Linear colour and coverage. |
| `normal` | World space, unit length. |
| `roughness`, `metallic` | Perceptual roughness 0..1, and how metallic. |
| `specular` | A dielectric's reflectance: 0.5 is 4 %, what most non-metals reflect; 0 turns reflection off (Unlit does). |
| `occlusion` | Baked or authored ambient occlusion. |
| `emissive` | Light given off, pre-exposed: 1 reads as white whatever the lighting. |
| `alphaCutoff` | Below it, a masked material's pixel is discarded. |
| `shadingModel`, `modelData` | Which shading model lights it, and what that model takes ([Shaders](../Shaders/README.md#lighting-a-pixel)). |

`SurfaceInputs.viewDirection` is the unit direction from the surface toward the camera. A transparent surface is
compiled with `SURFACE_FORWARD` 1 and may read what was drawn behind it, pre-exposed (`ScenePixelOf`,
`SceneBehind(pixel, roughness)`, blurred by roughness), as `Glass.surf` does: it emits what it lets through, and the
renderer adds its reflection at the reflectance its index of refraction gives.

## What must agree

- `shader` is a `.surf.hlsl`, not a pass shader.
- Each `params` key is a member of the surface's `MaterialParams`, with the form its type takes.
- A `TextureRef` param names a texture under `texture` and a `.rtex` under `renderTexture`.
- `"type": "masked"` needs a surface that has an alpha cutoff.

## Adding a material

Write `Name.mat` anywhere under the project's `Assets/`, or in this folder for the engine, and name it from an
entity's `Renderer`, in the slot its model's mesh names ([Models](../Models/README.md)).
