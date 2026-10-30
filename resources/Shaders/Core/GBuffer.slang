// The G-buffer: what the surface at each pixel of a view is, written by the surface draws and read back by the passes
// after them, and nothing else knows how it is packed. Core's GBuffer names its textures, the view's depth and the five
// targets the draws write, in this order, in formats the packing is made for:
//
//   Albedo    rgba8 sRGB  base colour; a occlusion
//   Normal    rgb10a2     rg the world normal on the octahedron (Core/Math), b roughness, a shading model
//   Surface   rgba8       metallic, specular, and the shading model's two values
//   Emissive  rgba16f     what the surface gives off, pre-exposed
//   Velocity  rg16f       where the pixel was on screen last frame less where it is now, in uv

module "Core/GBuffer";

import Core.Heap;
import Core.Math;
import Core.Surface;

// The G-buffer's textures, Core's GBuffer member for member, which a pass's constants hold whole.
public struct GBuffer
{
    TextureRef depth;        // the view's depth, 0 where nothing was drawn
    TextureRef albedo;
    TextureRef normal;
    TextureRef surface;
    TextureRef emissive;
    TextureRef velocity;

    // The surface at a pixel of the view, read back from the targets.
    public GBufferSurface Load(int2 pixel)
    {
        float4 albedo = this.albedo.Load(pixel, 0);
        float4 normal = this.normal.Load(pixel, 0);
        float4 values = this.surface.Load(pixel, 0);

        GBufferSurface surface;
        surface.baseColor = albedo.rgb;
        surface.occlusion = albedo.a;
        surface.normal = DecodeOctahedral(normal.xy * 2.0 - 1.0);
        surface.roughness = normal.z;
        surface.model = (ShadingModel)(uint)round(normal.w * 3.0);
        surface.metallic = values.r;
        surface.specular = values.g;
        surface.modelData = values.ba;
        return surface;
    }
}

// What a surface draw writes to the targets.
public struct GBufferTargets
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 surface : SV_Target2;
    float4 emissive : SV_Target3;
    float2 velocity : SV_Target4;
}

public struct GBufferSurface
{
    float3 baseColor;
    float occlusion;
    float3 normal;
    float roughness;
    ShadingModel model;
    float metallic;
    float specular;
    float2 modelData;
}

public GBufferTargets EncodeGBuffer(GBufferSurface surface, float3 emissive, float2 velocity)
{
    GBufferTargets output;
    output.albedo = float4(surface.baseColor, surface.occlusion);
    output.normal = float4(EncodeOctahedral(surface.normal) * 0.5 + 0.5, saturate(surface.roughness), (float)(uint)surface.model / 3.0);
    output.surface = float4(surface.metallic, surface.specular, surface.modelData);
    output.emissive = float4(emissive, 1.0);
    output.velocity = velocity;
    return output;
}
