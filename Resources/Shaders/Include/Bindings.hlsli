// Where SDL GPU expects a shader's resources: one macro per register class, resolved for the stage being
// compiled. The renderer defines STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE in front of every shader
// (IRendering.Compile), so no shader names its stage or a register space by hand.
//
//   READ(n)     everything read-only, numbered in the order SDL binds it: sampled textures first, then
//               storage textures, then storage buffers. A stage with eight sampled textures has its first
//               storage buffer at READ(8).
//   SAMPLER(n)  the sampler of the sampled texture at READ(n): a pair shares its index.
//   WRITE(n)    compute only: read-write storage textures first, then read-write storage buffers.
//   UNIFORM(n)  constant blocks. A stage has exactly one, the frame block at UNIFORM(0) (Include/Frame.hlsli).
//
// Conventions every shader is written against (style and best practices: README.md in this folder):
//   Frame:      left-handed, +X right, +Y up, +Z forward; 1 unit = 1 m.
//   Winding:    triangles are clockwise from outside; pipelines use FrontFace = Clockwise, CullMode = Back.
//               cross(b - a, c - a) is the outward normal.
//   Depth:      reverse-Z, infinite far, clear 0, GreaterOrEqual; NDC z = near / z_view.
//   Matrices:   cbuffer float4x4 is column_major (the default): System.Numerics matrices are uploaded
//               untransposed and applied with mul(M, v), which equals v * M on the CPU (row vector).
//               Storage buffers carry no matrix types: a transform is three float4 rows of transpose(world)
//               applied with dot(). Every struct member in a storage buffer is 16 bytes.
//   View:       camera-relative: positions are relative to CameraPos before ViewProj is applied.
//   Colour:     textures and the colour target are sRGB, shading is linear; the only OETF is the last step
//               of the tonemap pass.
//   Includes:   root-relative (#include "Include/Bindings.hlsli"); DXC's include directory is the shader root.
//   Limits:     SM 6.0 for Vulkan 1.0: no wave intrinsics, no 16-bit types, no runtime-indexed resource arrays.
//   Semantics:  vertex inputs are TEXCOORDn; SV_VertexID / SV_InstanceID are never read by the mesh path
//               (first_instance is not portable), the instance index arrives in an instance-rate buffer.

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
#error "No STAGE_VERTEX, STAGE_FRAGMENT or STAGE_COMPUTE: compile through IRendering.Compile, which defines the stage."
#endif

// A parameter that is a colour (r, g, b, a in a .mat or .pass file, Color in C#). A float4 to the shader.
typedef float4 Color;

#endif
