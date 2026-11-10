# TaaUpscaling

Takes the finished picture from the render size to the window's size with temporal anti-aliasing. While it is
listed, the camera draws each frame through a different point in each texel, and each window pixel is built from the
texels around it and from what the frames before saw there, so it is sharper than the picture stretched over the
window. Without it, the renderer stretches the picture.

- It upscales the renderer's output as it is shown, debug views included, and draws nothing headless.
- It anti-aliases as it upscales, so list it or the temporal anti-aliasing effect, not both: with both, every frame
  is blended over time twice. The engine's post processing, which a world without its own runs, has the temporal
  anti-aliasing, so a project that lists this gem gives its worlds post processing without it.
- Every pass and effect draws at the render size; it is the one step that draws at the window's size.

It uses the world's gem, whose view it upscales, and the renderer, which runs its pass once the picture is finished.

To use it, list `TaaUpscaling` in a project's `.magic` file.
