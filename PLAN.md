# Plan

The goal, the decisions, the buckets of slices and the log. Agents read this at the start of every session and work
only on the slices it lists ([AGENTS.md](AGENTS.md#slices)).

## Goal

Set by the kickoff interview. Changes only with the user's explicit yes.

- **For:** a high-quality, fast core for Magic's picture: dynamic GI with no bakes, then build on it.
- **Decision it drives:** whether Wicked Engine's frame replaces the rendering gem's lighting for good (slice 1 can stop
  this).
- **First thing a player does:** opens Forest and Lights and sees soft ray-traced shadows, bounce light and reflections.
- **Out of scope for now:** Later below, plus physics, GUI, terrain, ocean, hair, voxel GI and the rest of that engine.

## Decisions

One line each, newest first: `YYYY-MM-DD · decision · reason`.

- 2026-11-04 · The debug UI landed as one change, not slices · the user chose this when the slice rule arrived mid-task,
  having approved the whole plan.
- 2026-11-04 · A stats view lives in a new DebugTools gem · FPS, CPU and memory must work with any renderer.
- 2026-11-04 · Renderers draw overlays into their output through `IOverlay`, before showing it · screenshots show the UI,
  and `Submit` stays once a frame.
- 2026-11-04 · `IDebugUI`, `IOverlay`, `Frame.Cpu` and `IGraphicsApi.MemoryBytes` are public API; ImGuiDebugUI and
  DebugTools are new gems · approved with the plan.
- 2026-11-04 · The frame's target is upstream's on this laptop: 27 ms of GPU at 2858x1481 and 1.7 GB of VRAM (Sponza's hall view, Vulkan, Release) · measured in slice 1. The images are in the main checkout's `Build/Reference/`, not in a worktree's; `startup.lua` and `capture.ps1` beside them show how they were made, with this session's paths in them, from upstream at 0.72.141.
- 2026-11-04 · Roll from slice to slice under `/goal`, the plannotator review still gating each merge; stop at checkpoints, or when a spike or a measurement says this is the wrong direction · the user's goal says not to pause, and nothing merges unapproved.
- 2026-11-04 · A slice's shader lines may pass 300 when it ports one named upstream pass, C# held to the limit; each slice lists its upstream files · one pass can be 700 lines, and restructured shaders don't diff against upstream.
- 2026-11-04 · Copying this renderer is an exception to "never vendor or copy external code"; one notice file in the gem keeps the MIT notice, and the licences of the noise textures and of `ffx-shadows-dnsr` are read before they're copied · the user asked for a port, and MIT needs its notice kept.
- 2026-11-04 · No "Wicked" in folders, files or types; passes are modules in the gem's `Resources/Shaders/`, shared code goes in the shader library's categories · the user's review: it becomes our own shader structure.
- 2026-11-04 · No Surfel GI; DDGI plus RT diffuse · surfels are anchored to a triangle id and an LOD swap discards them, and our cluster cut changes constantly.
- 2026-11-04 · Keep the sky entity contract and `SkyLuts` · a sky is a material the project chooses; upstream's atmosphere is fixed.
- 2026-11-04 · Fog and glass go until Later, glass drawn opaque; point, spot and rect lights and camera post effects stay · upstream has the lights, and the post effect contract is Magic's.
- 2026-11-04 · Slice 3 deletes `GlobalIllumination`, `DistanceField`, `CardCapture`, `CardTable`, `SunShadows`, `Lighting`, `Shading`, `LightBins`, `FogVolume`, `GlassDraws`, `GlassShadow`, `Post`, `RendererPreset`, their shaders and the `tracer` setting; deleted lines don't count toward a slice's size · this is what "the old lighting" means, and it can only go in one step.
- 2026-11-04 · Delete the old lighting first, one renderer at a time, after slices 1 and 2 · two renderers for weeks costs more, and the spikes run first so nothing is deleted on faith.
- 2026-11-04 · Keep the cluster pipeline, `IMaterial` and `RayTracing`; replace what lights the picture · "throw out" means the lighting, and these hold the cluster DAG and culling rules.
- 2026-11-04 · Wicked Engine is the base, not kajiya · the user's choice after comparing foundations: kajiya is archived with no local lights; Wicked is MIT, maintained and complete, with simpler GI.
- 2026-11-04 · No start-up check of C# structs against their Slang twins; `[ShaderStruct]` removed · nothing read it,
  all 18 twins match today, and a check is about 250 lines and a new `IShaderCompiler` method for 3.4–6.8 s of cold
  module loading.

## Buckets

Slices are mode `prototype` and `-Change Visual`, unless one says otherwise.

### Current: prove it before deleting anything

Know the look, the cost and the porting pattern. These are spikes: nothing but this file merges.

- [x] 1. Run Wicked Engine on this laptop with RT shadows, DDGI, RT diffuse and RT reflections (`spike`, `-Change Docs`)
- [ ] 2. Restructure `rtdiffuseCS` into our modules and compile it through our Slang (`spike`, `-Change Docs`)

### Next: direct light

The frame is upstream's, with direct light only.

- [ ] 3. Delete the old lighting: the frame shows unlit albedo
- [ ] 4. What the passes need in `FrameConstants`, and the shared helpers in our library: a debug view shows normals and depth
- [ ] 5. `Shade` from the G-buffer with upstream's BRDF and the sky: Forest lit by sun and sky, no shadows
- [ ] 6. Light tile culling with point, spot and rect lights: the Lights sample is lit
- [ ] 7. Ray hit loading over Magic's tables, RT shadow mask, denoiser: soft shadows in Forest and Lights
- [ ] 8. TAA, eye adaption, bloom, tonemap, camera post effects: stable finished look. **Checkpoint:** against slice 1's image

### Then: diffuse GI

- [ ] 9. DDGI: probe grid, ray allocation, trace, update: a debug view shows probes lit
- [ ] 10. DDGI into `Shade`: bounce light in shadowed areas of Forest and Lights
- [ ] 11. RT diffuse: trace, spatial, temporal, upsample: contact-level bounce, misses fall back to DDGI

### Then: reflections

- [ ] 12. RT reflections: trace, resolve, temporal, upsample. **Checkpoint:** full picture against slice 1's image

### Then: fast and clean

- [ ] 13. Measure against slice 1's target, fix the worst passes, restore render scale
- [ ] 14. Remove what's now unused: distance field and card baking, the `Field` cache section, unused shader modules, the gem's README and description (`-Change Code`)

### Later

One line each. Not sliced until they're current.

- Height fog and volumetric light
- Glass
- Shadow maps and software tracing for GPUs with no ray query
- FSR2 upscaling
- Alpha cutouts on ray hits, if the hit loading leaves them out

## Cleanup

Anything noticed mid-slice that isn't the slice's job.

- CPU `Render` in the stats includes the GPU and display waits, so it reads about a whole frame at 60 FPS. Split the wait
  out of `Submit` to show real CPU time.
- The debug UI uses ImGui's own font. The Font importer and the asset-fed font loader are unblocked now SdlBindings has
  merged.
- The debug UI shows nothing until the renderer draws its first frame, and nothing without a renderer.
- The console and settings windows join DebugTools once `ICommands` and a settings service exist.
- Clicking and typing in the debug UI have not been driven by a script: SDL takes the mouse from the real cursor.
- Under the validation layer: every graphics pipeline's vertex stage breaks `vertexPipelineStoresAndAtomics` through the
  heap's writable buffers, the `Show` blit hazards with the window image's next transition, and the `Lit slots` copy
  hazards with its fill. Each ends a validated run under `--fail-on-error`.
- Slice 3 deletes `Shading`, `Post`, `Lighting` and `RendererPreset`, which `DebugView`, `RendererDebugWindow` and the
  views in `ShadeLighting` and `PostPasses` are built on. The new frame needs them written again over its own passes.
- `Validate.ps1 -Only Screenshot` stops after its first project: no native command has set `$LASTEXITCODE` by then.
- `GpuVertex` and `GpuCluster` are in Core, and their Slang twins are in the GI renderer's gem.

## Log

One line each, newest first: `YYYY-MM-DD · slice · result · notes`. Past about 30 lines, fold the oldest into one
paragraph.

- 2026-11-04 · Debug UI, debug views and stats view · committed unstamped at the user's word, not merged · one change, over the slice size by choice. Before the review's five fixes the tests passed and each sample's screenshot looked right; after them it has only compiled, and no run or second review saw them.
- 2026-11-04 · 1 · Vulkan Release on the RTX 3070 Ti Laptop at 2858x1481 with every RT feature and DDGI on: GPU 26.9 ms in the hall view (DDGI 1.6 ms), VRAM 1.7 GB; whole frames averaged 27 to 36 ms across five views in the last run, 25 ms for the atrium in an earlier one, and the run before it 28 to 67 ms, with one view at 243 ms as the profiler came on; about 3.5 min to compile 385 shaders on first run · Other sessions held the CPU at 50 to 100% throughout, so only the GPU figure is a clean target; no run was on a quiet machine, and the profiler image shows only part of the GPU list. The scene's own fire lights dominate the colour, and arcades away from them read dark. Whether this is the look the user wants is for this slice's review. One-line patch to the clone so `vulkan` is parsed before the window, and `alwaysactive` so it runs unfocused.
- 2026-11-04 · Correct the shader struct docs · done · removed `[ShaderStruct]` and the doc's mention of `ILight`.
