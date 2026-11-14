// What a material is handed for a pixel and what it gives back: the contract between the visibility resolve, which
// finds the pixel's triangle, and every material, which decides what the surface there is made of.
module "Surface/Surface";

import Core.Heap;
import Core.Math;

public struct SurfaceInputs
{
    // In the camera-relative world.
    public float3 position;
    // Unit, in the world, interpolated from the vertices.
    public float3 normal;
    public float3 tangent;
    public float tangentSign;
    public float2 uv;
    // How uv changes over a pixel across and down: what a texture is sampled with.
    public float2 uvDdx;
    public float2 uvDdy;

    // The normal with a tangent-space normal map bent into it, or the vertex normal where the map's id is 0, which
    // means none. The map's green channel points up the surface, as the maps of glTF and OpenGL do.
    public float3 SampleNormal(TextureRef map, SamplerRef sampler)
    {
        if (map.id == 0u)
            return normal;

        float3 bent = map.SampleGrad(sampler, uv, uvDdx, uvDdy).xyz * 2.0 - 1.0;
        float3 bitangent = cross(normal, tangent) * tangentSign;
        return NormalizeSafe(tangent * bent.x + bitangent * bent.y + normal * bent.z, normal);
    }
}

public struct SurfaceOutputs
{
    public float3 baseColor;
    // Unit, in the world: the vertex normal, or one the material perturbed.
    public float3 normal;
    public float roughness;
    public float metalness;
    public float3 emissive;
    public float occlusion;
    public float alpha;
}
