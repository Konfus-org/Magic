# ClusterRendering

The renderer. It draws the world on the GPU as clusters, at the detail each part of the view needs. It needs a GPU
provider and a shader compiler, and draws the clusters and cards a cluster generator makes for each model.

It draws opaque and transparent surfaces, point, spot and directional lights with shadows, the world's sky, the air and
fog. Effects such as global illumination, reflections and tonemapping are passes a world lists in its effects pipeline.

- **The frame** is a pipeline of passes ([The GPU](../../../Core/README.md#the-gpu)). The settings name it. By default
  it is `Pipelines/Clusters.pipeline` from the engine's resources.
- **Effects** run after the frame, from the pipeline a world's `PostProcessing` component names. A world without one
  has no effects.
- **Script passes.** A pass of kind `script` names a script that draws part of the frame, such as a HUD. It needs a
  gem that runs scripts.
- **Overlays.** The overlay pipeline runs over each target after its views are drawn, the window included. The debug
  UI draws here.
- **Debug views.** A debug view is a pass in `Passes/Debug/`, shown in place of the finished image when the debug tools
  ask for it. A project adds one by adding a pass there with a one-sentence description.
- **Failures.** A model that fails to load, or a material that fails to load or compile, is drawn with a magenta
  failure material. A pass that fails draws magenta, and a banner across the view says why.
- **Loading.** Nothing is drawn until it can be drawn properly, and what had to wait fades in. A domain appears whole in
  its first frame.

Its settings, such as the render scale and shadow quality, are on `ClusterRenderingSettings` and show in the settings
window (F4). By default each view is drawn smaller than its target and upscaled. Textures are compressed to BC7 the
first time they load, and kept in the cache.

- Among directional lights, only the key light casts shadows: the sun while it is up, else the moon while it is up,
  else the brightest.
- The key light's shadows update in steps as the light turns.
- A moving light redraws all its shadows every frame. Many moving lights make shadows coarse.
- Masked materials cast the shadow of their whole mesh. Cards cast no shadows.
- Only opaque surfaces trace reflections. Transparent surfaces reflect the sky.
- A reflection of something off screen shows a coarse, flat-coloured copy of it, taken from global illumination.
- Transparent surfaces get no air or fog.
- All of a texture's mips stay in memory.
