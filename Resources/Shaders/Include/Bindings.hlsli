// Register and space layout SDL GPU expects from HLSL, one macro per binding kind and stage. Every shader
// declares its resources through these so the order SDL reflects (samplers, then storage textures, then
// storage buffers, indices consecutive from 0 in that order) is never spelled out by hand.
//
// Conventions (plan section 1) every shader is written against:
//   Frame:      left-handed, +X right, +Y up, +Z forward; 1 unit = 1 m.
//   Winding:    triangles are clockwise from outside; pipelines use FrontFace = Clockwise, CullMode = Back.
//               cross(b - a, c - a) is the outward normal.
//   Depth:      reverse-Z, infinite far, clear 0, GreaterOrEqual; NDC z = near / z_view.
//   Matrices:   cbuffer float4x4 is column_major (the default): System.Numerics matrices are uploaded
//               untransposed and applied with mul(M, v), which equals v * M on the CPU (row vector).
//               Storage buffers carry no matrix types: a transform is three float4 rows of transpose(world)
//               applied with dot(). Every struct member in a storage or uniform buffer is 16 bytes.
//   View:       camera-relative: positions are relative to CameraPos before ViewProj is applied.
//   Colour:     textures and the colour target are sRGB, shading is linear; the only OETF is the last step
//               of Tonemap.frag.
//   Includes:   root-relative (#include "Include/Bindings.hlsli"); DXC's include directory is the shader root.
//   Limits:     SM 6.0 for Vulkan 1.0: no wave intrinsics, no 16-bit types, no runtime-indexed resource arrays.
//   Semantics:  vertex inputs are TEXCOORDn; SV_VertexID / SV_InstanceID are never read by the mesh path
//               (first_instance is not portable), the instance index arrives in an instance-rate buffer.

#ifndef MAGIC_BINDINGS_HLSLI
#define MAGIC_BINDINGS_HLSLI

// Vertex stage: sampled textures, samplers and storage in space0; uniforms in space1.
#define VS_TEX(n)   register(t##n, space0)
#define VS_SAMP(n)  register(s##n, space0)
#define VS_BUF(n)   register(t##n, space0)
#define VS_CB(n)    register(b##n, space1)

// Fragment stage: sampled textures, samplers and storage in space2; uniforms in space3.
#define PS_TEX(n)   register(t##n, space2)
#define PS_SAMP(n)  register(s##n, space2)
#define PS_BUF(n)   register(t##n, space2)
#define PS_CB(n)    register(b##n, space3)

// Compute stage: read-only in space0 (sampled textures, then storage textures, then storage buffers),
// read-write in space1 (textures, then buffers), uniforms in space2.
#define CS_TEX(n)   register(t##n, space0)
#define CS_SAMP(n)  register(s##n, space0)
#define CS_ROTEX(n) register(t##n, space0)
#define CS_ROBUF(n) register(t##n, space0)
#define CS_RWTEX(n) register(u##n, space1)
#define CS_RWBUF(n) register(u##n, space1)
#define CS_CB(n)    register(b##n, space2)

// Texture pool slots (Gems/Render/Residency/TextureArrayPool.cs): slot = formatIndex * 4 + sizeIndex, formats
// [sRGB, linear], sizes [256, 512, 1024, 2048]. Each slot is one Texture2DArray bound at t<slot>/s<slot> of the
// fragment stage; a material stores (slot << 16) | layer. The C# table checks these values against this file
// at startup, so the two never drift apart silently.
#define POOL_SRGB_256    0
#define POOL_SRGB_512    1
#define POOL_SRGB_1024   2
#define POOL_SRGB_2048   3
#define POOL_LINEAR_256  4
#define POOL_LINEAR_512  5
#define POOL_LINEAR_1024 6
#define POOL_LINEAR_2048 7
#define POOL_COUNT       8

#endif
