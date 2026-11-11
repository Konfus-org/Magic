// What a material is handed for a pixel and what it gives back: the contract between the visibility resolve, which
// finds the pixel's triangle, and every material, which decides what the surface there is made of.
module "Surface/Surface";

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
