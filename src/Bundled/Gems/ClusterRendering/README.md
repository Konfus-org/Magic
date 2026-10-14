# ClusterRendering

Draws the world GPU-driven, as clusters at the detail each part of it needs: each camera's view of the scene's
triangles into a visibility buffer, each pixel shaded once into a G-buffer and lit once, then the sky, transparent
surfaces lit as they are drawn, the view upscaled to its target's size, and the world's effects. Every shadow, the
sun's and every point and spot light's, is pages of one virtual shadow map. It takes the GPU, a shader compiler, the
assets, the cache, the world and the engine's threads, and a window and the debug UI when there are; it provides the
cache's ways of keeping a model's distance field and a texture compressed for its pool.

The CPU only keeps the GPU's tables in step with the world, a row for each thing that changed, and records a fixed list
of passes. Which instances and clusters are drawn and how finely, which lights reach which pixels, which shadow pages
are wanted, kept, given memory and drawn: all of it the GPU works out each frame, without reading anything back.

## Settings

In the project's `Presets/Default.preset`, by another preset (`--preset Toaster`), or with
`--set ClusterRendering.<key>=<value>`:

| Key | Default | |
| --- | --- | --- |
| `pipeline` | none | The pipeline each frame runs before the world's effects, `{ "id": 60200 }`; none for `Pipelines/Default.pipeline`, the type's default, which runs nothing. |
| `renderScale` | `1` | The share of its target's size a view is drawn at, then upscaled to fill it: below 1 for a slower GPU, 0.1 to 2. |
| `clusterError` | `1` | Pixels a cluster's error may cover on screen, and a shadow's texels, before a finer cluster is drawn in its place: lower is finer, 0.25 to 16. |
| `pieceCapacity` | `1048576` | Pieces of up to 32 triangles a view draws at most. Past it, what is farthest from the player is drawn coarser, never left out. |
| `uploadBudget` | `32` | Megabytes of models taken into the GPU's pool a frame at most, nearest the player first. |
| `lightCapacity` | `16384` | Point and spot lights a view lights pixel by pixel at most, nearest first, a power of two; the farther light only through global illumination. |
| `shadowPool` | `8192` | Texels a side of the pool every shadow is drawn into, a power of two: how many pages of shadow are held at once. |
| `shadowLevels` | `16` | Levels of the sun's shadow, each twice as wide as the last: how far it reaches, 1 to 24. |
| `shadowFinest` | `8` | Metres across the sun's finest level. |
| `shadowDepth` | `4000` | Metres along the sun a page holds either side of the camera: as far as what casts into it can be. |
| `shadowUpdates` | `512` | Pages of shadow drawn a frame at most, the sun's coarsest levels and what is nearest the player first; the rest wait, read coarser till then. |
| `anisotropy` | `8` | The most taps a material's textures are filtered with at a grazing angle: 1 to 16. |
| `giLevels` | `4` | Levels of global illumination's scene and probes, each twice as wide as the last: 1 to 4. |
| `giVoxel` | `0.5` | Metres a voxel of the finest level. |
| `giRays` | `64` | Rays a probe traces each time it is traced: 8 to 256. |
| `giUpdateEvery` | `4` | Frames between the finest level's probes being traced, a power of two; each coarser level's half as often. |
| `giMeshFields` | `1024` | Models whose distance fields are held at once, 64 KB each. |

They size what the renderer allocates, so they belong to the machine. `pipeline`, `anisotropy` and `giMeshFields` are
read as the renderer starts; the rest every frame. The renderer's frame is `Pipelines/Clusters.pipeline`.

## A frame

**The copy.** In LateUpdate, while the render thread is idle, systems on the ECS's threads copy what Render reads: each
instance's transform and renderer, the lights, cameras, sun, sky and post-processing. Each is kept at its entity's slot
and compared with what was there, so only what changed is passed on.

**Positions far from the origin.** Core's positions are doubles. Every table on the GPU keeps a position as a float and
what the float misses, and every shader works relative to the camera, both halves taken from the camera's, so what is
near the camera is exact however far it is from the origin.

**The scene on the GPU.** Instances, lights and materials are tables kept by slot and row, uploaded a run of changed
rows at a time. An instance's row holds its transform and the last frame's, its model's row, its material slots' rows,
and when its fade started: `Renderer.Fade` is timed on the GPU from there. Models go into one pool of geometry, nearest
the player first within `uploadBudget` a frame: each cluster's vertices are five words (its position in steps of its
model's grid, its normal and tangent on the octahedron, its uv as two halves) and its triangles a word each. A model
with no cluster hierarchy, one a gem made, is cut into clusters as it is, of one level. Cards (Core's `Card`) take a row
each and take over from a model's clusters far off, across a band where both dissolve into each other.

**Culling.** Each view culls its instances, then their clusters, in two phases (Karis 2021; Haar & Aaltonen 2015). The
first tests what is in view against last frame's depth pyramid, seen through last frame's view, and draws what it does
not hide; the second builds a pyramid of what was drawn and tests again everything the first hid, so what came into
sight is drawn the same frame. An instance hands only the levels of its model's hierarchy whose error, from as near and
as far as it reaches, can be what the view wants there. A cluster is drawn where its error, projected from its group's
sphere as near as it comes, covers no more than `clusterError` pixels and its parent's does: every cluster decides
alone, and as a group's clusters share their sphere and error the cut meets without cracks.

**Detail shared out nearest first.** Each view counts the pieces it drew in 64 buckets by distance from the player's
camera, four to a doubling. A bucket whose pieces, with every nearer bucket's, would pass most of `pieceCapacity` draws
coarser the next frame, its error allowed twice as many pixels each frame it would not fit and a little fewer each frame
it does, and no bucket is ever finer than a nearer one. Overflow never drops anything near: what is far goes coarse.

**The visibility buffer.** A cluster's triangles are drawn in pieces of up to 32, each an instance of one draw of 96
indices whose vertex stage reads the piece's vertices itself. Pieces are laid out by raster bin: the two that do not
clip (single and double sided), and each surface's own and its cards', which clip on its alpha or on a dissolve. Each
pixel keeps the name of the triangle in front, its piece's slot and which of its 32, and its depth.

**The G-buffer.** The view's 8 x 8 tiles are sorted by the surfaces their pixels show, and each surface draws a quad
over each of its tiles, evaluating its surface once a pixel for the triangle the visibility buffer names, with the
pixel's place on it from the triangle's homogeneous coordinates (Olano & Greer 1997) and its uv's derivatives from its
neighbours'. Every surface is the same `.surf.hlsl` it would be anywhere. The pixel's velocity, from where its instance
was last frame, is written beside it.

**Lights.** Every point and spot light is a row of a table on the GPU, written when it changes. Each view culls them,
sorts those in view into 1024 depth slices by a count and a prefix, nearest first, gives each slice the first and last
light that can reach it, and each 32-pixel tile a bit for each light that reaches it, each light setting the bits of
the tiles its sphere covers (Drobot 2017). A pixel walks only the lights in both, the walk kept uniform across a wave.

**Virtual shadow maps.** Every shadow is drawn into pages of one depth pool, 128 texels with a border of 4, only the
pages the view's pixels read, kept from frame to frame and drawn again only where what casts into them moved: of a
page, only the rectangle a moving thing reaches is emptied and drawn, so a small caster moving in a big page costs
little. The sun's
pages are a clipmap of `shadowLevels` levels about the camera, each 64 pages a side, a level's window moving a page at a
time with its entries addressed round so a page that stays keeps what was drawn. A point or spot light's pages are its
six faces' at a mip, a texel at most two pixels wide; its coarsest mip is one small page, a sixteenth of a page, which a
pixel is shaded through while its own waits. Each frame, on the GPU:

- each pixel marks the pages it reads, the sun's level as fine as it and the next three coarser, and each shadowed light
  that reaches it, a light's pages put into a hash table made again each frame;
- the sun's entries follow their window, and a light's take over the pages they had last frame, each page noting the
  rectangle of it that what moved since reaches;
- one group gives the pages wanted the pool's: the sun's four coarsest levels first, then by how far from the player
  the pixels that want them are, the pages read longest ago taken first; and picks up to `shadowUpdates` pages waiting
  to be drawn, by the same priority: half as many after a frame whose culls ran out of room for casters, which draws
  its pages again, and twice as many again, up to `shadowUpdates`, after one that did not;
- casters are culled by instance, then by cluster at each page's own texel size, against the rectangle of each page
  drawn, and drawn into it, the rectangle emptied first. Depth is clamped rather than clipped, so a caster nearer the
  sun than a page holds still casts; a light's faces are clipped a few centimetres ahead of it instead.

A page that waits is never read: a pixel reads the next coarser level, or mip, that is drawn. The sun's penumbra is as
wide as its disc makes it (PCSS), widened through the clipmap's coarser levels rather than more taps; a receiver is
pushed out along its normal by a texel of whichever level it reads.

**Temporal upscaling.** The view is drawn a sub-pixel off each frame (Halton 2, 3), and textures are read a little
sharper as it is drawn smaller. An upscale (Karis 2014) gathers the view's pixels about each pixel of the target,
weighed by how near their samples really were, and blends them into what it held there last frame, found by the nearest
surface's velocity and read through a Catmull-Rom filter, clipped toward the colours about it now (Salvi 2016), both
weighed by their brightness. Tonemapping reads what it made, at the target's size.

**Transparency.** Transparent instances are sorted farthest first on the CPU and drawn forward through the same pieces,
their finest clusters, lit as they are drawn and reading a copy of the lit scene behind them.

**Global illumination** is an effect: probes that see the scene as a signed distance field, made from each model's
field, which the GPU makes once from its coarsest clusters and the cache keeps. **Ambient occlusion**, **contact
shadows**, **auto-exposure** and **tonemapping** are effects too, reading the view's targets by name.

**Textures** are layers of eight texture arrays every surface pipeline binds, colour or data, each 256, 512, 1024 or
2048 texels with mips down to 4 x 4, compressed to BC7 on a worker as they load and kept in the cache, so they are
compressed once and take a quarter of the memory and of the reads.

**Nothing pops.** A model is drawn its coarsest until its finer clusters are in; an instance fades in or out across
`Renderer.FadeSeconds` by a dither, which moves each frame so the upscale blends it.

## Passes and pipelines

A pass is a `.pass` file, Core's `Pass`: the stage it runs in, its shader, the names it reads, writes and makes, and how
it runs. A pipeline is a `.pipeline` file, an ordered list of passes and other pipelines. The frame is two pipelines:
the one the `pipeline` setting picks, and the effects the world's `PostProcessing` names.

- **Names.** The renderer's own (the scene's tables, the shadows' pool and tables, each view's targets) are declared in
  `ResourceInfo.Engine`; any other is made by a pass (`creates`) for itself and every pass after it, for the frame in a
  stage that runs once a frame, else for each view. A pass may read what it makes itself.
- **Bindings are written for the shader**, in the order the binding model wants: sampled textures with a sampler named
  after each (`HdrSampler`), then textures that cannot be filtered (an integer's), read texel by texel as storage
  textures, then buffers. A shader compiled to bind other than its file says is an error naming both; a shader that
  reads a texture only texel by texel keeps its sampler bound through the header's `KeepReadsAlive`.
- **Params.** A pass's shader declares `struct PassParams`, with defaults (decimal or hex, `0xFFFFu`), and reads it
  through `LoadPassParams()`; a pass file's `params` are packed over them, so one shader runs as several passes.
- **Indirect dispatch.** `"dispatch": { "args": "Name", "first": 2 }` runs as many groups as a pass before it wrote,
  three words a row, so work only the GPU knows the size of costs no readback. No pass writes what it is dispatched by.
- **Sizes and history.** A texture made with `"size": "output"` follows the view's target, not the size it is drawn at;
  one made with `"history": true` is two, `Name` written this frame and `NameHistory` the last, swapped each frame and
  emptied whenever they are made again.
- **Drawn passes** draw the visibility buffer's pieces a raster bin at a time, the G-buffer a surface's tiles at a time,
  transparent pieces farthest first, depth alone for shadows, or the sky; the renderer composes each surface variant's
  shader from the pass's template and the surface.
- **Reloading.** Changing the world's effects, or editing a pipeline or shader, builds the whole listing again on
  workers; the frame draws with the old one until the new one is compiled, then swaps. Before the first is compiled,
  the window is cleared to black, as it is whenever no camera draws into it, and a change then waits for the listing
  under way, so the loading domain is drawn as soon as it can be.

## Debugging

- **The debug view.** While `IGpu.DebugView` names a stage, a frame records its stages up to that one and each view
  shows the first 2D texture the stage's last pass to make one draws into or writes, as the finished image is shown;
  depth, reverse-Z, shows nearly black. The occlusion stage shows the shadow pool when no effect there makes a texture.
- **Failure labels.** While the debug UI shows, every instance drawn with the failure surface says why at its place.

## What it does not do yet

- A masked material casts the shadow of its whole mesh, and cards cast none: an instance casts with its clusters, as
  coarse as its shadow's texels allow.
- A light that moves draws all its pages again every frame, which past `shadowUpdates` leaves them read coarse.
- A second view runs the shadows' passes again, and takes the lights' pages over from the first.
- Global illumination bounces the sun's light and the sky's, not point and spot lights'.
- A texture's mips are all resident; none is streamed by what the view reads of it.
