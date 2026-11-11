# TaaUpscaling

Takes the finished picture from the render size to the size it is shown at with temporal anti-aliasing. While it is
listed, the camera draws each frame through a different point in each texel, and each shown pixel is built from the
texels around it and from what the frames before saw there, so it is sharper than the picture stretched. Without it,
the renderer stretches the picture.

- It upscales the renderer's output as it is shown, debug views included, with a window or without.
- It anti-aliases as it upscales, so list it or the temporal anti-aliasing effect, not both: with both, every frame
  is blended over time twice.
- Every pass and effect draws at the render size; it is the one step that draws at the size the picture is shown at.

It uses the renderer, whose view it upscales and which runs its pass once the picture is finished.

To use it, list `TaaUpscaling` in a project's `.magic` file.
