# GlobalIllumination

Lights the view's surfaces with the light that bounces, on a GPU that traces rays: a probe grid, the diffuse light
traced near each surface and the reflections of smooth ones. It writes them as the frame's indirect light, which the
renderer's shading reads; without it, on a GPU that doesn't trace rays, or with `Rendering.IsIndirectLightOn` off, the
sky lights the surfaces instead.

To use it, list `GlobalIllumination` in a project's `.magic` file with a gem that builds the world as rays see it.

## What it draws

- A grid of probes over what rays see of the world, a metre past it on every side, gathers the light that bounces:
  `Rendering.ProbesAcross` along each side of the ground and `Rendering.ProbesUp` upwards. The grid follows the world
  slowly as instances come and go, and a world no ray sees is lit by the sky. Each frame its probes ask for rays, more
  of them where their light still changes and in view, and take them in turn while they fit in the frame's budget; one
  that waits keeps its light. Each ray is lit where it lands by one light picked at random, the sun among them, by the
  sky where it hits nothing, and by the probes themselves. Each probe blends them into the light it holds from each
  direction and how far it sees, and moves away from a surface it is too near. A ray reads the material it lands on
  averaged over its whole texture, with no specular of its own, so a metal reflects black.
- A surface's diffuse light from around it is traced, in place of the open sky's: each frame two rays from one pixel of
  each traced pixel's footprint, reaching 10 m, each lit where it lands by one light picked at random and by the probes,
  and the probes' light near the pixel that see it where it lands on nothing. It is smoothed over neighbours and over
  frames, then blurred across the surface where it is still grainy. So shadowed places get the light that bounces into
  them, a closed room none of the sky's, the base of what stands on the ground darkens and a lit wall tints what is near
  it. The renderer's `indirectDiffuse` debug view shows the light it gives a white surface.
- A surface with a roughness of 0.6 or less reflects what its rays find: one ray from a pixel of each traced pixel's
  footprint a frame, along a direction its roughness spreads, lit where it lands by one light picked at random and by
  the probes, or the sky where it lands nowhere. A frame's rays are resolved over their neighbours and blended with the
  frames before where the surface, or what it reflects, was, so a mirror is sharp and a rough surface blurred. The
  renderer's `reflections` debug view shows what is traced. With `Rendering.IsReflectionsOn` off, smooth surfaces
  reflect their diffuse light.
- The diffuse light and the reflections are traced at `Rendering.IndirectScale` of the picture's size along each
  side, and read back at the picture's size along its surfaces. `set Rendering.IndirectScale 0.5` sets it live.
- A domain loading waits for the passes to be built, and what was traced of the world before is forgotten.

## How it works

- Its `Render` takes the view and the world as rays see it, places the grid, records its passes in the frame's light
  stage, writes the indirect light into the frame's buffer as the library's `Light/Indirect`, and hands the frame's
  `IndirectLight` to the gems that take it. A frame whose world rays see no instance of makes none.
- The shaders are the `Shaders/` modules in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)). Each pass reads the frame's buffer, which holds the indirect light,
  as its header's x, and the gem's own constants as its y.
  - `UpdateProbes` brings the probes' light up to date.
  - `Scene/Traced` is what the light traced at a part of the picture's size shares: the gem's constants, which pixel
    traces, how a pass averages its neighbours, and its history.
- Parts of the traced diffuse light and the traced reflections are adapted from another engine under the MIT
  licence; `THIRD-PARTY-NOTICES.md` holds it.
