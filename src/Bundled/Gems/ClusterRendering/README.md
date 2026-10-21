# ClusterRendering

Draws the world GPU-driven, as clusters at the detail each part of it needs: each camera's view of the world's
triangles into a visibility buffer, each pixel shaded once into a G-buffer and lit once, then the sky, transparent
surfaces lit as they are drawn, the view upscaled to its target's size, and the world's effects. Every shadow, the
sun's and every point and spot light's, is pages of one virtual shadow map. It takes the GPU, a shader compiler, the
assets, the cache, the world, the events, the lifetime and the engine's threads, and a window, the debug UI and the
scripts when there are, the scripts to make the pipeline scripts a frame names; it provides the cache's ways of
keeping a model's distance field and a texture compressed for its pool.

The CPU only keeps the GPU's tables in step with the world, a row for each thing that changed, and records a fixed list
of passes. Which instances and clusters are drawn and how finely, which lights reach which pixels, which shadow pages
are wanted, kept, given memory and drawn: all of it the GPU works out each frame, without reading anything back.

## Settings

Each setting is documented on `ClusterRenderingSettings` and shown in the settings window. They size what the renderer
allocates, so they belong to the machine, and every one is read every frame: a newly named pipeline runs once it is
built, a new anisotropy makes the material sampler again. The frame runs the pipeline the settings name, before the
world's effects: by default the clusters pipeline in the bundled resources, the whole frame described here, so a
project names a pipeline only to run another.

## Passes and pipelines

A pass is a `.pass` file, Core's `Pass`, whose doc comments document its keys: the stage it runs in, its shader, the
names it writes and makes, and how it runs; what it reads its shader says. A pipeline is a `.pipeline` file, an
ordered list of passes and other pipelines ([Core's README](../../../Core/README.md#the-gpu)). The frame is two
pipelines: the one the settings pick, and the effects the world's `PostProcessing` names.

- **Names.** The renderer's own (the frame-wide tables, the materials and their texture pools, the shadows' pool and
  tables, each view's targets) are declared by the renderer; any other is made by a pass for itself and every pass
  after it, for the frame in a stage that runs once a frame, else for each view. A pass can see every name the frame
  has by the time it runs, its own among them, and its shader reads whichever it uses.
- **A shader binds what it uses.** Every name a pass can see is declared ahead of its shader, as it is read, with a
  macro defined for each saying that it is there ([Shaders](../../Resources/Shaders/README.md#writing-a-shader)). A
  probe compile finds which names it uses; those are bound, in the order the binding model wants, and the shader is
  compiled again for the GPU with them at the registers it is bound at, and checked to use no others. A draw's probe is
  its templates over the default surface, with what the surface contract offers any surface, so every surface of a
  draw is bound alike. How a texture is sampled wherever a shader samples it is the texture's: a filter the pass that
  makes it states, nearest for depth and single-channel textures and linear for the rest when it says nothing,
  comparison for the shadows' pool.
- **Params.** A pass's shader declares its params struct, with defaults, and reads it through the shader library; a
  pass file's params are packed over them, so one shader runs as several passes. A curve member takes a curve asset
  ([Curves](../../Resources/Curves/README.md)), and editing the curve builds the listing again.
- **Indirect dispatch.** A pass dispatched indirectly names the pass before it whose output holds the group counts,
  so work only the GPU knows the size of costs no readback. No pass writes what it is dispatched by.
- **Sizes and history.** A texture a pass makes at the output's size follows the view's target, not the size it is
  drawn at; one made with history is two, one written this frame and one holding the last, swapped each frame and
  emptied whenever they are made again.
- **Drawn passes** draw the visibility buffer's pieces a raster bin at a time, the G-buffer a surface's tiles at a time,
  transparent pieces farthest first, depth alone for shadows, the sky, or its shadow on the world; the renderer composes
  each surface variant's shader from the pass's template and the surface, and the sky's and its shadow's from their
  templates and the sky shader.
- **Script passes.** A pass of kind `script` names a `Script` asset, a class that is an `IPipelineScript`, which the
  renderer makes for each listing it builds, through the scripting gem, with the services its constructor asks for, and
  calls on the render thread to record the pass: a hook at a point of the frame for code that is no gem, a HUD, an
  overlay, the debug UI. The script is told the one texture the pass writes, its format and encoding, and draws into it
  with pipelines of its own. It is disposed with its listing.
- **The overlay stage** runs once for each target drawn into this frame, the window whether or not a view draws into
  it, after every view in the target is composed: its passes see the frame's names and the target itself, which only
  a script pass draws into, as its format is the target's own. The frame runs the overlay pipeline the settings name,
  else the overlay pipeline at the path the engine expects, from whichever gem or project has it.
- **Reloading.** Changing the world's effects, or editing a pipeline or shader, builds the whole listing again on
  workers; the frame draws with the old one until the new one is compiled, with every surface the frame draws by then,
  then swaps. What first shows meanwhile waits for the new one ([Nothing pops](#nothing-pops)). One listing builds at a
  time: a change while one is under way waits for it to swap in, then one build follows for everything that changed
  meanwhile, so a burst of changes (every shader remade, one at a time) is one further build rather than one each.
  Before the first is compiled, the window is cleared to black, as it is whenever no camera draws into it, and the
  same waiting draws the loading domain as soon as it can be.

## A frame

### The copy

In LateUpdate, while the render thread may still be drawing the frame before, what Render reads is copied into the
copy it is not reading. One system on the main thread reads what the world has one of, or a few: the cameras, the
player, the sun, the sky, the weather and the post-processing. Systems on the ECS's threads then copy each instance's
transform and renderer and each light, kept at its entity's slot and compared with what was there, so only what
changed is passed on. Sync swaps the two copies, and Render reads the one just finished.

Every shader works with positions relative to the camera, the frame's camera position taken from each, so what is
near the camera is as exact as floats are near zero, wherever the camera stands.

### Instances, lights and materials on the GPU

Instances, lights and materials are tables kept by slot and row, uploaded a run of changed rows at a time. An instance's
row holds its transform and the last frame's, its model's row, its material slots' rows, and when its fade in started,
which the GPU times it from. Models go into one pool of geometry, nearest the player first and no more a frame than its
upload rate: each cluster's vertices are a few words (its position in steps of its model's grid, its normal and tangent
on the octahedron, its uv as halves) and its triangles a word each. A model with no cluster hierarchy, one a gem made,
is cut into clusters as it is, of one level. Cards (Core's `Card`) take a row each and take over from a model's clusters
far off, across a band where both dissolve into each other, once their atlases are in: until then, or for good when one
fails, the model's clusters are drawn at every distance. A card shows the two views of its atlases either side of the
camera, dithered by how far round from one to the next it is, so it turns smoothly as the camera goes round it.

An asset stays on the GPU while something holds it: the instances drawing one model with the same materials hold
those, a material holds the textures it samples, a model holds its cards' atlases, a view holds the render texture
it draws into, and a domain being taken in holds everything it draws. What nothing holds any more is let go once a
frame, so an asset let go and held again within a frame never leaves, and a texture shown by two things leaves only
when both let it go.

### Culling

Each view culls its instances, then their clusters, in two phases (Karis 2021; Haar & Aaltonen 2015). The first tests
what is in view against last frame's depth pyramid, seen through last frame's view, and draws what it does not hide;
the second builds a pyramid of what was drawn and tests again everything the first hid, so what came into sight is
drawn the same frame. An instance hands only the levels of its model's hierarchy whose error, from as near and as far
as it reaches, can be what the view wants there. A cluster is drawn where its error, projected from its group's sphere
as near as it comes, covers no more than the error the settings allow in pixels and its parent's does: every cluster
decides alone, and as a group's clusters share their sphere and error the cut meets without cracks.

Detail is shared out nearest first. Each view counts the pieces it drew in buckets by distance from the player's
camera, a few to a doubling. A bucket whose pieces, with every nearer bucket's, would pass most of the pieces the
renderer has room for draws coarser the next frame, its error allowed twice as many pixels each frame it would not
fit and a little fewer each frame they would fill well under it, holding in between so detail settles rather than
going back and forth; no bucket is ever finer than a nearer one. Overflow never drops anything near: what is far
goes coarse.

### The visibility buffer and the G-buffer

A cluster's triangles are drawn in small pieces, each an instance of one fixed draw whose vertex stage reads the
piece's vertices itself. Pieces are laid out by raster bin: the two that do not clip (single and double sided), and
each surface's own and its cards', which clip on its alpha or on a dissolve. Each pixel keeps the name of the
triangle in front, its piece's slot and which of the piece's triangles, and its depth.

The view's small tiles are sorted by the surfaces their pixels show, and each surface draws a quad over each of its
tiles, evaluating its surface once a pixel for the triangle the visibility buffer names, with the pixel's place on it
from the triangle's homogeneous coordinates (Olano & Greer 1997) and its uv's derivatives from its neighbours'. Every
surface is the same `.surf.hlsl` it would be anywhere. The pixel's velocity, from where its instance was last frame,
is written beside it.

### Lights

Every point and spot light is a row of a table on the GPU, written when it changes. Each view culls them, sorts those
in view into depth slices by a count and a prefix, nearest first, gives each slice the first and last light that
can reach it, and each tile of pixels a bit for each light that reaches it, each light setting the bits of the tiles
its sphere covers (Drobot 2017). A pixel walks only the lights in both, the walk kept uniform across a wave.

### Virtual shadow maps

Every shadow is drawn into pages of one depth pool, each with a border for filtering, only the pages the view's pixels
read, kept from frame to frame and drawn again only where what casts into them moved: of a page, only the rectangle a
moving thing reaches is emptied and drawn, so a small caster moving in a big page costs little. The sun's pages are a
clipmap of as many levels as the settings say about the camera, a level's window moving a page at a time with its
entries addressed round so a page that stays keeps what was drawn. A point or spot light's pages are its six faces' at
a mip, a texel at most two pixels wide; its coarsest mip is one small page, a fraction of a page, which a pixel is
shaded through while its own waits. Each frame, on the GPU:

- each pixel marks the pages its lighting can read: every sun level from one finer than its own to three coarser than
  the coarsest its dither can pick, each at the point the lookup reads it there, and each shadowed light that reaches
  it, a light's pages put into a hash table made again each frame;
- the sun's entries follow their window, and a light's take over the pages they had last frame, each page noting the
  rectangle of it that what moved since reaches;
- one group gives the pages wanted the pool's: each light's small page first, so its pixels always have a drawn page to
  fall back on, then the sun's levels, coarsest first, as a coarse page covers four times the ground of the next finer,
  so after a turn every pixel soon has some shadow, which then sharpens, then lights' whole pages by how far from the
  player they are; the pages read longest ago taken first, then, when the pool is full, pages read this frame from
  lights farther than what wants them, which fall back on their coarser mips (never the sun's, whose coarser levels
  nearer pixels' soft shadows are filtered through); and picks pages waiting to be drawn, up to the count the settings
  allow a frame, by the same priority: half as many after a frame whose culls ran out of room for casters, and twice
  as many again, up to that count, after one that did not;
- casters are culled by instance, then by cluster at each page's own texel size, against the rectangle of each page
  drawn, and drawn into it, the rectangle emptied first. A frame whose culls ran out of room draws nothing, as its pages
  would have holes where the casters left out should be: every page of it waits again, read coarser till the next
  frame draws it. Depth is clamped rather than clipped, so a caster nearer the sun than a page holds still casts; a
  light's faces are clipped a few centimetres ahead of it instead.

A page is never read where it waits: a sun page waiting only where something moved is read everywhere else on it, as
the rest still holds what it did, and a page waiting whole, or a light's, is not read at all, the pixel reading the
next coarser level, or mip, that is drawn. Where one sun level gives way to the next, a pixel picks between the two by
a dither that moves each frame, which the upscale blends, so the levels meet without a line. The sun's penumbra is as
wide as its disc makes it (PCSS), widened through the clipmap's coarser levels rather than more taps, but never past
the three coarser the pixel marked, nor finer than the one finer, so a shadow sharpens as the camera nears it, to half
a pixel, and picked between two levels by the same dither, so it never steps from one to the next. The casters it is
measured from are searched for at the widest of those levels, over all its filter reaches there, so every pixel the
filter could darken finds them and the penumbra fades out all the way to light; where no level from there on holds a
page yet, none is found, and the pixel is filtered at its own level rather than left lit. A receiver is pushed out
along its normal by a texel of whichever level it reads, and compared a texel of depth nearer the sun, so a surface
facing the sun does not shadow itself.

### Temporal upscaling

The view is drawn a sub-pixel off each frame (Halton 2, 3), and textures are read a little sharper as it is drawn
smaller. An upscale (Karis 2014) gathers the view's pixels about each pixel of the target, weighed by how near their
samples really were, and blends them into what it held there last frame, found by the nearest surface's velocity and
read through a Catmull-Rom filter, clipped toward the colours about it now (Salvi 2016), both weighed by their
brightness. Tonemapping reads what it made, at the target's size.

### Transparency

Transparent instances are sorted farthest first on the CPU and drawn forward through the same pieces, their finest
clusters, lit as they are drawn and reading a copy of the lit scene behind them.

### Global illumination

An effect: probes that see the world as a signed distance field, made from each model's field, which the GPU makes
once from its coarsest clusters and the cache keeps. The field and the probes are nested levels about the camera, kept
from showing where they meet or lagging behind a change:

- A place is lit by the finest level it lies well within, and blended into the next toward that level's edge, so
  where one level gives way to the next moves with the camera, never in the steps a level moves by; a level whose
  probes cannot see a place hands it on by as much, smoothly.
- A probe blends each tracing into what it held by how long it waited, so light follows a change in a fraction of a
  second at the finest level, a little longer each coarser one, at any frame rate.
- A probe that comes into use, as a level moves on, starts from the next coarser level's light where it is, the sky's
  at the coarsest, rather than from nothing, and keeps what it holds while it is out of use.
- A ray that runs out of steps creeping along a surface takes it as hit rather than letting the sky in, and a model
  only a few voxels big is no fatter in the field than its own field reaches, not a solid box, while a floor or a wall
  thinner than a voxel is still closed, so no ray passes through it.
- The field holds each surface's colour as matte, less as much of it as the material is metal: a metal only mirrors
  light, which the field cannot, so it holds none rather than glowing in the sun.
- What a ray hits is lit by the sun and by the point and spot lights that reach it, each through the field, so lamps
  bounce as the sun does. Each probe's cell, where it lies in the world, lists the few lights that reach it most
  strongly, made again each frame on the GPU from those that reach the field at all, found first among the table's
  slots, so the list follows lights that move and stays as a level moves.

### The sky

The world's sky material names a sky shader, drawn along each direction over what nothing was drawn on, from the sun
and the weather the frame's constants hold. The sun is the directional light tagged as the sun: there is one, and the
world says which it is, so a directional light that is not tagged lights nothing, which the log says once. The weather,
still air and a clear sky without one, reaches every pass the same way, so whatever the wind should move can read it.

A sky material is a row of the materials as a surface's is, its params packed the same way, but no mesh draws it, so
it has no bins and no pipelines but the sky's: a mesh that names one is drawn as the shader failure. A sky whose
material is no sky, a surface's or a sky's that does not compile, is drawn with the sky failure material in its place,
as a broken material is drawn with a failure material: magenta in the view, and a plain grey sky in the light it sheds,
so the world it lights stays lit. Why is said in the log once and on the failure banner for as long as it stays broken.
A world with no sky material has the renderer's own, a gradient as bright as the sun makes it.

### The sky's shadow

What the sky has in front of the sun, its clouds, shades the world: the sun reaches each place only by the share the
sky lets through toward it. The sky shader says that share for any place, and the renderer draws it, composed into a
template of its own as the sky is, into a small texture over the ground around the camera each frame, so the shadows
cross the ground as the wind carries the clouds. Everything the sun lights reads it by name: the lighting of each
pixel, times the sun's shadow map, so an overcast sky leaves no hard shadow as it leaves no sun; the light the global
illumination's rays find; and, at the camera's own place, the exposure metered from the world's light and the sun's
light the fog scatters. The texture covers a level square through the camera, its middle moved in whole steps of the
world so the shadows of still clouds stand still, and a place is looked up where its way toward the sun crosses it,
which serves every height below the clouds. It is read through a cubic filter rather than a bilinear one, as up close a
texel covers much of the view and a bilinear read would show the texels' grid along a cloud's edge. The share under the camera is the share the view sees of the sun's disc,
so the ground goes dark as the disc does. A world with the renderer's own sky, or a broken one, has nothing in front of
the sun.

### The sky's light

Captured each frame into a small cube, projected onto nine spherical-harmonic coefficients for light from everywhere,
and prefiltered by GGX into a cube with a mip for each of a few roughnesses (filtered importance sampling, Křivánek &
Colbert 2008), which every surface reflects the sky by.

### Reflections

An effect: each pixel smooth enough to show them, past a roughness the settings set none, traces a ray from its GGX
lobe, a new one each frame, through global illumination's field when the world draws it, and what it hits is lit as
the field lights it: by its probes, the sun and the lights near it, through the field's own shadows; past everything,
the sky as rough as the surface. Nothing is read from the view, so a reflection shows the same whatever is on screen,
what is behind the camera or hidden as much as what is in sight, and only as coarsely as the field holds it, a voxel
of flat colour with no highlights of its own. A ray sets out through the field from the finest level that holds its
surface, a voxel and a half of that level off it, so a far surface's ray does not start inside its own coarse copy;
moved out of the field along its gradient where it starts inside another surface's fattened band, the foot of a wall
within the floor's; and a voxel along the ray, past the field's rounding of its own corners, which a ray leaving a
corner at a grazing angle would otherwise meet. A rough surface traces two rays and takes their mean, as its lobe is
wide. The rays are filtered across the surface, over a wider patch the rougher it is, and blended over frames, last
frame's clipped toward the neighbours' colours in YCoCg, more of it kept the rougher the surface; and the lighting
takes them in place of the sky's reflection.

### Height fog

An effect a world adds when it wants it, tuned where its effects list it: its colour, the share of the light reaching
it that it scatters; its intensity, how much fog there is a metre at the densest; where it starts, metres from the
camera; and its falloff, a curve of how dense it is by height above the world's origin. How much fog each pixel's
ray crosses is the falloff's integral over the heights the ray goes through, exact for a curve of straight lines, so
it costs one fullscreen pass with no steps and no noise; the light it scatters is the sky's from everywhere and the
sun's through the sky's shadow at the camera, forward more than back, so it follows the time of day and the weather and
the far world takes the sky's colour. It is drawn
over the lit scene and the sky, before transparent surfaces, which it does not fog, and before the scene the
reflections read is kept, so they show it.

### Other effects

Ambient occlusion, contact shadows, exposure and tonemapping are effects too, reading the view's targets by name.
Exposure is metered one of two ways, by which effect a world names: from the view's pixels, so it follows what the
view sees and a dark wall filling it brightens everything; or from the light its sun, through the sky's shadow, and its
sky shed on an open, level surface at the camera, so what fills the view never changes it, for a world they light.

### Textures

Layers of a few texture arrays every surface pipeline binds, colour or data, one per size from small to large, with
mips, compressed to BC7 on a worker as they load and kept in the cache, so they are compressed once and take a
quarter of the memory and of the reads.

### Nothing pops

Nothing is drawn until it can be drawn as it should be, and what had to wait dissolves in:

- A material is drawn once its textures are in and every pipeline it is drawn with is compiled, for the listing being
  built while there is one, so nothing shows flat, black, missing or without the world's effects; until then its row
  stays as it was, so one packed again keeps being drawn the old way meanwhile.
- An instance is drawn once its model and its cards' atlases are in and each of its materials is drawn; until then its
  row is empty, so it is not drawn, casts nothing and is not in global illumination's field.
- One that waited dissolves in over the fade the shaders set from when it could be drawn, and its shadows' pages
  and the field are made again where it stands; one whose model and materials are drawn already appears at once, as a
  script spawning it means it to.
- A dissolve is a dither that moves each frame, so the upscale blends it.
- A domain is taken in before it spawns (Core's `IPreloader`): its models into the pool, its materials with their
  textures and every surface's pipelines, and the frame built for the effects it names while the world names none,
  so the loading domain is drawn with them and nothing is built again at the switch. The world spawns it once all of
  that is in, so a domain appears whole in its first frame, and only what a script spawns later dissolves in. What was
  taken in is held while the domain is open, so nothing of it is let go between coming in and being drawn.

## Debugging

- **Debug views.** A debug view is a pass in `Passes/Debug/`, named for the one thing it shows (the depth, the
  normals, which lights reach each pixel, the sun's shadow as the lighting reads it) with its shader in
  `Shaders/Debug/`: a fullscreen pass that reads what it needs by name, as any pass does, and draws into the debug
  view's texture, which each view then shows in place of its finished image, drawn without the upscale's sub-pixel
  jitter meanwhile, so it holds still. While one is named (told on `Events`), its pass runs after the world's
  effects, in the UI stage unless it needs something an earlier stage overwrites (what the surfaces emit, before the
  lighting adds to it, is read in the G-buffer stage). A view draws black where nothing was drawn, flat teal where it
  reads an effect's output the world does not draw, and never magenta, which says that something failed; one that
  shows a value or overlays the scene draws over a faint grey of the albedo, through the debug views' shared header,
  so the world's shape reads whatever the value. While the pass is not ready, the view shows the failure's magenta
  saying that nothing draws, never an image from an earlier frame. A project adds a view by adding a pass there, with
  a description of what it shows in a sentence; it is listed in the settings window and taken by the `view` command
  by its file name, and the debug UI says its name and description on screen while it is shown.
- **Failures are drawn, never hidden.** A material that does not load or whose surface does not compile, one that
  samples a texture that does not load, and a model that does not load are drawn as the failure material for what
  broke (Core's failure asset for it), breathing so nothing takes them for real, in the material's own row, so
  the instance that names it changes nothing. A pass that does not run (it does not bind, probe or compile, or its
  pipeline does not load) has what it would draw into cleared to the failure's magenta each frame, a fullscreen pass's
  target or a draw's colours, and the frame says why in words across every view, in the failure banner the output
  draws over the finished image ([Shaders](../../Resources/Shaders/README.md#writing-a-shader)), so it is in a
  screenshot and a Release build alike. What only the GPU finds wrong, a lit pixel that is NaN or a material row past
  the table, a shader draws as the failure where it is and raises as a code in the frame's failures buffer, which
  the banner says too; a compute pass that raises one lists that buffer among its writes.
  Which stand-in replaces what broke, what is said and what the banner shows are decided in one place, which the
  parts that find a failure (loading, compiling, the world copy) report to, so a failure looks the same wherever it is
  found; the output only draws what it decided.
- **Failure labels.** While the debug UI shows, every instance drawn as the failure says why at its place.

## Limits

- A masked material casts the shadow of its whole mesh, and cards cast none: an instance casts with its clusters, as
  coarse as its shadow's texels allow.
- A light that moves draws all its pages again every frame, which past the pages a frame may draw leaves them read
  coarse.
- A second view runs the shadows' passes again, and takes the lights' pages over from the first.
- Reflections are traced for opaque surfaces only: a transparent one, glass or water drawn as transparent, reflects the
  sky. What is reflected is the field, flat-coloured and without highlights, a voxel to a quarter of a metre close by
  and coarser further off; and nothing where the world draws no global illumination, the sky alone.
- A texture's mips are all resident; none is streamed by what the view reads of it.
- The sky's shadow covers a few kilometres around the camera and is the square's edge's past it, so a far hill is shaded
  by the cloud over the edge, not its own. Its texels are metres across and read through a smooth filter, so a cloud's
  edge sharper than a few of them is softened rather than drawn sharp. A place above the clouds is shaded as if it were below them. Thin cloud dims the sun's shadows but does not
  soften them, as the sun's light seen through cloud spreads wider than its disc.
