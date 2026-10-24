# VoxelRendering

A renderer. It draws the world on the GPU as clusters, at the detail each part of the view needs. It needs a GPU
provider whose shaders read resources through a heap, a shader compiler, and a gem that runs scripts. It draws the
clusters and cards a cluster generator makes for each model.

- **The frame** is a pipeline script, a C# class that records the renderer's steps and its own passes in order
  ([Rendering](../../../Core/README.md#rendering)). The settings name it. By default it is `Scripts/DefaultPipeline.cs`
  from the gem's resources. To change the frame, or light it with a shader of your own, copy that file into the
  project's assets, rename the class, change it, and name the copy in the settings.
- **The look** comes from the world's `PostProcessing` effects, in order. Effects next to each other that change each
  pixel by itself, such as `Tonemap` and `Grade`, run as one pass. A world without effects shows the lit view as it
  is.
- **The weather** comes from the world's `Weather` effects: its `Wind` and `Clouds` move and shape the sky's clouds.
- **Overlays.** The overlay script draws over the window after its views are drawn. By default it is
  `Scripts/DebugUi.cs`, the debug UI's.
- **Failures.** A model that fails to load, or a material that fails to load or compile, is drawn with a magenta
  failure material. A pass whose shader fails is left out, and a banner across the view says why.
- **Loading.** Nothing is drawn until it can be drawn properly, and what had to wait fades in. A domain appears whole in
  its first frame. A frame shows only once every pass's shader is compiled.
- **Lighting.** The world is lit by its directional lights, its point and spot lights and its sky. The world is also
  kept as voxels around the player's camera, finest near it, and changed only where something moved. Each light's
  shadow is a cone traced through the voxels toward it, as wide as the light looks, so shadows soften with distance from
  what casts them. The sky's light is hidden where the voxels say the world hides it. The voxels' size and levels, and
  the most lights a view lights by, are settings.
- **Pausing.** While the world is paused, its animated materials and its sky stand still, and they follow its time
  scale. Fades and failures go on. A surface or sky animates by its inputs' `frame.worldSec`, and by `frame.realSec`
  for what goes on while the world is paused.

Its settings, such as the render scale, are on `VoxelRenderingSettings` and show in the settings window (F4).
Textures are compressed to BC7 the first time they load, and kept in the cache.

- Light does not bounce yet: what the lights and the sky do not reach directly is dark.
- Transparent surfaces are not drawn, and cast no shadow.
