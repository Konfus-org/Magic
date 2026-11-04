# The Render Pipeline

Nov 3, 2026

A plan for Magic's renderer, written from scratch. Nothing of the original renderer or its shaders is kept. The
engine it plugs into is the one [PLAN.md](PLAN.md) describes: gems as step functions, the frame as a value, state in
four places, every slow thing an `Async` method, results slow to make in the cache.

## What it must do

| Requirement                                     | How                                                                                                                           |
| ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| GPU driven                                      | The CPU uploads tables; the GPU culls instances and clusters, picks detail, builds its own draw lists and draws them           |
| Infinite draw distance, no pop in               | A continuous cluster hierarchy picked by screen error down to a root of a dozen triangles, reverse float depth, camera relative |
| Sharp shadows that cast forever                 | Virtual shadow maps: a clipmap of pages for the sun, cube pages for local lights, pages cached between frames                   |
| Dark interiors, no bleeding                     | Distance-field traced global illumination with a surface cache; occlusion is a real trace, never an interpolated guess          |
| Light through glass                             | A translucent page layer in the shadow maps and a transmittance volume the GI rays walk through                               |
| A lot of lights                                 | Clustered light lists on the GPU; shadows virtual for the few that earn it, distance-field traced for the rest                 |
| Believable reflections, nothing only on screen  | Screen traces first for detail, world traces through the distance field with surface-cache lighting after, probes last        |
| Realtime, no baking                             | Every lighting term is computed each frame from the live world; what the import step generates is geometry, never light        |
| Models through assimp, shaders in Slang         | One loader gem, one compiler gem, both behind Core interfaces                                                                 |
| Easy to extend                                  | A material, a light, a sky, a post effect and a world tracer are each a Slang interface; the frame is a pipeline asset         |
| Vulkan apart from the pipeline                  | `IGraphicsApi` is handles, descriptions and a command list value; the renderer never sees a Vulkan type                        |
| Cross platform                                  | Vulkan 1.3 as MoltenVK and KosmicKrisp expose it is the baseline; mesh shaders and ray queries are optional paths              |
| Auto-generated derived data                     | A post-processor on `Model` makes clusters and distance fields into the cache, keyed by the asset's version; cards at runtime  |

Two things this plan refuses. **No lighting is baked**: the cache holds geometry-derived data only (clusters, distance
fields, the cards' albedo, normal and depth), and a static scene lit twice from two different
suns gives two different pictures. **No technique is only screen space**: a screen trace may shortcut a world trace, but
every term has a world-space source that works when the thing it needs is behind the camera.

## The shape

```text
Core    IGraphicsApi    IShaderCompiler    IRendering  IRenderPipeline    IAssetLoader<Model>    ILoadPostProcessor<Model>
             ▲                ▲                ▲              ▲                   ▲                        ▲
gems  VulkanGraphics ┘  SlangShaders ┘  WorldRenderer ┴──────┘        AssimpModels ┘        MeshPreprocessor ┘
```

Six gems, each talking to the others only through Core:

| Gem                | Provides                                          | Takes                                                                       | Native                   |
| ------------------ | ------------------------------------------------- | --------------------------------------------------------------------------- | ------------------------ |
| `VulkanGraphics`   | `IGraphicsApi`                                    | settings, `Threads`, `IWindow?`                                             | Vortice.Vulkan, SDL3     |
| `SlangShaders`     | `IShaderCompiler`, `ICache<CompiledShader>`       | `Assets`, `Caches`, settings                                                | slang                    |
| `AssimpModels`     | `IAssetLoader<Model>`                             | nothing                                                                     | assimp                   |
| `MeshPreprocessor` | `ILoadPostProcessor<Model>`, `ICache<MeshDerived>` | `Assets`, `Caches`, `Threads`, settings                                     | meshoptimizer            |
| `WorldRenderer`    | `IRendering`, `IPreloader`, `ICache<GpuTexture>` | `IGraphicsApi`, `IShaderCompiler`, `Assets`, `Caches`, `World`, `Threads`, `App`, settings, `IWindow?`, `IScripts` | bc7enc |
| `SdlPlatform`      | already planned; the window and input             |                                                                             |                          |

Core gains, in `Contexts/Gpu/` and `Contexts/Rendering/`, the plain data both sides share: handles, descriptions,
formats, the command list, the render world, the components (`Renderer`, `Camera`, `DirectionalLight`, `PointLight`,
`SpotLight`, `RectLight`, `Sky`, `Fog`, `PostProcessing`) and the asset types (`Model`, `Material`, `Texture`,
`Shader`, `RenderPipeline`, `PostEffect`). About 2,000 lines, all records and structs.

## `IGraphicsApi`

The GPU as a value-passing machine. A gem never calls into Vulkan; it makes resources from descriptions, records a
command list as data, and hands the list over once per frame.

```csharp
public interface IGraphicsApi
{
    GpuFeatures Features { get; }                      // what is optional and present: MeshShaders, RayQuery, Atomic64
    GpuLimits Limits { get; }                          // the device's name, heap size, alignments, memory
    GpuTexture WindowTarget { get; }                   // the swapchain image this frame, none when headless
    GpuTimings Timings { get; }                        // the GPU's own timing of the frame finished two frames ago

    Result<GpuBuffer> CreateBuffer(in BufferDescription description);
    Result<GpuTexture> CreateTexture(in TextureDescription description);
    Result<GpuSampler> CreateSampler(in SamplerDescription description);
    Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description);
    Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description);
    void Destroy(GpuBuffer buffer);                    // deferred until no frame in flight uses it; same for the rest

    GpuUpload BeginUpload(int bytes);                  // a span in this frame's staging ring
    Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(GpuBuffer buffer, long offset, int bytes, CancellationToken cancel);

    void Submit(in GpuCommands commands);              // once per frame, on the main thread
}
```

- **Handles are heap indices.** `GpuBuffer`, `GpuTexture`, `GpuSampler` are 32-bit ids into one bindless heap, the
  same number the shader reads. A `GpuTexture` carries a second id per view it was asked for (storage, a mip, an
  array slice). The driver binds one descriptor set for the whole frame and never another.
- **`GpuCommands` is a value.** An `ImmutableArray<GpuPass>`, each pass a name, its kind (compute, draw, copy), the
  `GpuTransition`s it needs first and the commands inside: bind pipeline, push constants (one 16-byte block: a heap
  index to the pass's constants buffer plus three scalars), dispatch, dispatch indirect, draw indexed indirect count,
  copy, clear, timestamp, begin and end debug group. Recording goes through a `GpuCommandRecorder` ref struct that
  builds the arrays; the renderer's graph compiler is what calls it.
- **Transitions are the caller's.** A transition names a resource, the access before and the access after, as
  API-neutral `GpuAccess` values (`ShaderRead`, `ShaderWrite`, `ColorTarget`, `DepthTarget`, `CopySource`, `Indirect`,
  `Present`). The driver maps them to `vkCmdPipelineBarrier2` with exact stage and access masks and the image layout
  each needs, keeping each texture level's layout as the transitions move it. Putting the planning in the render graph
  keeps the driver a translator and makes the plan a pure function a test can check.
- **Graphics pipelines use dynamic rendering**, no render pass objects. A `GraphicsPipelineDescription` is the SPIR-V
  per stage, the vertex layout (none for the cluster path: vertices are fetched), the target formats, depth and blend
  state, cull mode and topology. The viewport and scissor are commands.
- **Frames in flight are two.** The driver owns a staging ring per frame, a deferred-destroy queue per frame and the
  timestamp pool; the frame begins by waiting on the fence from two frames ago and nothing else.
- **One queue.** Async compute is a later optimisation the graph is shaped for (pass kinds and declared accesses are
  what a scheduler needs) and the interface never exposes.
- **Headless** runs the same code with no swapchain; `WindowTarget` is null and the pipeline draws into a texture the
  screenshot command reads back.

The Vulkan 1.3 features the gem requires and checks at device pick: `dynamicRendering`, `synchronization2`,
`shaderDemoteToHelperInvocation` (a pixel shader's `discard`),
`descriptorIndexing` with update-after-bind and partially bound, `bufferDeviceAddress`, `drawIndirectCount`,
`shaderDrawParameters`, `samplerFilterMinmax` where present, 32-bit image atomics on `R32_UINT`, `timelineSemaphore`,
`multiDrawIndirect`, `fillModeNonSolid` (debug views). Optional and probed: `VK_EXT_mesh_shader`,
`VK_KHR_ray_query` with acceleration structures, `shaderBufferInt64Atomics`, `VK_KHR_fragment_shading_rate`. The
baseline set is what MoltenVK on Apple silicon and KosmicKrisp report today; verifying it on a Mac is the first task of
phase 0, and a feature that turns out missing there is demoted to optional.

## Shaders

Slang, one module per file, read through the heap, generic over the contracts. The library lives in
`src/Bundled/Resources/Shaders/` and every renderer and every game shader builds on it.

```text
Core/      Heap, Frame, Math, Color, Pack, Sampling, Random, Wave
Scene/     Instances, Clusters, Vertices, Materials, Lights, Cameras, Views (the GPU tables and how to read them)
Surface/   ISurface, SurfaceInputs, SurfaceOutputs, Brdf, ShadingModels
Material/  IMaterial, Standard, Glass, Failure
Light/     ILight, Directional, Point, Spot, Rect (LTC), Units
Shadow/    IShadow, Virtual, DistanceField
Sky/       ISky, Atmosphere, Procedural
Trace/     IWorldTracer, DistanceField, RayQuery, Screen
Gi/        SurfaceCache, RadianceCache, ScreenProbes, Integrate
Post/      IPixelEffect, Exposure, Bloom, Tonemap, Taa
```

The contracts a game or a gem implements:

```slang
interface IMaterial    { SurfaceOutputs Evaluate(SurfaceInputs inputs); }     // a visibility-buffer pixel to a surface
interface ILight       { LightSample Sample(ShadingPoint point, float2 xi); }  // radiance, direction, distance, pdf
interface ISky         { float3 Evaluate(SkyInputs inputs); }                  // radiance along a direction
interface IPixelEffect { float4 Apply(EffectInputs inputs, float4 color); }    // one post effect, fused with the others
interface IWorldTracer { TraceHit Trace(Ray ray, TraceLimits limits); }        // the distance field, or a ray query
interface IShadow      { float Evaluate(ShadowInputs inputs); }                // virtual pages, or a cone through the field
```

- A pass that works with any is generic over it and specialised when compiled: `ResolveMaterial<M : IMaterial>`,
  `ShadeLighting<S : ISky>`, `ApplyEffects<E0, E1, E2 : IPixelEffect>`, `TraceProbes<T : IWorldTracer>`. The compiler
  gem compiles one specialisation per combination the frame uses and caches each under the hash of its sources and
  type arguments. A game adds a material by writing one `.slang` file that implements `IMaterial` and naming it in a
  `.material` asset; nothing in C# changes.
- Constants reach a pass as one struct in a buffer whose heap index is the push constant. The struct's C# twin is
  `[ShaderStruct]` and the renderer checks layout against reflection at start, so a mismatch is a start-up error with
  both layouts printed, never a corrupt frame.
- Buffers are `StructuredBuffer<T>` and `RWStructuredBuffer<T>` reached through typed heap references
  (`BufferRef<GpuInstance>`), textures through `TextureRef<float4>`; the index of every reference is clamped to the
  heap size before use.
- No 16-bit types, no wave-size-dependent results, no implicit derivatives outside a draw; everything a compute pass
  needs comes through `SurfaceInputs` as explicit gradients from the visibility buffer's analytic derivatives.
- Every technique names its paper in a comment at the top of its module. No third-party shader code.

## The frame

```text
main thread                                          render thread
───────────────────────────────────────────────────  ───────────────────────────────────────────────────────────────
ECS ─Step─▶ RenderWorld (instances, lights, cameras, Render(frame, state):
            sky, fog, post, overrides; a value)        1 diff RenderWorld against the GPU tables → uploads
                            │                          2 run the IRenderPipeline script → RenderGraph (passes, resources)
                            └──── FrameOutput ─────▶   3 compile the graph → GpuCommands (transitions, aliasing; pure)
                                                       4 IGraphicsApi.Submit

GPU, as the default pipeline orders it
───────────────────────────────────────────────────────────────────────────────────────────────────────────────────
Update        instance and cluster tables · light grid · distance-field clipmaps · transmittance volume · captures
Shadows       VSM: mark pages from last depth → allocate → cull clusters per page → raster opaque, then glass pages
Visibility    cull instances → cull clusters, pick detail, test last HZB → raster R32 ids + reverse depth → HZB →
              retest what the old HZB hid, raster the rest
Materials     classify 8×8 tiles by material → one resolve dispatch per material → GBuffer + motion vectors
Surface cache direct light on the cards (all lights, VSM) → radiosity on the cards traced through the field → lit atlas
GI            radiance-cache probes trace the field, shade from the cards → screen probes trace short, gather long →
              filter → integrate
Reflections   screen trace → field trace with card lighting → probe fallback → denoise
Lighting      clustered direct (VSM or field shadows) + GI + reflections + sky + volumetric fog → HDR
Translucency  forward draw of glass clusters: lit the same way, refracting the HDR image, resolved order independent
Post          TAAU → exposure → bloom → fused effects → tonemap → overlay → window
```

### The world copy

`Step` runs queries over the ECS and produces a `RenderWorld`: `ImmutableArray<RenderInstance>` (entity, model handle,
material handles per slot, world transform as a `Matrix4x4` plus a `double3` origin, flags: casts shadows, translucent,
static), the lights, every camera, the sky, the fog, the post settings. The GPU tables keep stable slots keyed by
entity, so a frame's work is the diff: added, moved, removed. A static instance that has not moved costs no upload and
keeps its shadow pages and surface-cache lighting.

Positions on the GPU are relative to the camera. The instance origin is kept in double precision on the CPU, the
camera's origin is subtracted there, and the matrix uploaded is float with a small translation. With a reverse-Z
`D32_SFLOAT` depth and an infinite far plane, a model 50 km away draws without z-fighting against one 5 m away.

### Geometry: clusters

Every model draws as clusters of up to 128 triangles and 128 vertices in a hierarchy the preprocessor built:

- **Leaves** are the mesh's own triangles, clustered by `meshopt_buildMeshletsFlex` with spatial bounds.
- **Parents** come from grouping 8 to 16 adjacent clusters (`meshopt_partition`), simplifying the group's merged
  triangles to half with the group's border locked (`meshopt_simplifyWithAttributes`, `LockBorder`), and reclustering
  the result. Each group records the error the simplification introduced, made monotonic up the tree by taking the
  max with the children, and the bounding sphere the error applies to. This is the DAG from Karis, "Nanite: A Deep
  Dive", 2021; the cut through it is chosen per cluster with no knowledge of its neighbours, so neighbouring clusters
  at different levels never crack because their shared border was locked when either was made.
- **The root** is the last level simplified once more without regard to its topology (`meshopt_simplifySloppy`) down
  to a dozen triangles, the one level the ordinary simplifier cannot reach. Its parent error is the model's
  diameter, so the same error test that picks every other level draws the root until the whole model is under a
  pixel, and then nothing. Forests, cities and debris fields at the horizon are a dozen triangles each until they
  are smaller than a pixel.
- **Vertices** are packed per model: positions as floats, normals and tangents octahedral 16-bit, UVs 16-bit, in
  one buffer per model that the cluster table indexes. A cluster is 80 bytes of header: vertex offset, triangle
  offset, counts, bounds, LOD error, parent error, cone for backface culling.

**Culling** is two compute passes per view with an indirect draw between:

1. Instances against the frustum and the previous frame's depth pyramid (HZB); survivors write their cluster ranges.
2. Clusters, each in its own thread: frustum, the normal cone, the detail test (project the parent's error and the
   cluster's own; draw when the parent's is above the threshold and the cluster's is at or below), then the HZB test
   against last frame's pyramid. Visible clusters go to the visible list; clusters that fail only the HZB go to a
   retest list.
3. `vkCmdDrawIndirectCount` over the visible list, one indirect draw per cluster, not indexed: the vertex shader
   fetches each vertex by its place in the cluster's run of the index pool, so the triangle index is the vertex id
   over three and no pixel shader needs `SV_PrimitiveID`, which Metal has no geometry stage to give. The vertex
   shader writes the visibility id as a flat varying and the pixel shader stores it.
4. Build this frame's HZB from the depth so far. Retest the retest list against it, draw what passed. Nothing that is
   visible this frame is missed, and nothing that was hidden last frame and still is costs a pixel.

The threshold is one pixel of projected error at the output resolution, scaled by a quality setting. There is no
distance cull and no size cull as such: a thing stops drawing when its diameter, the root's parent error, projects
under the threshold, and then it was not visible.

**The visibility buffer** is `R32_UINT`: 25 bits of index into the frame's visible-cluster list and 7 bits of triangle
index, plus the hardware depth. The visible-cluster list holds instance and cluster ids, so one load gives the
triangle, its instance, its material and the three vertices, as in Burns and Hunt, "The Visibility Buffer", 2013.
Barycentrics and their screen derivatives come from the three projected vertices (Schied and Dachsbacher, "Deferred
Attribute Interpolation", 2015), so every texture sample in the material pass has correct gradients with no quad
overdraw and no derivative at all from the hardware.

**Mesh shaders**, where `Features.MeshShaders` is present, replace step 3's vertex path with one task plus mesh
shader that also culls triangles per cluster (backface, degenerate, outside the viewport) before rasterisation. Same
cluster data, same visibility buffer, chosen at pipeline creation. A software rasteriser for sub-pixel triangles is
not planned: the detail test keeps triangles around a pixel or larger, and the 64-bit image atomics it needs are
missing on the Mac baseline.

### Materials

A material is a `.material` asset: the Slang file implementing `IMaterial`, its textures and parameters. The
preprocessor compiles a `GpuMaterial` row per material: a material-kind index and a 256-byte parameter block with heap
indices for its textures.

Shading a visibility buffer per material needs the pixels sorted by material. A classify pass walks the visibility
buffer in 8×8 tiles, reads each pixel's material kind through its instance, and appends the tile to each kind's list
that occurs in it. One indirect dispatch per material kind present this frame then runs `ResolveMaterial<M>` over its
tiles, skipping the pixels of other kinds, and writes the GBuffer:

| Target          | Format             | Holds                                                           |
| --------------- | ------------------ | --------------------------------------------------------------- |
| Normal          | `R16G16_UNORM`     | octahedral world normal                                         |
| Base            | `R8G8B8A8_SRGB`    | base colour, metalness                                          |
| Surface         | `R8G8B8A8_UNORM`   | roughness, specular, occlusion, shading model and flags         |
| Emissive        | `B10G11R11_UFLOAT` | emitted radiance                                                |
| Motion          | `R16G16_SFLOAT`    | screen velocity from last frame's instance transform            |
| Depth           | `D32_SFLOAT`       | from the visibility pass, reverse                               |

The lighting after this is one code path for every material. A material that needs another response to light (cloth,
clear coat, subsurface, hair) sets a shading model the lighting switches on; those models live once in
`Surface/ShadingModels.slang`, written from their papers, and a material only chooses one.

Skinning and morphing run as a compute pass before culling that writes an instance's posed vertices into a per-instance
buffer the clusters then read; the cluster bounds for a skinned instance are the bind-pose bounds grown by the
skeleton's travel. It is a second-phase item and the data layout leaves room for it from the first.

### Shadows: virtual shadow maps

One technique for every shadow that must be sharp, as the reference describes it:

- **The sun** has a clipmap of up to 24 levels, each a 16k×16k virtual texture of 128×128 pages centred on the camera,
  level `n` covering twice the span of `n − 1`. Level 0 spans a few metres at sub-centimetre texels; level 23 spans
  the continent. A shadow is sampled at the level whose texel matches the receiver's screen footprint, so a shadow is
  as sharp as the screen can show it near and far alike.
- **Local lights** that earn a virtual shadow map, the `ShadowedLights` nearest and brightest this frame, get six
  virtual faces with mip levels, sampled by distance the same way.
- **Pages are requested from the depth buffer.** A pass over last frame's depth projects each pixel into every light,
  finds the page and level it needs, and marks it in the page table. A page is allocated from the physical pool
  (default 8k×8k `R32_UINT`, 4,096 pages, 256 MB; a setting) when first marked and kept while it keeps being marked.
- **Pages are cached.** A static instance that has not moved leaves its pages valid; an instance that moved, appeared
  or died invalidates the pages its old and new bounds touch, in a pass over the instance diff. A page is redrawn only
  when invalid, so a still scene draws no shadow geometry at all after the first frame, and a moving character
  redraws the few pages under it.
- **Drawing a page** is the cluster path again: cull clusters against every invalid page's frustum (one pass, pages as
  the view list), then one indirect draw per page and cluster, the vertex shader offsetting into the page's place in
  the pool and the pixel shader doing `InterlockedMin` of the depth into the `R32_UINT` pool. No depth attachment, no
  per-page viewport state, one draw stream for all pages of all lights.
- **Sampling** reads the page table, falls back one level when a page is missing, and filters with a 3×3 PCF on the
  physical page with receiver-plane depth bias computed from the GBuffer normal, so a flat floor at grazing sun has
  neither acne nor a detached shadow. Contact hardening comes from a 5-tap blocker search in the same pages.
- **Glass casts coloured shadows.** Translucent clusters draw into a second pool of `R8G8B8A8` pages with
  multiplicative blending of their transmittance colour; the sampler multiplies the light by the page's colour when the
  translucent depth is in front of the receiver. Stained glass paints the floor.

The budget is the pool. When more pages are marked than fit, the farthest levels are dropped first, and the shadows
there fall back to the distance field.

### The distance field

Every world trace that is not a screen trace walks a signed distance field. Nothing leaks through it because it is the
real geometry, not a grid of radiance.

- **Mesh distance fields** come from the preprocessor: a narrow-band SDF per model, 64³ voxels across the bounds
  with a band of 4 voxels, the sign within the band from the nearest triangle's pseudo-normal and beyond it from ray
  parity, so open meshes still have an inside. Stored in the cache with the clusters.
- **The global distance field** is 4 clipmaps of 128³ voxels around the camera (12.5 cm to 1 m per voxel), each a
  `R8_SNORM` 3D texture of distance in band units, in bricks of 8³ voxels. A compute pass updates the bricks instances
  touched since last frame, taking the min over the mesh fields of the instances listed for each brick. A still world
  updates nothing; the camera moving scrolls the clipmaps and fills the new slab.
- **Translucent instances** are left out of the field and listed instead as spheres with a tint, up to 64 a frame,
  that the rays attenuate through, so light reaches a probe behind a window and arrives tinted.
- **`IWorldTracer`** has two implementations: sphere tracing the global field with a cone for soft contact, and
  `RayQuery` against acceleration structures the driver builds from the clusters when the feature exists. A trace
  returns a hit position, a normal from the field's gradient, and the instance it most likely hit through the
  brick's instance list, which is what the surface cache needs to shade it.

### Global illumination

The design is Wright et al., "Lumen: Real-time Global Illumination in Unreal Engine 5", 2022, in its software path,
with kajiya's temporal shape for the probes. The chain is: cards hold what every surface looks like lit; probes in the
world gather from the cards along traced rays; probes on screen gather from the world probes and from their own short
rays; the integrator turns that into irradiance per pixel.

- **The surface cache.** The cards are the six sides of a model's box, each the model drawn orthographically along
  that axis into atlases of albedo, normal and depth, at the texels its size in the world earns (a texel per
  decimetre, 8 to 128 across, the instance's scale bucketed by powers of two), so a surface facing any way has a
  side that saw it at the detail it shows. A pool of 160 lit slots goes to the instances that take most of the
  view; a slot holds the irradiance on the instance's cards at half their texels, a few slots lit a frame: the sun
  through the virtual shadow maps and every light whose reach touches the instance, through a light list per slot,
  plus the card's own radiosity, a probe an 8 by 8 tile of lit texels that traces a few cosine rays through the
  field each time the slot is lit, reads the lit cards they land on, and keeps most of what it held. So the bounce
  converges on the cards themselves, and the world probes are only the cache past the field's range. What a ray
  reads when it lands on an instance is the card's albedo under the lit irradiance there: the side its normal faces
  most, where the card's depth agrees with the point, else the side after it. An instance with no slot reads its
  card's albedo under a fallback light.
- **The radiance cache.** A clipmap of world probes (4 levels, 16³ probes, 1 m to 8 m apart), each an octahedral
  16×16 map of radiance, a 16×16 map of hit distance and its square, and an 8×8 map of irradiance. Each frame a
  budget of 256 probes, the ones whose cells moved first and then each in turn, traces 256 rays through the field,
  shades hits from the surface cache and misses from the sky, and blends into its history. Visibility is stored with
  the radiance, so a probe inside a wall is never read from the room beside it (Majercik et al., "Dynamic Diffuse
  Global Illumination with Ray-Traced Irradiance Fields", 2019 for the Chebyshev test).
- **Screen probes.** One probe per 8×8 pixel tile, placed on the depth at a jittered pixel of the tile, 64 texels
  each over the hemisphere of its normal, cosine-distributed, so the average of its texels times pi is its
  irradiance. The 64 rays are shared out by what last frame's probe at the same surface saw in each texel's
  direction: a texel that held much of the light traces up to four rays, one that held almost none keeps what it
  held, so a window or a lamp is sampled where it is and the rest is not wasted on the dark. A ray first traces the
  screen (the depth pyramid, a few steps, for contact detail), then the field up to the radiance cache's range, and
  ends in the nearest world probes weighted by their visibility; a ray that runs out of steps ends where it got to,
  not at the sky. Each probe's result is temporally reprojected from last frame's probe at the same surface, then
  filtered spatially over neighbouring probes on the same plane.
- **Integration.** Per pixel, the four nearest screen probes are interpolated with plane and normal weights, or,
  where none of them lies on the pixel's plane, the ring of probes around them by plane alone; the diffuse comes
  from their irradiance, the rough specular from the irradiance too, and the short-range occlusion the depth around
  the pixel shows (horizons searched in a few directions over a few pixels, blurred along the depth) stops the leak
  interpolation can still make at a contact. Sharp specular has its own pass.

Why interiors are dark: a probe inside a room traces 256 rays that all hit the room's walls in the field, and those
walls' cards are lit only by what reaches them, which for a windowless room is nothing. Why the floor behind you
glows: the card under the window is lit by the sun through the glass pool, a world probe beside the player gathers that
card, and the floor at your feet reads that probe.

### Reflections

Per pixel with roughness under the rough-specular threshold: a ray sampled from the GGX lobe (one per pixel per
frame on the quality preset, one for every two pixels across on the others), a screen trace through the HZB, then a
field trace, the hit shaded from the surface cache, a miss from the world probes and the sky. The rays around a
pixel are gathered over a reach the roughness sets, none for a mirror, each weighed by how near its surface is to
the pixel's and how near what it hit is to what the pixel's own ray hit, so a mirror stays sharp and a rough floor
smooth; then blended with last frame's at the surface's velocity, held within what the neighbours see. Nothing a
mirror shows is limited to what is on screen: turn away from a lake and the house behind you is still in it, from
its cards.

### Lighting

Direct light is clustered: a froxel grid of 32×18×48 in view space, a compute pass that bins every light into the
froxels it reaches, and per pixel the loop over the froxel's list. A light is `ILight` evaluated with physical units
(lumens and candela, lux for the sun, nits for emissive), so an exposure meter can mean something. Shadows per light
are `IShadow`: virtual pages for the budgeted set, a soft cone through the field for the rest, the choice per light in
the light table. The sky is `ISky`: Hillaire, "A Scalable and Production Ready Sky and Atmosphere Rendering
Technique", 2020, with transmittance, multi-scattering and sky-view LUTs, so the sun's colour at dusk and the sky's
light on the ground come from one model. Volumetric fog runs in the same froxel grid, lit by the same lists and the
sun's pages, so light shafts fall through windows into the dust.

The lighting pass writes HDR `R16G16B16A16_SFLOAT`.

### Translucency

Translucent clusters are culled with the opaque ones but drawn after lighting, forward, in a pass that is generic over
`IMaterial` and shades with the same lights, shadows and probes. The order problem is solved without sorting by
moment-based order-independent transparency (Münstermann et al., 2018): a first draw accumulates four power moments of
depth per pixel, with the absorbance by colour, a second draw resolves each fragment's transmittance from the moments
and adds its light and how far it bends the picture, and the tonemap puts the sum over the picture behind, read at the
bend. Colour the glass casts on the world is the sun's glass shadow, one orthographic map around the camera the panes
multiply their tints into, and light arriving through it at a probe is the glass list's, the panes as spheres a ray
is attenuated by.

### Post

TAAU first, so the frame can render at a scale under one and every stochastic term above (probes, reflections, soft
shadows) converges in it; the jitter sequence and the history weight follow Karis, "High Quality Temporal
Supersampling", 2014, with the reprojection from depth and last frame's view and a disocclusion test from the
depth the history kept. Then a histogram exposure meter over the resolved picture, bloom as a mip pyramid, the
effects fused into one pass as `PostFinal<E0..E3>` over the `IPixelEffect`s the camera's `PostProcessing` names, and
the AgX tonemap. The window receives the picture in its composition's space.

## Assets and what the import step makes

The loader reads the file; the post-processor derives what the renderer needs; the cache keeps it under the asset's
version hash, so a change to the file or its `.meta` remakes it and nothing else does. `--bake` runs every domain
through this so a release ships with the caches full.

| From       | Made by            | Into the cache as   | Holds                                                                        |
| ---------- | ------------------ | ------------------- | ---------------------------------------------------------------------------- |
| `Model`    | `MeshPreprocessor` | `MeshDerived`       | packed vertices, the cluster DAG, the mesh distance field |
| `Texture`  | `WorldRenderer`    | `GpuTexture`        | BC7 (colour), BC5 (normals), BC4 (masks) with mips, through bc7enc           |
| `Shader`   | `SlangShaders`     | `CompiledShader`    | SPIR-V per specialisation, reflection, the layout of every `[ShaderStruct]`   |
| `Material` | `WorldRenderer`    | nothing             | a row in the material table, remade from the snapshot when the asset arrives  |

Captures need a GPU, so they are made on the render thread when a model first loads, after its clusters are resident,
through `Threads.PostAsync`; the preloader counts them as part of a domain's opening. Everything else runs on a worker.
`MeshPreprocessor` is pure functions over arrays (clusterise, group, simplify, build the field) with a
thin `ProcessAsync` around them, so each is a unit test with a known mesh.

## The pipeline asset

`IRenderPipeline` is what the renderer runs each frame; the default is `Resources/Scripts/DefaultPipeline.cs`, a script
asset the project's `.magic` or a camera can replace. Until script assets exist, the renderer runs the default order
below with the `WorldRenderer.preset` setting choosing how much of it a frame does. A pipeline records passes into a
render graph through the renderer's steps:

```csharp
public sealed class DefaultPipeline : IRenderPipeline
{
    public void Build(RenderGraph graph, in RenderView view, RenderSteps steps)
    {
        var shadows = steps.RenderShadows(graph, view);
        var visibility = steps.RenderVisibility(graph, view);
        var gbuffer = steps.ResolveMaterials(graph, visibility);
        var cache = steps.LightSurfaceCache(graph, shadows);
        var gi = steps.GatherGlobalIllumination(graph, view, gbuffer, cache);
        var reflections = steps.TraceReflections(graph, view, gbuffer, cache);
        var hdr = steps.ShadeLighting(graph, view, gbuffer, shadows, gi, reflections);
        hdr = steps.DrawTranslucency(graph, view, hdr, shadows, gi);
        var output = steps.RunPost(graph, view, hdr, gbuffer);
        steps.DrawOverlays(graph, output);
        steps.Present(graph, output);
    }
}
```

Each step returns typed graph handles, so a custom pipeline can leave a step out, put its own pass between two (a
`graph.AddCompute("Outline", shader, reads: gbuffer.Normal, writes: hdr, ...)`), or replace one. The graph is a value:
passes with declared reads and writes over transient resources with lifetimes. Compiling it is a pure function that
orders passes, computes every transition, aliases transient memory and emits `GpuCommands`; it is unit-tested with no
GPU. A pass with a missing input, a cycle or a use after a resource's death fails the compile with the pass names,
and the frame draws the failure banner.

Extension points, from the cheapest up:

1. A post effect: one `.slang` implementing `IPixelEffect`, named in a `PostProcessing` component. No code.
2. A material: one `.slang` implementing `IMaterial`, named in a `.material`. No code.
3. A sky or a light type: a `.slang` implementing `ISky` or `ILight`, named in the `Sky` component or the light.
4. A pass: a compute or draw entry plus a few lines in a pipeline script.
5. A pipeline: a script that composes the steps in another order or with another set.
6. A tracer or a shadow: an `IWorldTracer` or `IShadow` implementation, chosen in settings, for a renderer feature
   the GPU has that this plan treats as optional.

## Inside the engine

Core runs one thread and a set of pipelines (`PipelineStages.cs`); a gem joins the stages it needs with
`IPipeline<TStage>`, and the gems run in the order the project lists them, each after the gems it took in its
constructor. The renderer fits that shape:

- **Stages.** The GPU provider joins `Render` first, as it is made first: it waits for the frame two before, takes the
  window's image and resets the staging ring. The renderer joins `Update`, where it reads the ECS into a `RenderWorld`
  value, and `Render`, where it diffs that value against the GPU tables, runs the pipeline script into a graph, compiles
  the graph into `GpuCommands` and submits. The provider also joins `ServicePump`, polling fences, so a readback
  awaited during `Startup` completes while no frame runs.
- **State.** The renderer keeps every GPU table and its free list, the resident models and textures, the VSM page
  table and pool state, the probe clipmaps' scroll origins, the last frame's HZB and history textures and the compiled
  pipelines in its own fields, replaced whole each frame as immutable collections of handles. Nothing of it is read by
  another gem except through `FrameEvents` and the services it provides.
- **Residency.** A model's clusters, field and captures are resident while any instance of it is in an open domain; a
  texture while a resident material names it. Uploads go at `UploadBudgetBytes` per frame through the staging ring,
  largest first; the domain's `WorldLoad` does not finish until everything it spawns is resident, so nothing it shows is
  ever missing. Per-cluster streaming and feedback-driven mip streaming are the second phase's additions and the tables
  are laid out so they slot in (a cluster page index per cluster range, a requested-mip per material slot).
- **Loading a domain.** In `WorldLoad` the renderer loads the models, materials and textures the domain's prefabs name
  and waits for their captures; then it renders the held world out of sight for the frames it takes the probes and
  pages to settle (`SettleFrames`, 8 by default) before returning, so a domain appears whole and lit, never dark for a
  second and then bright.
- **Failures.** A model that failed loads as the failure cube, a material that failed as the magenta `Failure`
  material, a shader that failed compiling keeps the last good one and shows a banner naming the file and line. Every
  one is `Log.Error` once, and the frame goes on.
- **Settings** cover quality (render scale, VSM pool and levels, probe counts, field clipmaps, trace distances, light
  and shadow budgets), with presets `Toaster`, `Default`, `Ultra`. Core has no settings service yet; until it does
  each gem keeps its knobs as constants, and the Vulkan gem turns validation on in a Debug build. Debug views come as a
  setting: visibility ids, cluster level, overdraw, VSM pages and levels, field slices, cards, probes, the light grid,
  each a `[branch]` in the output pass rather than a variant.
- **Metrics.** Every pass is a debug group the provider stamps; `GpuTimings` reports each pass's GPU time, and the
  renderer's meter adds the visible cluster count, pages drawn, probes traced and bytes uploaded.

## Alternatives considered

| Instead of                       | Considered                                         | Why not                                                                                              |
| -------------------------------- | -------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| Distance-field GI + surface cache | Voxel cone tracing (SEGI, the original's voxels)   | Light leaks through walls thinner than a voxel and the cones blur every corner; interiors are never dark |
|                                  | Hardware ray tracing only (kajiya)                 | No ray tracing on MoltenVK; it is the optional `IWorldTracer` for GPUs that have it                   |
|                                  | Probe grids alone (DDGI)                           | Kept as the radiance cache; alone it is too coarse for contact and leaks at interpolation            |
| Virtual shadow maps              | Cascaded shadow maps                               | Fixed resolution per cascade: blurry at the horizon, acne near, never both sharp                      |
|                                  | Ray-traced shadows                                 | Not on the baseline; the field's cone shadow fills in for lights past the VSM budget                   |
| Visibility buffer + GBuffer      | Classic deferred                                   | Materials in the raster pass fight the cluster path's one pipeline, and quad overdraw on small triangles |
|                                  | Forward+                                           | Every material carries the whole lighting loop, and no visibility pass to drive the shadow page marks |
| Cluster DAG, hardware raster     | A compute software rasteriser (Nanite's)           | Needs 64-bit image atomics, absent on the Mac baseline; the detail test keeps triangles pixel-sized    |
| A sloppy root under a pixel       | Imposter quads, distance culling, HLOD groups      | An imposter is a second representation to capture and keep right; culling pops; HLOD is authoring  |
| Moment-based OIT                 | Sorted per instance, depth peeling, linked lists   | Sorting fails on interleaved glass, peeling is a pass per layer, lists are unbounded memory           |
| An own render graph              | A NuGet render graph                               | None exists over our own `IGraphicsApi`; the compile is 600 lines of pure code we can test             |
| TAAU                             | MSAA, FXAA                                         | Neither converges a stochastic frame; MSAA is wasted on a visibility buffer                             |
| Clipmap radiance cache           | Irradiance volumes placed by hand                  | Authoring, and a volume is baked by another name                                                    |

## Libraries looked at

- **Vortice.Vulkan** for the API, already the project's choice; `Silk.NET` would do the same with a larger surface.
- **slang** prebuilt, bound by hand as planned; there is no maintained NuGet binding with reflection.
- **assimp** built with FBX, glTF and OBJ only; `SharpGLTF` would cover glTF alone.
- **meshoptimizer** for clusters, partitioning, simplification with locked borders, vertex quantisation and cache
  optimisation; it is the one library that does all of the DAG's mechanical parts and is maintained for exactly this.
  Nothing else was close.
- **bc7enc** (rgbcx and bc7enc_rdo) for BC4/5/7, as the original used; `Compressonator` is heavier and `ISPC
  texcomp` needs ISPC in the build.
- **For the distance field**, nothing: a BVH over triangles with a winding-number sign in C# is 400 lines and runs on
  a worker; `openvdb` is far too much and `Discregrid` is unmaintained.
- **For denoising**, nothing: `NRD` targets DX12 and Vulkan with its own binding layer and wants ray-traced inputs;
  the filters here are small and written from their papers.

## Risks and what is not verified

1. **The Mac baseline.** The plan rests on MoltenVK and KosmicKrisp giving descriptor indexing with update-after-bind,
   buffer device address, draw indirect count and 32-bit image atomics. The first task on hardware is a feature dump
   on Apple silicon. If image atomics fall out, the VSM page draw becomes one draw per page with a depth attachment
   into the pool, slower and still correct. If descriptor indexing falls out, the whole design changes, which is why
   it is checked first.
2. **Distance-field GI quality at the field's resolution.** A wall thinner than a voxel of the finest clipmap (0.2 m)
   can leak at distance. The mesh fields are full resolution and the global field is what the long rays use; if that
   is not enough, the finest clipmap gets smaller voxels at the cost of memory, and the ray-query tracer removes the
   problem on GPUs that have it.
3. **The surface cache budget.** Lighting every card of every near instance each frame is the single largest cost in
   the GI chain. The budget is by screen size and a per-frame update rate; whether it keeps up with a moving scene at
   60 fps is only measurable once built.
4. **Materials have no vertex stage.** A material shades a surface the cluster path produced; it cannot displace
   vertices or author its own vertex format. Terrain, foliage sway and water need a pre-pass that writes posed vertices
   (the skinning path) rather than a vertex shader. If a game needs a true custom vertex stage, it is a custom pipeline
   pass drawing its own geometry into the visibility buffer.
5. **No caustics.** Light through glass is coloured and attenuated, never focused. A refraction pool is a later
   addition if wanted.
6. **Performance targets** are a guess until measured: 1080p at 60 fps on a mid-range GPU of 2023 with the `Default`
   preset, the GI chain under 6 ms, the VSM under 2 ms in a still scene. A miss by a factor of two is a settings change;
   a miss by five would move the screen-probe gather down to half resolution and the radiance cache up as the main
   source.

## Build order

Each phase ends with a screenshot that joins `tools/Scripts/Validate.ps1`, and the pure parts of each with tests.
Line counts are estimates of C# and Slang.

| Phase | Builds                                                                                                   | Checkpoint                                                                                                   | Size  |
| ----- | -------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ | ----- |
| 0     | Core's `Gpu/` and `Shaders/`, `IGraphicsApi`, `VulkanGraphics`, `IShaderCompiler`, `SlangShaders`, the `Core/` shaders, a `GpuChecks` gem | A compute gradient through the heap, read back and checked headless on lavapipe, and shown in the window on Windows, Linux and a Mac | 7k    |
| 1     | `AssimpModels`, `MeshPreprocessor` clusters and DAG, the render graph, instance and cluster tables, culling, visibility, HZB, materials, GBuffer, a flat sun | 10,000 instances of a 1M-triangle model draw at a stable frame time at any distance; walking away pops nothing; the graph compile and the DAG build have tests | 9k |
| 2     | A sloppy root as the last level, captures for the cards, texture compression and the cache, failure stand-ins | A forest of 100,000 trees to the horizon; a broken model is a cube, a broken material magenta            | 3k    |
| 3     | Virtual shadow maps for the sun: page marking, allocation, caching, cluster draw into pages, sampling    | A fence shadow sharp at 2 m and a ridge's shadow sharp at 2 km in one view; a still scene draws zero pages     | 4k    |
| 4     | Lights: clustered lists, physical units, local VSMs, rect lights, the atmosphere sky, volumetric fog     | The Lights sample at a thousand lights; dusk from the atmosphere; a shaft through a window into fog           | 4k    |
| 5     | The distance field: mesh fields in the preprocessor, the global clipmaps, the cone shadow, `IWorldTracer` | Field shadows for the lights past the VSM budget; a field slice debug view; the mesh field has a test on a cube | 3k |
| 6     | GI: card placement and captures, the surface cache lighting, the radiance cache, screen probes, integration | A closed box reads black in a readback check; a Cornell box bleeds red and green; a room lit through its window with the camera facing away | 8k |
| 7     | Reflections and their denoiser                                                                            | A lake shows the house behind the camera; a rough floor shows a soft, stable version of it                      | 2k    |
| 8     | Translucency: MBOIT, refraction, the glass shadow pool, the transmittance volume                         | Stained glass colours the floor; a probe behind a window is lit; overlapping panes resolve without sorting       | 3k    |
| 9     | Post: TAAU, exposure, bloom, fused effects, tonemap, the pipeline asset, presets, debug views, the preloader's settle, `--bake` | Every sample domain screenshots; a custom pipeline script with an outline pass; `Toaster` on lavapipe headless | 4k |
| 10    | Optional paths: mesh shaders, ray-query tracer, skinning pre-pass, cluster streaming, mip streaming       | Same pictures, faster on GPUs that have the features; an animated model                                         | 5k    |
| 11    | Crisp GI on the software path: a sloppy root in place of the imposter, six-sided cards with their own radiosity, finer fields, probes in every 8 pixels with shared rays and screen occlusion, motion vectors, reflections at a preset stride | The question mark bleeds red onto its ground; the Lights room's walls and ceiling carry the bounce of the sun strip with no tile or band to see; a mirror floor is sharp and a rough one smooth | 3k |

Of phase 10, the ray-query tracer is built: a bottom-level structure over each model's leaf clusters, a top-level one
over the instances, and the trace passes specialised to it where the GPU has ray queries. The mesh shader path,
the skinning pre-pass and the streaming of clusters and mips are not: the first saves only the vertex fetch the
vertex-pull path already keeps cheap, the second waits for skinned models in the loader, and the pools and the
textures hold what the samples need.

Phases 1 to 4 and 5 to 8 are each sequential within and may run as two tracks after phase 1, since the field and the
GI chain touch nothing in the shadow and light chain but the GBuffer they read.

## Verification

- **Pure functions, unit tests:** the graph compile (ordering, transitions, aliasing, the error cases), the DAG build
  on a known mesh (error monotonic, borders shared, the cut through it at a given error), the card placement on a box
  and a room, the mesh field on a cube (distances exact at the centres, sign inside), VSM page allocation and
  invalidation on a CPU model of the page table, the froxel binning of a light, the `[ShaderStruct]` layout check.
- **Readback checks, headless:** a `probe x y` console command prints a pixel's HDR value and a `expect` command
  compares a region's mean against a bound, so Validate asserts the closed box is black, a white furnace keeps its
  energy, and a sunlit plane matches the sky model's irradiance within a tolerance, on lavapipe with the `Toaster`
  preset.
- **Screenshots, every sample domain,** looked at by a person at every phase, with the debug views captured too.
- **Metrics:** the `Magic.Gems.WorldRenderer` meter in the log at every checkpoint, so a phase that slows a pass is
  seen when it lands and not at the end.
