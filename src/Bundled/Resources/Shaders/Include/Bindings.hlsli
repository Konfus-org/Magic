// The engine's binding model, and the conventions every shader is written against. The compiler gem
// (IShaderCompiler.CompileAsync) defines STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE in front of every shader, so
// no shader names its stage or a register space by hand; every GPU gem (IGpu) binds by this model.
//
//   READ(n)     everything read-only, numbered in the order a stage binds it: sampled textures first, then
//               storage textures, then storage buffers. A stage with eight sampled textures has its first
//               storage buffer at READ(8).
//   SAMPLER(n)  the sampler of the sampled texture at READ(n): a pair shares its index.
//   WRITE(n)    compute only: read-write storage textures first, then read-write storage buffers. A storage
//               texture a shader writes states its format for Vulkan, [[vk::image_format("rgba16f")]], which
//               otherwise needs a device feature to write it.
//   UNIFORM(n)  constant blocks. A stage has exactly one, at UNIFORM(0).
//   Names:      a shader a renderer composes (a pass's, a surface's) reads the frame's textures and buffers by name:
//               the renderer declares every name the pass can see ahead of it, numbered as above, and binds whichever
//               the shader uses, read back from its compile; the shader binds nothing itself.
//   Buffers:    a storage buffer is byte-addressed, ByteAddressBuffer or RWByteAddressBuffer, never structured: a GPU
//               gem may view it as raw bytes, and a structured buffer would then read its element size from a view
//               that has none, which some GPUs take as 0. Its elements go through LOAD and STORE, its
//               length through CountOf, and an atomic through the buffer's own methods, by byte.
// Conventions:
//   Frame:      left-handed, +X right, +Y up, +Z forward; 1 unit = 1 m.
//   Winding:    triangles are clockwise from outside; pipelines cull back faces. cross(b - a, c - a) is the
//               outward normal.
//   Depth:      reverse-Z, infinite far, 32-bit float, clear 0, GreaterOrEqual; NDC z = near / z_view.
//   Matrices:   cbuffer float4x4 is column_major (-Zpc): System.Numerics matrices are uploaded untransposed and
//               applied with mul(M, v), which equals v * M on the CPU (row vector). Storage buffers carry no
//               matrix types: a transform is three float4 rows of transpose(world) applied with dot(). Every
//               struct member in a storage buffer is 16 bytes.
//   View:       camera-relative: positions are relative to the camera before ViewProj is applied, so precision
//               does not fall off away from the origin.
//   Light:      physical units: illuminance in lux (the sun's), luminous intensity in candela (a point or spot
//               light's), luminance in nits. The scene colour is pre-exposed, radiance times exposure, so 16-bit
//               targets keep their precision however bright the scene; emissive surfaces and what a
//               transparent surface sees behind it are in those units.
//   Colour:     linear Rec. 709 in shading, sRGB in colour textures. The window's image's format says who encodes
//               its colour: an UNORM image's the last pass of a frame encodes (Include/Color.hlsli's LinearToSrgb) and
//               dithers, an sRGB one's the GPU encodes as it writes it.
//   Includes:   by path under Shaders/ (#include "Include/Bindings.hlsli"): the shader asset Shaders/<path>, from
//               whichever root has it, which the compiler loads; nothing is read from a folder.
//   Language:   HLSL 2021 (and(), or() and select() for vector logic), Shader Model 6.0, warnings are errors.
//   Waves:      wave intrinsics (Vulkan's subgroup operations) where every lane that calls one is
//               active and the result is the same whatever the wave's size, 32 or 64 by GPU: scalarising a loop,
//               reducing a group, or handing out the places of a count (WaveAllocate), which the atomics' order
//               already made arbitrary. Never otherwise a result that depends on which lanes share a wave.
//   Limits:     no 16-bit types (slow on GPUs still in use), no runtime-indexed resource arrays.
//   Semantics:  vertex inputs are TEXCOORDn, declared in n order; SV_VertexID / SV_InstanceID are never read by
//               the mesh path (first_instance is not portable): the instance index arrives in an instance-rate
//               buffer.

#ifndef MAGIC_BINDINGS_HLSLI
#define MAGIC_BINDINGS_HLSLI

#if defined(STAGE_VERTEX)
#define READ(n) register(t##n, space0)
#define SAMPLER(n) register(s##n, space0)
#define UNIFORM(n) register(b##n, space1)
#elif defined(STAGE_FRAGMENT)
#define READ(n) register(t##n, space2)
#define SAMPLER(n) register(s##n, space2)
#define UNIFORM(n) register(b##n, space3)
#elif defined(STAGE_COMPUTE)
#define READ(n) register(t##n, space0)
#define SAMPLER(n) register(s##n, space0)
#define WRITE(n) register(u##n, space1)
#define UNIFORM(n) register(b##n, space2)
#else
#error "No STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE: compile through IShaderCompiler.CompileAsync, which defines the stage."
#endif

// A parameter that is a colour (r, g, b, a in a .mat or .pass file, Color in C#). A float4 to the shader.
typedef float4 Color;

// Element index of a storage buffer of T, read or written.
#define LOAD(buffer, T, index) (buffer).Load<T>((index) * sizeof(T))
#define STORE(buffer, T, index, value) (buffer).Store<T>((index) * sizeof(T), (value))

// How many elements of size bytes a storage buffer holds, read-only or not.
template<typename T>
uint CountOf(T buffer, uint size)
{
    uint bytes;
    buffer.GetDimensions(bytes);
    return bytes / size;
}

#if defined(STAGE_COMPUTE)
// The first of the count places a lane takes in a count a buffer keeps at a byte offset: the wave's places taken
// together by one atomic (Adinets 2014, "Optimized Filtering with Warp-Aggregated Atomics"), each lane's after the
// lanes' before it, as the places were as arbitrary as the atomics' order already. Only the lanes active where it is
// called take places.
uint WaveAllocate(RWByteAddressBuffer counts, uint offset, uint count)
{
    uint before = WavePrefixSum(count);
    uint total = WaveActiveSum(count);
    uint first = 0u;
    if (WaveIsFirstLane())
    {
        counts.InterlockedAdd(offset, total, first);
    }

    return WaveReadLaneFirst(first) + before;
}
#endif

#endif
