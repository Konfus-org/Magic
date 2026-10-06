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

That is `Checker.mat`: the PBR surface with a checkerboard texture.

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
| `{ "texture": { "id": 36 } }` | a `TextureRef`: a texture or a render texture |

## The engine's surfaces

| Id | Surface | Params |
| --- | --- | --- |
| 10001 | `Surfaces/Pbr.surf.hlsl` | `color`, `roughness`, `metallic`, `normalScale`, `alphaCutoff`, `emissive`, `colorMap`, `normalMap`, `ormMap` (occlusion, roughness, metallic), `emissiveMap` |
| 10002 | `Surfaces/Unlit.surf.hlsl` | `color`, `colorMap`, `alphaCutoff` |
| 10073 | `Surfaces/Glass.surf.hlsl` | `tint`, `ior`, `thickness`, `dispersion`, `roughness`, `normalScale`, `normalMap` (use with `"type": "transparent"`) |
| 10003 | `Surfaces/Failure.surf.hlsl` | the renderer's own: what anything broken is drawn with |

How to write a surface of your own is "Writing a surface" in the [Shaders README](../Shaders/README.md#writing-a-surface).

## How the renderer uses it

- An entity draws with materials through its `Renderer` component: `"materials": { "slot0": { "id": 710 } }`, up
  to `slot7`. A model's parts name the slot they use (see [Models](../Models/README.md)). A slot with no material
  draws with slot 0's default: the PBR surface with its defaults.
- Every distinct surface (with its `type` and `doubleSided`) is one pipeline, composed by the engine around the
  surface function; every material of that surface is one record in a GPU table, so all of them draw in one indirect
  call. A transparent surface whose code calls `SceneBehind(` is refractive and drawn in the glass passes.
- The surface's `MaterialParams` declaration is read by the renderer, not the compiler: it packs the params into a
  record of at most 128 bytes and 32 fields, and generates the loader the shader uses. Member semantics `GiColor`,
  `GiEmissive` and `GiColorMap` tell the GI which values are the surface's colour and light.

## Lifetime

- Loaded when the first entity using it registers, counted per user, and its slot freed when the last goes.
- Editing the `.mat`, its surface or a texture it samples repacks it live; a changed surface recompiles its pipeline.
- **Failures never stop the frame.** A material whose file is broken, whose shader is missing or does not compile,
  or whose texture does not load keeps its slot but draws with the glowing failure surface, and (in a Debug build)
  a label at the entity says why. Fixing the file repairs it in place.

## What must agree

- `shader` is a `.surf.hlsl`, not a pass shader.
- Each `params` key is a member of the surface's `MaterialParams`, with the form its type takes. An unknown key is
  a warning and is ignored.
- A `TextureRef` param names a texture asset (or `.rtex`); anything else is a texture failure.
- `"type": "masked"` needs a surface that has an alpha cutoff (Pbr and Unlit do).

## Adding a material

Write `Name.mat` anywhere under the project's `Assets/` (the samples keep a domain's materials in
`Domains/<Domain>/Materials/`), or in `Resources/Materials/` for the engine. Name it from a chunk's `Renderer`, or try
it live with `summon Name.mat` in the console: it appears on a cube in front of the camera.
