// The conventions every shader is written against. Every shader reads what it uses through the heap
// (Include/Heap.hlsli) and binds nothing by register. The compiler gem (IShaderCompiler.CompileAsync) defines
// STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE in front of every shader.
//
//   Buffers:    a storage buffer is byte-addressed, never structured. Its elements go through LOAD and STORE, its
//               length through CountOf, and an atomic through the buffer's own methods, by byte.
// Conventions:
//   Frame:      left-handed, +X right, +Y up, +Z forward; 1 unit = 1 m.
//   Winding:    triangles are clockwise from outside; pipelines cull back faces. cross(b - a, c - a) is the
//               outward normal.
//   Depth:      reverse-Z, infinite far, 32-bit float, clear 0, GreaterOrEqual; NDC z = near / z_view.
//   Matrices:   a float4x4 is column_major (-Zpc): System.Numerics matrices are uploaded untransposed and
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
//   Includes:   by path under Shaders/ (#include "Include/Heap.hlsli"): the shader asset Shaders/<path>, from
//               whichever root has it, which the compiler loads; nothing is read from a folder.
//   Language:   HLSL 2021 (and(), or() and select() for vector logic), Shader Model 6.0, warnings are errors.
//   Waves:      wave intrinsics (Vulkan's subgroup operations) where every lane that calls one is
//               active and the result is the same whatever the wave's size, 32 or 64 by GPU: scalarising a loop,
//               reducing a group, or handing out the places of a count (WaveAllocate), which the atomics' order
//               already made arbitrary. Never otherwise a result that depends on which lanes share a wave.
//   Limits:     no 16-bit types (slow on GPUs still in use), no runtime-indexed resource arrays but the heap's.
//   Vertices:   a vertex shader reads its vertices from the heap by SV_VertexID; there are no vertex buffers.

#ifndef MAGIC_BINDINGS_HLSLI
#define MAGIC_BINDINGS_HLSLI

#if !defined(STAGE_VERTEX) && !defined(STAGE_FRAGMENT) && !defined(STAGE_COMPUTE)
#error "No STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE: compile through IShaderCompiler.CompileAsync, which defines the stage."
#endif

// A parameter that is a colour (r, g, b, a in a .mat file, Color in C#). A float4 to the shader.
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
