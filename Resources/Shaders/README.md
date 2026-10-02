# Shaders

HLSL, compiled at run time by the renderer gem (DXC to SPIR-V through SDL_shadercross, then on to DXIL for
D3D12). The engine conventions every shader is written against (handedness, winding, reverse-Z, matrix
order, colour space) are in the header of [Include/Bindings.hlsli](Include/Bindings.hlsli); this file is how
the shaders are laid out and written.

## Layout

| Folder | What is in it |
| --- | --- |
| `Include/` | Headers. `Bindings` (registers), `Frame` (the one constant block), `Structs` (GPU tables, mirrored in `GpuStructs.cs`), `Surface` (the surface shader contract), `Pass` (the data pass contract), `Instance`, `MeshVaryings`, `Math`. |
| `Templates/` | The mesh path the renderer composes around a surface: `Mesh.vert`, `Forward.frag`. |
| `Surfaces/` | Surface shaders (`.surf.hlsl`): what a material looks like. `Failure.surf` is what anything broken is drawn with. |
| `Cull/` | The GPU culling compute passes and what they share (`Common.hlsli`). |
| `Passes/` | Data passes (`.pass` assets): the fullscreen vertex shader, tonemap, vignette. |
| `Overlay/` | 2D overlays (ImGui). |
| `Test/` | Start-up probes of the conventions (`RenderChecks.cs`). |

The frame runs them in this order: `PageCull` → `CullEarly` → draw → (with occlusion) `HiZBuild` →
`SeedLateArgs` → `CullLate` → draw → passes → overlays.

## Rules that keep shaders correct

These are the ones that break silently when ignored. Most were learned the hard way.

- **Declare resources through `READ` / `SAMPLER` / `WRITE` / `UNIFORM`.** Never write a register or a space
  by hand. `READ(n)` numbers run across sampled textures, then storage textures, then storage buffers, in
  that order.
- **One constant block per stage, at `UNIFORM(0)`.** DXC drops a block nothing reads and SDL needs the
  blocks a stage uses to be consecutive from 0. Everything per-frame lives in `Include/Frame.hlsli`; a
  header that needs more appends to it with `FRAME_APPEND`.
- **A GPU struct changes together with its C# twin** in `Core/Contexts/Rendering/GpuStructs.cs`. Both sides
  read the same bytes blind. Storage-buffer structs are built from 16-byte members; the constant block is
  built from 16-byte rows (a `float3` with a scalar, two `float2`, four scalars).
- **Varyings shared by composed or generated shaders live in one header** (`MeshVaryings`, `PassVaryings`).
  On the DXIL path the fragment inputs must line up with the vertex outputs register for register. A
  self-contained pair (overlay, test triangle) may declare matching `VsOut` / `PsIn`, and says so in its
  header comment.
- **Never drop `KeepBindingsAlive` from the forward template.** It keeps every texture pool and every
  interpolant referenced, whatever a surface reads; without it bindings shift and pipelines fail to link on
  D3D12.
- **No implicit-derivative `Sample` inside a branch or loop.** Take `ddx` / `ddy` outside and use
  `SampleGrad` (what `SampleTexture` does), or `SampleLevel` when no filtering across mips is wanted.
  Fullscreen passes use `SampleLevel(..., 0.0)`: texels map one to one, and the code then also works in a
  compute pass.
- **A function has one exit when it samples or writes inside control flow.** Assign a result and return it
  at the end; a `return` inside a `switch` that samples has been miscompiled by older DXC builds. Guard
  clauses at the top of a compute `main` (bounds, dead slots) are fine.
- **No float equality, no `fmod` for patterns.** Convert to integers and test bits.
- **`normalize` only what cannot be zero.** Use `NormalizeOrZero` for anything that comes from mesh data.
- **Failures are decided on the CPU.** A broken material, texture or model is routed to
  `Surfaces/Failure.surf.hlsl` by the renderer. Shaders never test for failure and hold no mutable globals.
- **Compile-time variants over uniform flags** when the CPU already knows the answer for the pipeline's
  whole life (`SURFACE_MASKED`, `OCCLUSION`, `FAILURE_FORCE`). A variant then declares only the resources it
  uses.
- **Verify on D3D12 and Vulkan.** Vulkan tolerates unused bindings and inputs that D3D12 rejects. Run the
  Fallbacks, Grid and Wall samples; a Debug build turns on the debug layer, the probes and the cull check.

## Style

**Naming**

| What | Style | Example |
| --- | --- | --- |
| Types, functions | `PascalCase` | `SurfaceInputs`, `SphereInFrustum` |
| Globals: resources, constant-block members, `static const` | `PascalCase` | `Materials`, `ViewSize`, `PulsePeriod` |
| Struct members, parameters, locals | `camelCase` | `worldPosition`, `levelOffset` |
| Macros | `UPPER_SNAKE` | `SURFACE_MASKED`, `READ(n)` |
| Booleans | `is` / `has` prefix, or a predicate function | `isMirrored`, `HasTexture` |

- A sampler is named after its texture: `Depth` and `DepthSampler`.
- GPU table rows are `Gpu*` (`GpuInstance`, `GpuMaterial`). Stage I/O is `VsIn`, `VsOut`, `PsIn`, or a named
  varyings struct.
- Names say what the value is. No single letters beyond a loop counter and the coefficients of a published
  formula; no Hungarian prefixes; no names that shadow an intrinsic (`step`, `point`, `pass`).
- Entry points are `main`. A fragment `main` takes one varyings struct (plus system values such as
  `SV_IsFrontFace` as separate parameters).

**Constants**

- A named constant is `static const`. A macro is only for what the preprocessor or an attribute needs: include
  guards, register macros, variant switches, `numthreads` sizes and array dimensions.
- No magic numbers. A number that must agree with another shader or with C# is named once, in the header
  both sides include, with a comment naming its twin.

**Literals and types**

- Floats carry a decimal point (`1.0`), unsigned integers a `u` (`0u`, `1u << 2`).
- Build vectors with constructors (`float3(0.0, 0.0, 0.0)`), not scalar assignment.
- Conversions between float, int and uint are written as casts.

**Control flow**

- `[branch]`, `[loop]` or `[unroll]` on every branch and loop that depends on data. Plain `if` is for guard
  clauses.
- Prefer a select (`condition ? a : b`) to a branch for a single value.

**Files**

- Order: header comment, blank line, includes, macros, constants, resources, structs, functions, `main`.
- The header comment says what the file is for and anything a reader must know before changing it.
  Comments explain why, not what.
- Every header has an include guard `MAGIC_<NAME>_HLSLI` and includes what it uses. Includes are
  root-relative and sorted.
- 120 columns. Four spaces. Braces on their own lines. One declaration per line, not aligned into columns
  (trailing comments in a block may line up).

## Writing a surface

```hlsl
#include "Include/Surface.hlsli"

struct MaterialParams
{
    float4 color : GiColor = float4(0.8, 0.8, 0.8, 1.0);
    TextureRef colorMap : GiColorMap;
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);

    float4 color = material.color;
    [branch] if (HasTexture(material.colorMap))
        color *= SampleTexture(input, material.colorMap);

    surface.baseColor = color.rgb;
    surface.alpha = color.a;
    return surface;
}
```

Defaults and `Gi*` roles are read by the renderer and stripped before compilation. `SampleTexture(input,
texture)` samples at the mesh uv; for any other uv pass its own derivatives to `SampleTextureGrad`.

## Writing a pass

```hlsl
#include "Include/Pass.hlsli"

struct PassParams
{
    float strength = 0.4;
};

float4 main(PassVaryings input) : SV_Target0
{
    PassParams passParams = LoadPassParams();
    float4 color = Ldr.SampleLevel(LdrSampler, input.uv, 0.0);
    return float4(color.rgb * (1.0 - passParams.strength), color.a);
}
```

Each input named in the `.pass` file arrives as a `Texture2D` with a `<Name>Sampler`.

A pass runs only when it is listed. Post-processing is global, not a camera's: one entity of its own carries a
`PostProcessing` component whose `passes` are applied in the order written, over every render target.

```json
{ "name": "PostProcessing", "components": { "PostProcessing": { "passes": [ { "id": 10030 }, { "id": 10031 } ] } } }
```

- Nothing is implied, the tonemap included: without the component the linear scene is shown.
- Up to 8 passes. Of several entities carrying the component, the first is followed and a warning is logged.
- A pass is loaded while it is listed and unloaded, with the targets it made, when it is not.
- An input must be `Hdr`, `Ldr`, `Depth` or the output of a pass earlier in the same list.
