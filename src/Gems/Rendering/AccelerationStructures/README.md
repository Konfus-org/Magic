# AccelerationStructures

Builds the world as rays see it, on a GPU that traces rays, for the gems that trace them: the acceleration structure
over the instances and each material as a ray hit reads it. Without it, or on a GPU that doesn't trace rays, nothing
traces rays: there are no traced shadows or indirect light, and glass and the effects see nothing through the world.

To use it, list `AccelerationStructures` in a project's `.magic` file with the gem that keeps the world on the GPU,
whose instances it builds from.

## What it builds

- The top-level acceleration structure over every instance whose model has its triangles' structure, which the
  world's gem makes as the model arrives. It is built again in a frame whose instances changed.
- Each material as a ray hit reads it: its texture averaged over the whole texture, made again each frame.
- The box over the world boxes of the instances rays see.

## How it works

- Its `Render` writes them into the frame's buffer as the library's `Scene/Models` `TracedWorld`, which every shader
  that traces reads, and hands the frame's `TracedWorld` to the gems that take it.
- `AverageMaterials` in this gem's `Resources/Shaders` averages the material rows, a pass for each material shader.
