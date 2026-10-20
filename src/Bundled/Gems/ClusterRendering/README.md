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
| `overlay` | none | The pipeline run over each target once every view in it is composed, the overlay stage's; none for `Pipelines/Overlay.pipeline`, whichever gem or project has it, and nothing when none does. |
| `renderWidth`, `renderHeight` | `0`, `0` | Pixels across and down a view is drawn at, then scaled to fill its target: `720` down for a slower GPU, `2160` to supersample. `0` is the target's own size, and one of them alone keeps the target's proportions. |
| `outputFilter` | `linear` | How a view's image is read as it is scaled onto its target: `linear` between its pixels, or `nearest`, which keeps a low resolution crisp: the post stage then runs at the drawn size too, so nothing upscales before the output. |
| `clusterError` | `1` | Pixels a cluster's error may cover on screen, and a shadow's texels, before a finer cluster is drawn in its place: lower is finer, 0.25 to 16. |
| `pieceCapacity` | `1048576` | Pieces of up to 32 triangles a view draws at most. Past it, what is farthest from the player is drawn coarser, never left out. |
| `uploadBudget` | `32` | Megabytes of models taken into the GPU's pool a frame at most, nearest the player first. |
| `lightCapacity` | `16384` | Point and spot lights a view lights pixel by pixel at most, nearest first, a power of two; the farther light nothing. |
| `shadowPool` | `8192` | Texels a side of the pool every shadow is drawn into, a power of two: how many pages of shadow are held at once. Past it, the lights farthest from the player are shadowed coarser. |
| `shadowLevels` | `16` | Levels of the sun's shadow, each twice as wide as the last: how far it reaches, 1 to 24. |
| `shadowFinest` | `8` | Metres across the sun's finest level. |
| `shadowDepth` | `4000` | Metres along the sun a page holds either side of the camera: as far as what casts into it can be. |
| `shadowUpdates` | `512` | Pages of shadow drawn a frame at most, the sun's coarsest levels first, then lights nearest the player; the rest wait, read coarser till then. |
| `anisotropy` | `8` | The most taps a material's textures are filtered with at a grazing angle: 1 to 16. |
| `giLevels` | `4` | Levels of global illumination's scene and probes, each twice as wide as the last: 1 to 4. |
| `giVoxel` | `0.5` | Metres a voxel of the finest level. |
| `giRays` | `64` | Rays a probe traces each time it is traced: 8 to 256. |
| `giUpdateEvery` | `4` | Frames between the finest level's probes being traced, a power of two; each coarser level's half as often. |

They size what the renderer allocates, so they belong to the machine, and every one is read every frame: a new
`pipeline` runs once it is built, a new `anisotropy` makes the material sampler again. Each is under `Render` with a
sub-category, `[Category("Render/Shadows")]`, so a settings UI lists it with the other gems' rendering settings under
a header of its kind. The renderer's frame is `Pipelines/Clusters.pipeline`.

## A frame

**The copy.** In LateUpdate, while the render thread may still be drawing the frame before, systems on the ECS's threads
copy what Render reads into the copy it is not reading: each instance's transform and renderer, the lights, cameras,
sun, sky and post-processing. Each instance and light is kept at its entity's slot and compared with what was there, so
only what changed is passed on. Sync swaps the two copies, and Render reads the one just finished.

**Camera-relative.** Every shader works with positions relative to the camera, the frame's `CameraPosition` taken
from each, so what is near the camera is as exact as floats are near zero, wherever the camera stands.

**The scene on the GPU.** Instances, lights and materials are tables kept by slot and row, uploaded a run of changed
rows at a time. An instance's row holds its transform and the last frame's, its model's row, its material slots' rows,
and when its fade in started, which the GPU times it from. Models go into one pool of geometry, nearest
the player first within `uploadBudget` a frame: each cluster's vertices are five words (its position in steps of its
model's grid, its normal and tangent on the octahedron, its uv as two halves) and its triangles a word each. A model
with no cluster hierarchy, one a gem made, is cut into clusters as it is, of one level. Cards (Core's `Card`) take a row
each and take over from a model's clusters far off, across a band where both dissolve into each other, once their
atlases are in: until then, or for good when one fails, the model's clusters are drawn at every distance. A card shows
the two views of its atlases either side of the camera, dithered by how far round from one to the next it is, so it
turns smoothly as the camera goes round it.

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
they would fill well under it, holding in between so detail settles rather than going back and forth; no bucket is ever
finer than a nearer one. Overflow never drops anything near: what is far goes coarse.

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
- one group gives the pages wanted the pool's: each light's small page first, so its pixels always have a drawn page to
  fall back on, then the sun's levels, coarsest first, as a coarse page covers four times the ground of the next finer,
  so after a turn every pixel soon has some shadow, which then sharpens, then lights' whole pages by how far from the
  player they are; the pages read longest ago taken first, then, when the pool is full, pages read this frame from
  lights farther than what wants them, which fall back on their coarser mips (never the sun's, whose coarser levels
  nearer pixels' soft shadows are filtered through); and picks up to `shadowUpdates` pages waiting to be drawn, by the
  same priority: half as many after a frame whose culls ran out of room for casters, and twice as many again, up to
  `shadowUpdates`, after one that did not;
- casters are culled by instance, then by cluster at each page's own texel size, against the rectangle of each page
  drawn, and drawn into it, the rectangle emptied first. A frame whose culls ran out of room draws nothing, as its pages
  would have holes where the casters left out should be: every page of it waits again, read coarser till the next
  frame draws it. Depth is clamped rather than clipped, so a caster nearer the
  sun than a page holds still casts; a light's faces are clipped a few centimetres ahead of it instead.

A page that waits is never read: a pixel reads the next coarser level, or mip, that is drawn. Where one sun level gives
way to the next, a pixel picks between the two by a dither that moves each frame, which the upscale blends, so the
levels meet without a line; it marks pages from the finer. The sun's penumbra is as wide as its disc makes it (PCSS),
widened through the clipmap's coarser levels rather than more taps, but never past the three coarser the pixel marked,
so a shadow sharpens as the camera nears it, and picked between two levels by the same dither, so it never steps from
one to the next. The casters it is measured from are searched for at the widest of those levels, over all its filter
reaches there, so every pixel the filter could darken finds them and the penumbra fades out all the way to light; a
receiver is pushed out along its normal by a texel of whichever level it reads.

**Temporal upscaling.** The view is drawn a sub-pixel off each frame (Halton 2, 3), and textures are read a little
sharper as it is drawn smaller. An upscale (Karis 2014) gathers the view's pixels about each pixel of the target,
weighed by how near their samples really were, and blends them into what it held there last frame, found by the nearest
surface's velocity and read through a Catmull-Rom filter, clipped toward the colours about it now (Salvi 2016), both
weighed by their brightness. Tonemapping reads what it made, at the target's size.

**Transparency.** Transparent instances are sorted farthest first on the CPU and drawn forward through the same pieces,
their finest clusters, lit as they are drawn and reading a copy of the lit scene behind them.

**Global illumination** is an effect: probes that see the scene as a signed distance field, made from each model's
field, which the GPU makes once from its coarsest clusters and the cache keeps. The field and the probes are nested
levels about the camera, kept from showing where they meet or lagging behind a change:
- A place is lit by the finest level within 12 of its voxels of the camera, and blended into the next out to 20, so
  where one level gives way to the next moves with the camera, never in the steps a level moves by; a level whose
  probes cannot see a place hands it on by as much, smoothly.
- A probe blends each tracing into what it held by how long it waited, so light follows a change in about a third of
  a second at the finest level, √2 times longer each coarser one, at any frame rate.
- A probe that comes into use, as a level moves on, starts from the next coarser level's light where it is, the sky's
  at the coarsest, rather than from nothing, and keeps what it holds while it is out of use.
- A ray that runs out of steps creeping along a surface takes it as hit rather than letting the sky in, and a model
  only a few voxels big is no fatter in the field than its own field reaches, not a solid box, while a floor or a wall
  thinner than a voxel is still closed, so no ray passes through it.
- The field holds each surface's colour as matte, less as much of it as the material is metal: a metal only mirrors
  light, which the field cannot, so it holds none rather than glowing in the sun.
- What a ray hits is lit by the sun and by the point and spot lights that reach it, each through the field, so lamps
  bounce as the sun does. Each probe's cell, where it lies in the world, lists the 16 lights that reach it most
  strongly, made again each frame on the GPU from those that reach the field at all, found first among the table's
  slots, so the list follows lights that move and stays as a level moves.

**The sky's light** is captured each frame into a cube, 128 texels a side, projected onto nine spherical-harmonic
coefficients for light from everywhere, and prefiltered by GGX into `SkySpecular`, a mip for each of six roughnesses
(filtered importance sampling, Křivánek & Colbert 2008), which every surface reflects the sky by.

**Reflections** are an effect: each pixel smooth enough to show them, past `roughest` none, traces a ray from its GGX
lobe, a new one each frame, through the view's depth for what the view shows, lit as the last frame lit it; where that
finds nothing, through global illumination's field when the world draws it, so what is behind the camera or something
else still shows; and past everything, the sky as rough as the surface. What the field hits is lit as the view lit it
wherever the view shows it, so the two agree, and only what the view cannot see is lit as the field lights it,
coarser: by its probes, the sun and the lights near it. A ray sets out through the field from the finest level that holds its surface,
a voxel and a half of that level off it, so a far surface's ray does not start inside its own coarse copy. Every hit
is trusted by degrees, by how near the view's edges it is, how far
behind what was drawn the ray passed and how squarely it meets the surface, so a pixel slides from one to another as
the view moves rather than jumping. The rays are filtered across the surface and blended over frames, last frame's
clipped toward the neighbours' colours in YCoCg, more of it kept the rougher the surface, and the lighting takes them
in place of the sky's reflection.

**Height fog** is an effect a world adds when it wants it, tuned where its effects list it: its `color`, the share of
the light reaching it that it scatters; its `intensity`, how much fog there is a metre at the densest; where it
`start`s, metres from the camera; and its `falloff`, a curve of how dense it is by height above the world's origin.
How much fog each pixel's ray crosses is the falloff's integral over the heights the ray goes through, exact for a
curve of straight lines, so it costs one fullscreen pass with no steps and no noise; the light it scatters is the
sky's from everywhere and the sun's, forward more than back, so it follows the time of day and the far world takes
the sky's colour. It is drawn over the lit scene and the sky, before transparent surfaces, which it does not fog, and
before the scene the reflections read is kept, so they show it.

**Ambient occlusion**, **contact
shadows**, **exposure** and **tonemapping** are effects too, reading the view's targets by name. Exposure is metered
one of two ways, by which effect a world names: from the view's pixels (`AutoExposure`), so it follows what the view
sees and a dark wall filling it brightens everything; or from the light its sun and sky shed on an open, level
surface (`IncidentExposure`), so what fills the view never changes it, for a world they light.

**Textures** are layers of eight texture arrays every surface pipeline binds, colour or data, each 256, 512, 1024 or
2048 texels with mips down to 4 x 4, compressed to BC7 on a worker as they load and kept in the cache, so they are
compressed once and take a quarter of the memory and of the reads.

**Nothing pops.** Nothing is drawn until it can be drawn as it should be, and what had to wait dissolves in:
- A material is drawn once its textures are in and every pipeline it is drawn with is compiled, for the listing being
  built while there is one, so nothing shows flat, black, missing or without the world's effects; until then its row
  stays as it was, so one packed again keeps being drawn the old way meanwhile.
- An instance is drawn once its model and its cards' atlases are in and each of its materials is drawn; until then its
  row is empty, so it is not drawn, casts nothing and is not in global illumination's field.
- One that waited dissolves in across the shaders' `FadeSeconds` from when it could be drawn, and its shadows' pages
  and the field are made again where it stands; one whose model and materials are drawn already appears at once, as a
  script spawning it means it to.
- A dissolve is a dither that moves each frame, so the upscale blends it.
- A domain is taken in before it spawns (Core's `IPreloader`): its models into the pool, its materials with their
  textures and every surface's pipelines, and the frame built for the effects it names while the world names none,
  so the loading domain is drawn with them and nothing is built again at the switch. The world spawns it once all of
  that is in, so a domain appears whole in its first frame, and only what a script spawns later dissolves in. What was
  taken in is held while the domain is open, so nothing of it is let go between coming in and being drawn.

## Passes and pipelines

A pass is a `.pass` file, Core's `Pass`: the stage it runs in, its shader, the names it writes and makes, and how it
runs; what it reads its shader says. A pipeline is a `.pipeline` file, an ordered list of passes and other pipelines.
The frame is two pipelines: the one the `pipeline` setting picks, and the effects the world's `PostProcessing` names.

- **Names.** The renderer's own (the scene's tables, the materials and their texture pools, the shadows' pool and
  tables, each view's targets) are declared in `ResourceInfo.Engine`; any other is made by a pass (`creates`) for
  itself and every pass after it, for the frame in a stage that runs once a frame, else for each view. A pass can see
  every name the frame has by the time it runs, its own among them, and its shader reads whichever it uses.
- **A shader binds what it uses.** Every name a pass can see is declared ahead of its shader, as it is read (a sampled
  texture with a sampler named after it, `HdrSampler`; an integer texture, which cannot be filtered, as a storage
  texture read texel by texel; a buffer), with `HAS_Name` defined for each, so a shader does without an effect's
  output the world does not draw. A probe compile finds which names it uses (`StageBindings`); those are bound, in the
  order the binding model wants, and the shader is compiled again for the GPU with them at the registers it is bound
  at, and checked to use no others. A draw's probe is its templates over the default surface, with what the surface
  contract offers any surface, so every surface of a draw is bound alike. How a texture is sampled wherever a shader
  samples it is the texture's: `"filter"` on its `creates` entry, nearest for depth and single-channel textures and
  linear for the rest when it says nothing, comparison for the shadows' pool.
- **Params.** A pass's shader declares `struct PassParams`, with defaults (decimal or hex, `0xFFFFu`), and reads it
  through `LoadPassParams()`; a pass file's `params` are packed over them, so one shader runs as several passes. A
  `Curve` member takes a curve asset, `{ "curve": { "id": 3363 } }`, the default curve when none is given, packed as
  its ends and 14 values between and read with `Include/Curve.hlsli`; editing the curve builds the listing again.
- **Indirect dispatch.** `"dispatch": { "args": "Name", "first": 2 }` runs as many groups as a pass before it wrote,
  three words a row, so work only the GPU knows the size of costs no readback. No pass writes what it is dispatched by.
- **Sizes and history.** A texture made with `"size": "output"` follows the view's target, not the size it is drawn at;
  one made with `"history": true` is two, `Name` written this frame and `NameHistory` the last, swapped each frame and
  emptied whenever they are made again.
- **Drawn passes** draw the visibility buffer's pieces a raster bin at a time, the G-buffer a surface's tiles at a time,
  transparent pieces farthest first, depth alone for shadows, or the sky; the renderer composes each surface variant's
  shader from the pass's template and the surface.
- **Script passes.** A pass of kind `script` names a `Script` asset, a class that is an `IPipelineScript`, which the
  renderer makes for each listing it builds, with the services its constructor asks for, and calls on the render
  thread to record the pass: a hook at a point of the frame for code that is no gem, a HUD, an overlay, the debug UI.
  The script is told the one texture the pass writes, its format and encoding, and draws into it with pipelines of
  its own (`PipelineScriptContext`). It is disposed with its listing.
- **The overlay stage** runs once for each target drawn into this frame, the window whether or not a view draws into
  it, after every view in the target is composed: its passes see the frame's names and `Target`, the target itself,
  which only a script pass draws into, as its format is the target's own. The frame runs the overlay pipeline the
  `overlay` setting names, else `Pipelines/Overlay.pipeline` from whichever gem or project has it, the debug UI's
  among them.
- **Reloading.** Changing the world's effects, or editing a pipeline or shader, builds the whole listing again on
  workers; the frame draws with the old one until the new one is compiled, with every surface the frame draws by then,
  then swaps. What first shows meanwhile waits for the new one (see Nothing pops). One listing builds at a time: a
  change while one is under way waits for it to swap in, then one build follows for everything that changed
  meanwhile, so a burst of changes (every shader remade, one at a time) is one further build rather than one each.
  Before the first is compiled, the window is cleared to black, as it is whenever no camera draws into it, and the
  same waiting draws the loading domain as soon as it can be.

## Debugging

- **Debug views.** A debug view is a pass in `Passes/Debug/`, named for the one thing it shows (the depth, the
  normals, which lights reach each pixel, the sun's shadow as the lighting reads it) with its shader in
  `Shaders/Debug/`: a fullscreen pass that reads what it needs by name, as any pass does, and draws into `DebugView`,
  which each view then shows in place of its finished image. While `IGpu.DebugView` names one, its pass runs after the
  world's effects, in the `ui` stage unless it needs something an earlier stage overwrites (what the surfaces emit,
  before the lighting adds to it, is read in the G-buffer stage). A view draws black where nothing was drawn, flat
  teal where it reads an effect's output the world does not draw, and never magenta, which says that something
  failed; one that shows a value or overlays the scene draws over a faint grey of the albedo, so the scene's shape
  reads whatever the value (`Shaders/Debug/View.hlsli`). While the pass is not ready, the view shows the failure's
  magenta saying that nothing draws (`Passes/Nothing.frag.hlsl`), never an image from an earlier frame. A project
  adds a view by adding a pass there, with a `description` of what it shows in a sentence; it is listed in the
  settings window and taken by the `view` command by its file name, and the debug UI says its name and description
  on screen while it is shown.
- **Failures are drawn, never hidden.** A material that does not load or whose surface does not compile, one that
  samples a texture that does not load, and a model that does not load are drawn as the failure material for what
  broke (Core's `Assets.Failure<Material>`: solid magenta, a red checker, and solid red on the failure cube), breathing
  so nothing takes them for real, in the material's own row, so the instance that names it changes nothing. A pass that
  does not run (it does not bind, probe or compile, or its pipeline does not load) has what it would draw into cleared
  to the failure's magenta each frame, a fullscreen pass's target or a draw's colours, and the frame says why in words
  across every view: a box of magenta in the view's bottom left (`Include/FailureBanner.hlsli`), each failure wrapped
  over the rows it takes, drawn
  by the output over the finished image, in the text a shader draws itself, so it is in a screenshot and a Release
  build alike. What only the GPU finds wrong, a lit pixel that is NaN or a material row past the table, a shader draws
  as the failure where it is and raises as a code in the frame's `Failures` buffer (`Include/Failure.hlsli`), a word
  each, which the box says too; a compute pass that raises one lists `Failures` among its writes.
- **Failure labels.** While the debug UI shows, every instance drawn as the failure says why at its place.

## What it does not do yet

- A masked material casts the shadow of its whole mesh, and cards cast none: an instance casts with its clusters, as
  coarse as its shadow's texels allow.
- A light that moves draws all its pages again every frame, which past `shadowUpdates` leaves them read coarse.
- A second view runs the shadows' passes again, and takes the lights' pages over from the first.
- Reflections are traced for opaque surfaces only: a transparent one, glass or water drawn as transparent, reflects the
  sky. What only the field holds is reflected as coarsely as the field holds it, flat-coloured, a voxel to half a metre
  and more.
- A texture's mips are all resident; none is streamed by what the view reads of it.
