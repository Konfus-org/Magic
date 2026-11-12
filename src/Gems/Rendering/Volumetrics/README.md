# Volumetrics

Fills the air between the camera and what it sees with the world's haze, lit by the sky, the sun and the local lights
where their light reaches. On a GPU that traces rays, their light falls in shafts through the gaps in whatever blocks
it. The renderer's shading reads it as the frame's air; without the gem, the air changes nothing.

To use it, list `Volumetrics` in a project's `.magic` file, and give the world's `Weather` a `FogDensity` or a
`CloudCover` above 0. A gem that builds the world as rays see it shadows the haze; without one, the sun lights
all of it.

## What it draws

- The world's `Weather` sets how the haze looks: how thick it is at a height, how fast it thins above that, its colour,
  and how much it scatters towards the sun. A script can change it while the world runs; clear air costs nothing.
- The volume reaches `Rendering.VolumetricsDistance` metres from the camera. `Rendering.VolumetricsScale` sets its
  cells across and down as a part of the picture's size. Past the volume's reach the haze looks the same, without
  shadows.
- Each cell is lit at a point that moves within it each frame and blended with the frames before, so the shafts are
  smooth, and soft at the cells' size. Each frame a cell takes one of the local lights that reach it, picked by how
  much it would give, so where many overlap their glow builds up over a few frames.
- The `Weather`'s `CloudCover` fills the sky with clouds from `CloudHeight` up 2 km, carried by its `Wind`. They are
  drawn into a picture of the sky around the camera, a sixteenth of it each frame, so they look right from the
  ground but not from among them. Their shadow falls on the ground and in the haze, and moves with them.

## How it works

- Its `Render` takes the view and, where a gem makes it, the world as rays see it. It records its passes in the
  frame's light stage, writes the air into the frame's buffer as the renderer's `Light/Air`, and hands the frame's
  `Air` to the gems that take it, which read it through `ApplyAir`.
- The shader is the `Shaders/FillAir` module in this gem's `Resources`, built on the shader library
  ([Shaders](../../../Resources/Shaders/README.md)). `ScatterAir` lists the local lights that reach each group of
  cells, lights each cell and blends it with its history. `IntegrateAir` sums each column away from the camera.
  `Shaders/DrawClouds` draws the clouds' picture.
