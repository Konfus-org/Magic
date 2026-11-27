# Rendering

The rendering gems draw the world into the window. The renderer, `DeferredRendering`, keeps the world on the GPU and
lights it. The other gems add parts of the picture: the geometry, the world as rays see it, shadows, indirect light,
reflections, the sky, the air, the upscaling and the post effects. A project lists the gems that it wants in its
`.magic` file.

Each gem has a `Renderer` part with one or more render steps. Core runs a step after the steps that make its inputs.
A step that takes `RenderViews`, or the output of such a step, runs one time for each view of the frame. This diagram
shows what each step makes and which steps take it.

```mermaid
flowchart LR
    World[DeferredRendering world] -- GpuWorld, RenderViews --> View[DeferredRendering view]
    World -- GpuWorld --> Pools[ClusteredGeometry pools]
    World -- GpuWorld, GpuInstances --> Accel[AccelerationStructures]
    World -- GpuWorld --> Post[PostEffects]
    Taa[TaaUpscaling] -- Upscaling --> View
    Post -- PictureEffects --> View
    Accel -- TracedWorld --> View
    View -- GpuView --> Clusters[ClusteredGeometry]
    View -- GpuView --> Sky[ProceduralSky]
    Sky -- SkyTables --> GI
    Sky -- SkyTables --> Shading
    Sky -- SkyTables --> Refl
    Pools -- ClusterPools --> Clusters
    Accel -- TracedWorld --> Shadows[TracedShadows]
    Accel -- TracedWorld --> GI[GlobalIllumination]
    Accel -- TracedWorld --> Vol[WeatherFX]
    Shadows -- ShadowMask --> Shading[DeferredRendering shading]
    View -- GpuView --> Fit[ShadowMaps fit]
    Fit -- CascadeViews --> ShadowDepth[ClusteredGeometry shadow depth]
    Pools -- ClusterPools --> ShadowDepth
    ShadowDepth -- CascadeDepth --> Resolve[ShadowMaps resolve]
    Fit -- CascadeViews --> Resolve
    Resolve -- ShadowMask --> Shading
    World -- GpuWorld, GpuInstances --> LocalFit[ShadowMaps local fit]
    LocalFit -- LocalShadowViews --> LocalDepth[ClusteredGeometry local depth]
    LocalDepth -- LocalShadowDepth --> LocalResolve[ShadowMaps local resolve]
    LocalResolve -- LocalShadows --> Shading
    View -- GpuView --> Gtao[AmbientOcclusion]
    Gtao -- AmbientOcclusion --> Shading
    View -- GpuView --> SkyLight[LightProbes sky light]
    Sky -- SkyTables --> SkyLight
    SkyLight -- SkyLight --> Shading
    World -- GpuWorld, GpuInstances --> SkyFit[LightProbes sky fit]
    SkyFit -- SkyViews --> SkyDepth[ClusteredGeometry sky depth]
    SkyDepth -- SkyDepth --> SkyProbes[LightProbes sky probes]
    SkyProbes -- SkyProbes --> SkySeen[LightProbes sky visibility]
    SkySeen -- SkyVisibility --> Shading
    GI -- IndirectLight --> Shading
    Accel -- TracedWorld --> Refl[TracedReflections]
    GI -- IndirectLight --> Refl
    Refl -- Reflections --> Shading
    Vol -- Air --> Shading
    Post -- PictureEffects --> Shading
    Clusters -. GBuffer .-> Shading
    Shading --> Banner[Failure banner]
```

- An input with a nullable type is optional. Without the gem that makes it, the step that takes it runs without it.
- The gems that trace rays need a GPU with ray queries. On a GPU without them, these gems make nothing.
- The settings of the rendering gems are in the `<Gem>Settings` class of each gem. `query Rendering` and `query Debug`
  list them, and `set` changes them, as `set Rendering.ShadowScale 1`.
- The shaders of a gem are the `Shaders/` modules in its `Resources` folder. They import the shader library
  ([Shaders](SHADERS.md)).
- `bake` in the console loads each asset, which makes what each gem makes of each model. `bake <path>` loads one
  asset, or each asset under a folder. It opens no domain: a compiled shader is cached when a domain first draws
  with it.

## DeferredRendering

The renderer. It has four render steps:

1. The world, one time for each frame: it puts the models, materials, textures, render textures, instances and lights
   of the world on the GPU. It makes the `GpuWorld`, the `GpuInstances` and the `RenderViews` of the frame.
2. The view, one time for each view: it makes the targets and the frame buffer of the view, as its `GpuView`.
3. The shading, one time for each view: it lights the GBuffer that a geometry gem drew, with the sun, the sky and the
   lights. Then it runs the `PictureEffects` of the frame and writes the picture into the target of the view.
4. The failure banner: it fills the window with black under the views. Then it draws the cover of a load and the
   render failures of the frame over the window.

The gem also holds the types that the rendering gems share. List it in the `.magic` file after the GPU provider, the
ECS, the model importer and the texture importer, with a gem that draws its geometry. The settings are in
`DeferredRenderingSettings`.

### Models and loading

- A model shows when it is on the GPU. A model that nothing drew for `Rendering.ModelKeepSec` leaves the GPU. It
  loads again when a frame draws it.
- A renderer with no materials draws each mesh of its model with the default material. When a renderer has a
  material in one or more slots, a mesh whose slot is empty does not draw, casts no shadow, and rays go through it.
  To open a door or a window, leave the slot of that part empty.
- When a domain replaces a world that shows, the cover fades in over that world. Behind the full cover, the loading
  screen comes in its place. Then the cover fades out into the loading screen. Without a world that shows, the
  loading screen shows at once.
- The loading screen stays until all that the new world draws is on the GPU or
  failed. This includes the passes of the gem.
- Then the cover fades in over the loading screen. The cover is one colour over the window. The debug UI and the
  failure banner show over it. An empty world gets the full cover at once.
- Behind the full cover, the new world comes in the place of the loading screen, and its time stands still. Each gem
  that blends its picture over frames starts its history again and settles: the shadows, the indirect light, the
  reflections, the air and the upscaling.
- The cover fades out when each gem settled. A gem that does not settle in time does not stop the load. The world
  then shows as it is, and the log has a warning.
- The cover over the new world is an off-white when the light that lights it is a sun or a light alone, above the
  horizon, with an intensity above 0. A world with no directional light also gets an off-white cover. A moon, a light
  below the horizon or a light of intensity 0 gives a black cover. The cover over the old world is black.
- While the cover is full, the post effects keep no history, so the exposure takes the value of each frame. When the
  cover fades, the exposure adapts from the settled picture.
- Each frame, the renderer fills the window with black before the views draw. A part of the window that no view draws
  shows black.

### Cameras

- Each camera is a view. A camera draws into its section of the window, or into its render texture at the size of
  the texture. Without a camera for the window, a camera at the origin draws into it.
- A camera for a part of the window draws over the cameras for all of it. Cameras with different sections give a
  split screen.
- A camera for the window draws at `Rendering.FieldOfView` when that is more than 0.
- A material samples a render texture when a texture param names it as `renderTexture`. The cameras for render
  textures draw first, so the window shows the picture of this frame.

### Picture size

- The picture is `Rendering.Resolution` in size. At `0 0`, it is the size of the window, or 1280 by 720 without a
  window.
- The world draws at `Rendering.RenderScale` of that size. At `0`, it draws at the largest size that fits in 1920 by
  1080.
- A gem that upscales takes the picture to its full size. Without one, the renderer stretches it with
  `Rendering.UpscaleFilter`.
- A screenshot is the picture as the window shows it, without what other gems draw over it.

### Lights and the sky

- A pixel with no geometry shows the sky. The material of a sky entity draws the sky.
- The `Brightness` of a `Sky` multiplies the sky that is seen, the light the sky gives the surfaces and what the haze
  scatters. The sun and the moon keep their own light. 1, the default, is the sky of the air itself.
- A sky gem makes the sky tables. Without a sky entity or a sky gem, there are no sky tables. The shading and the
  indirect light then use `OpenSky`, and the open sky lights the world at `Rendering.SkyBrightness`.
- The directional light is the sun while it is above the horizon. Else it is the moon while the moon is above the
  horizon. Else it is the first directional light.
- Lights use physical units: lux for the sun, candela for point, spot and rect lights. A frame draws the first 1024
  lights.
- Where a gem made a shadow mask, the mask shadows the surfaces. Else nothing is shadowed.
- Where a gem made indirect light, the surfaces take it. Else the sky lights them. A surface with a roughness under 0.3
  shows the reflections.
- The indirect light gem traces the diffuse light and denoises it with NVIDIA's NRD (RELAX_DIFFUSE).
- A shadowed surface keeps a small part of the light that its lights give it unshadowed, so a shadow is not black.
  With indirect light, the sun gives this part only where the surface sees enough of the sky.
- Where a gem filled the air, the picture shows the air in front of each surface.

### Glass

A translucent material, such as the `Glass` material, draws over the lit picture. The nearest pane in front of each
pixel lets the light behind it through, by its colour. Where a gem builds the world as rays see it, the light also
goes through the panes behind it. The pane reflects the light around it, and the sun lights it.

### Failures

A model that does not load draws as a flashing teal cube of one metre, with "Model failed" and its path. Other
failures flash on the mesh of their entity ([Debugging](DEBUGGING.md)).

- Each failure that a gem or Core gives to `RenderFrame.Fail` in a frame is one line of the banner. The banner shows
  eight lines. The last line counts the rest.
- A failed post effect flashes a blue frame round the window, with its line at the bottom. The other effects run.
- A failed sky flashes coral with "Sky failed". Failed clouds flash a green band with "Weather failed".
- The shader of the banner is `Embedded/FailureBanner.slang` in the assembly of the gem, at
  `Shaders/FailureBanner.slang`. To replace it, put a file at that path in the project.

### Debug views

`DeferredRendering.RenderView` shows one part of the GBuffer, the indirect light, the shadow maps, the ambient
occlusion or the sky visibility alone. `Final` shows the finished frame. The
names are in `DebugView`. With a gem that gives the debug UI, F2 opens the Rendering window. F5 and F6 step through
the views.

To measure the light against a path tracer, use the reference views ([Debugging](DEBUGGING.md#reference-views)).
`screenshot --hdr` saves the frame's lit light before the exposure, in nits, as a PFM. The camera does not jitter
in that frame, so the light is at the middle of each pixel and has no anti-aliasing. In a debug view it still saves
the lit light, not the view.

### Change the renderer

- `Renderer` has one `Render` method for each step. Each method is its step, in order from top to bottom.
- `GpuResources` reads a texture, material or model asset when a frame first asks for it.
- `GpuPacker.Pack` in Core writes the row of a shader struct from its reflected fields and an `IGpuParams` source. The
  renderer packs its material rows with it, and the post-effects gem packs its effect rows with it. `MaterialParams`
  is the source for a slot of a material. A gem gets the heap ids of a material param's texture from
  `GpuWorld.GetTexture`.
- A pass is one `RenderGraph.Compute` or `RenderGraph.Draw` call. The call names what the pass reads and writes.
- A step draws nothing until each shader entry point that it runs is built.
- Each frame, a step asks the GPU provider for its textures and buffers by name and description. The same name and
  description give the same resource, with what the last frame wrote in it. For a resource that each view has of its
  own, the step gives the view with the ask.
- The tables and techniques of the renderer, which the other rendering gems import, are in the folders under its
  `Shaders/`.

## ClusteredGeometry

Draws each model of the view as a hierarchy of clusters, and far away as its impostor. It draws into the depth,
GBuffer and glass targets of the view. List it in the `.magic` file after the renderer. The settings are in
`ClusteredGeometrySettings`.

- When a model loads, the gem makes its clusters of up to 128 triangles and its impostor atlas once for each change
  to its file. It keeps them in the cache.
- The GPU selects the detail of each cluster.
- It draws the depth of the shadow maps that a shadow gem fits, each from the light. An instance casts the shadow of
  what the camera sees of it: its clusters, or its impostor where the camera sees the impostor. A renderer that is
  `IsShadowless` and glass cast no shadow.
- An instance draws as its impostor when it is small on screen. Near that size, the clusters and the impostor blend,
  so nothing pops.
- When the clusters of a frame are near the limit of the gem, the gem draws more instances as impostors and coarser
  clusters until they fit.
- A translucent cluster draws the nearest pane into the glass targets. A glass instance that draws as its impostor
  draws opaque.
- When a domain loads, the loading screen stays until the models are on the GPU and the resolve pass of each
  material shader is built.

To change it, start at `Renderer`, which has two steps. One step keeps the models clustered on the GPU, one time for
each frame. The other step records the passes of each view, in order. `ClusterResources` keeps the clusters of each
model on the GPU. `ClusterPasses` records the passes: the cull, the draws into the visibility buffer, the depth pyramid
and the resolve of each material.

## AccelerationStructures

Builds the world as rays see it, for the gems that trace rays. List it in the `.magic` file with the renderer.

- It builds the acceleration structure of each model when the model arrives on the GPU.
- It builds the top-level acceleration structure over the instances again in each frame where they changed.
- It makes the average of each material over its texture, for a ray that hits it.

It gives the frame's `TracedWorld` to the gems that take it. Without this gem, nothing traces rays. Then there are no
traced shadows and no indirect light, and glass shows nothing behind it.

## TracedShadows

Shadows each pixel with rays through the world as rays see it. List it in the `.magic` file with the
AccelerationStructures gem. The settings are in `TracedShadowsSettings`. `Rendering.IsTracedShadowsOn` turns it off.

- The sun casts soft shadows, as wide as its disc. The first 15 point, spot and rect lights of each screen tile cast
  shadows as soft as the size of the light. `IsShadowless` does not stop them yet.
- A shadow ray goes through glass and each material with alpha under 1, by its colour. A window lets the sun in.
- The gem traces at `Rendering.ShadowScale` of the picture size. A smaller scale is faster and makes small shadows
  softer.
- The gem traces one ray for each pixel and light, then removes the noise over frames and over the screen. The
  shadows of the tile lights stay grainy at their soft edges.

## ShadowMaps

Shadows each pixel from shadow maps that are drawn again each frame, so a world that changes keeps its shadows. It
needs a geometry gem that draws shadow maps. List it in the `.magic` file in place of TracedShadows; the two cannot be
listed together. The settings are in `ShadowMapsSettings`. `Rendering.IsShadowMapsOn` turns it off.

- The sun casts shadows up to `Rendering.MaxShadowDistance` metres from the camera, in up to four cascades. Each
  cascade is a square of the sun's view, which keeps its size and moves in whole texels, so a shadow stays still while
  the camera turns and moves.
- A caster between the sun and a cascade casts into it, up to a kilometre away and further.
- A surface moves along its normal before it is tested, by `Rendering.NormalBias` texels of its cascade where the light
  is glancing, and by half a texel where it faces the light.
- Each point, spot and rect light casts a shadow from a tile of one atlas, `Rendering.AtlasResolution` texels on each
  side: six faces for a point light, five for a rect light and one for a spot light. A light near the camera gets a
  tile of up to `Rendering.MaxTileSize` texels, and a far light one of down to `Rendering.MinTileSize`.
- The still casters of each tile are kept in a cache of the same size. A tile draws its still casters again only when
  its light moves, its size changes or a caster in its range changes, up to `Rendering.CacheTilesPerFrame` tiles a
  frame. The moving casters draw over the cache each frame.
- Every shadow is contact-hardening (PCSS): sharp where a caster meets its shadow and softer as the shadow goes away
  from the caster, as wide as the light's size makes it.
- A short march through the depth buffer towards the sun shadows the gap that the sun's shadow map leaves where a
  caster meets its shadow. The local lights have no contact shadow yet.
- The `CascadeAtlas` and `ShadowAtlas` debug views show the depth of the cascades and of the local lights' tiles.
- `samples/MountainCabin/MountainCabin.magic` opens the cabin with this gem, AmbientOcclusion and LightProbes.
  `samples/MountainCabin/MountainCabinTraced.magic` opens it with the traced gems in their place.

## AmbientOcclusion

Darkens the ambient light where surfaces meet, from the depth buffer and the normals of the view (GTAO, ported from
Intel's XeGTAO under its MIT licence). It also finds the bent normal, the mean direction that is open to the sky. The
settings are in `AmbientOcclusionSettings`. `Rendering.IsAmbientOcclusionOn` turns it off.

- `Rendering.Quality` sets the slices and steps of each pixel: Low, Medium, High or Ultra.
- `Rendering.Radius` is the metres around a pixel that can hide its ambient light.
- The diffuse ambient light is darkened after its bounces between the surfaces, and the specular as much as the
  occlusion hides of its lobe.
- It darkens only the ambient light. The sun and the local lights have their own shadows.
- The `AmbientOcclusion` debug view shows the occlusion alone.

## LightProbes

Lights the surfaces with the sky, and finds how much of the sky each surface sees, with no rays and nothing baked.
Each part is made again at run time, so a world that changes keeps its light. It needs a geometry gem that draws
shadow maps. The settings are in `LightProbesSettings`. `Rendering.IsSkyVisibilityOn` turns the sky visibility off.

- The sky's light over each hemisphere comes from its spherical harmonics, projected each frame. The ground below the
  horizon reflects the sky's and the sun's light by the sky's ground albedo.
- A sky map of `Rendering.SkyMapResolution` texels on each side, filtered for each roughness, gives the sky's
  reflections.
- Direction maps hold the depth of the world as the sky sees it from `Rendering.NearDirections` directions around the
  camera, in a box of `Rendering.NearRegion` metres, and from `Rendering.FarDirections` directions in a box of
  `Rendering.FarRegion` metres. Each is an orthographic depth map in a D16 atlas.
- A map draws again only where a caster changes, up to `Rendering.ChangeRectsPerFrame` rectangles a frame, and when
  the camera moves out of its box, up to `Rendering.ScrollMapsPerFrame` maps a frame.
- Each probe tests the maps to find the share of the sky that it sees, and keeps it as an ambient cube: the share
  that a surface facing each of the 6 axes sees. Near the camera, a grid of probes 0.5 m apart, 48 m wide and 16 m
  high, tests the near maps each frame. Far from the camera, a volume of probes 4 m apart tests the far maps: all of it
  when it moves with the camera, else one eighth of it each frame.
- Each face weighs each direction by how bright the sky is there, so the side of the sky about the sun counts more.
- Each pixel reads the 8 probes about a point a little off its surface, as a trilinear filter does. A probe behind the
  surface is left out, so a wall's face does not see the probes on its other side. Near the camera, a probe is also
  left out when a short march along the screen's depth finds a wall between it and the pixel, as a contact shadow
  does. So a floor beside an outer wall does not read the probes outside it. The near grid and the far volume blend
  between 20 m and 23 m.
- The share of the sky dims the sky's light at each pixel, so a room or a cave that sees no sky is dark.
- The `SkyVisibility` debug view shows the share of the sky that each pixel's diffuse light sees.

## GlobalIllumination

Lights the surfaces with the light that bounces: a probe grid and the diffuse light near each surface. List it in the
`.magic` file with the AccelerationStructures gem. The settings are in `GlobalIlluminationSettings`.
`Rendering.IsIndirectLightOn` turns it off.

- Three grids of probes are centred on the camera and move with it a cell at a time. The probes of the first grid are
  `ProbeSpacing` metres apart, and each next grid has cells twice as wide, so it reaches twice as far. A point gets the
  light of the finest grid that holds it, blended into the next grid near the edge. A probe keeps its light while it
  stays in its grid. Past the edge of the last grid, a point gets the light of the probes at the edge.
- Each frame, rays from each surface find the light that bounces into it. A closed room gets none of the light of the
  sky, and a lit wall tints what is near it.
- A ray reads the material that it hits as the average over its texture, with no specular. A metal bounces no light.
- Each frame, the lights are put into the cells that they reach of a grid over the first probe grid. Its cells are
  half as wide as the probe grid's. Where a ray hits, it looks only at the lights of the cell there. The cells at the edge of the grid reach on past it.
- The gem traces at `Rendering.IndirectScale` of the picture size.
- The `IndirectDiffuse` debug view shows what the gem traced.

## TracedReflections

Traces what smooth surfaces reflect. List it in the `.magic` file with the GlobalIllumination gem, which it needs. The
settings are in `TracedReflectionsSettings`. `Rendering.IsReflectionsOn` turns it off. Without the gem, a surface
reflects the sky, or its diffuse light where the diffuse light is traced.

- A surface with a roughness under 0.3 reflects what its rays find. A mirror is sharp and a rougher surface is
  blurred. A surface at 0.3 or more reflects its diffuse light, and its reflection does not get stronger when it is
  seen edge-on.
- A ray that lands on a surface reads its material as the indirect light's rays do, and the probes light it. Its
  specular reflects the probes' light as a rough mirror, so a metal shows the colour of what is around it.
- One bright ray does not flash: each ray's light is held near the brightness of the rays around it.
- The gem traces at `Rendering.IndirectScale` of the picture size, as the indirect light does.
- The `Reflections` debug view shows what the gem traced.

## WeatherFX

Fills the air between the camera and the surfaces with the haze of the world. The sky, the sun and the local lights
light the haze. On a GPU that traces rays, the light falls in shafts through gaps. The gem has no settings.

To use it, list `WeatherFX` in the `.magic` file, and give the `Fog` of the world's `Weather` a `Density`, or its
`Clouds` a `Cover`, above 0. With the AccelerationStructures gem, the haze has shadows. Without it, the sun lights all
of the haze.

- The `Fog` of the `Weather` component sets the haze: its density at a height, how fast it thins above it, its
  colour and how much it scatters towards the sun. A script can change it while the world runs.
- The volume goes to the `VolumeDistance` of the `Fog` from the camera. Past it, the haze has no shadows.
- The `Cover` of the `Clouds` fills the sky with clouds from their `Height` up for 2 km. The `Wind` moves them. The
  clouds look correct from the ground, not from inside them. Their shadow falls on the ground and in the haze.
- Clear air costs nothing.

## ProceduralSky

Makes the sky of the Earth's air: Rayleigh and Mie scattering and ozone. List it in the `.magic` file after the
renderer. The renderer shades the sky, the sun's colour and the sky's light on the world from its tables.

- One time for each view, it hands on the shader type of the sky, `Atmosphere`, as its `SkyTables`. While an entity
  has a sky, it also makes the three sky tables of the view.
- Its `Sky` material is the material for the `Sky` component.
- The view's table holds the sky's light with the `Brightness` of the `Sky` in it, so everything that reads that table
  takes it: the sky that is seen, the ambient light, the probes, the diffuse rays and the reflections.
- Without the gem, the world is lit by the open sky.

## PostEffects

Runs the post-processing chain over the lit picture. List it in the `.magic` file after the renderer. Without it, the
renderer shows the lit picture with a simple tonemap and no other effects. A scene that bright local lights light is
then almost white. Such a scene needs this gem.

The effects of the first `PostProcessing` component run in order over the lit picture. Each effect is a material
whose shader implements the pixel effect contract ([Shaders](SHADERS.md)). One time for each frame, the gem makes the
effects of the frame as its `PictureEffects`. The shading of the renderer runs them.

- Without a `PostProcessing`, the picture gets the `Exposure` and `Agx` materials of the gem.
  `Rendering.ExposureCompensation` sets the brightness. `Rendering.DisabledEffects` names effects to leave out. The
  settings are in `PostEffectsSettings`.
- A `PostProcessing` lists each effect that it wants. To use a different tonemap, put its material in the place of
  `Agx`.
- The `Exposure` effect is a camera's fixed exposure, set by its `ev100` param, so a room is as dark beside the
  daylight as a camera sees it. The eye's adaptation moves it towards the picture's own brightness over about a second,
  by up to its `adaptation` param in stops. Its `compensation` param adds stops.
- While an effect that declares jitter runs, the camera moves to a different point in each pixel each frame.
- An effect shows when its material and its passes are built. A domain shows when its effects are built or failed.

## TaaUpscaling

Takes the picture from the render size to the size that the window shows, with temporal anti-aliasing. Without it, the
renderer stretches the picture. List it in the `.magic` file with the renderer.

- While it runs, the camera moves to a different point in each pixel each frame.
- It upscales what the renderer shows, debug views included, with a window or without.
- `Rendering.Sharpness` sharpens the upscaled picture, from 0, which turns it off, to 1.
- Use this gem or a post effect with the `TemporalAntiAliasing` shader, not both. With both, each frame blends over
  time two times.
