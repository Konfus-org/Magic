# GpuChecks

Checks the GPU provider and the shader compiler together, for the sample project and `tools/Scripts/Validate.ps1`: as
the run starts, a compute entry fills a texture with a gradient through the heap, the texture is read back, and a
corner that reads wrong is an error. Every frame after, the gradient is stretched over a frame the size of a window,
shown in the window when there is one, and saved as a PNG by the console's `screenshot` command.

To use it, list `GpuChecks` in a project's `.magic` file after the gems that provide the GPU and the shader compiler.
