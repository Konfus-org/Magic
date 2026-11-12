// Directions and points drawn from the distributions the gathers and traces integrate over, each from a pair of
// numbers in [0, 1), and the frame a local direction is turned into the world with.
module "Core/Sampling";

import Core.Math;

// The bits of i reversed as a fraction in [0, 1): the second axis of a Hammersley point set.
public float GetRadicalInverse(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

// Point i of a well-spread set of count points in [0, 1)².
public float2 GetHammersley(uint i, uint count) { return float2((float(i) + 0.5) / float(count), GetRadicalInverse(i)); }

// A point on the unit disk, area-uniform, keeping the square's neighbourhoods (Shirley and Chiu, "A Low Distortion
// Map Between Disk and Square", 1997).
public float2 SampleConcentricDisk(float2 xi)
{
    float2 offset = xi * 2.0 - 1.0;
    if (all(offset == float2(0.0)))
        return float2(0.0);

    float radius;
    float angle;
    if (abs(offset.x) > abs(offset.y))
    {
        radius = offset.x;
        angle = (Pi / 4.0) * (offset.y / offset.x);
    }
    else
    {
        radius = offset.y;
        angle = (Pi / 2.0) - (Pi / 4.0) * (offset.x / offset.y);
    }

    return radius * float2(cos(angle), sin(angle));
}

// A direction on the hemisphere about +z with the density of cos(theta) / pi, which the diffuse lobe has.
public float3 SampleCosineHemisphere(float2 xi)
{
    float2 disk = SampleConcentricDisk(xi);
    float z = sqrt(max(0.0, 1.0 - dot(disk, disk)));
    return float3(disk, z);
}

// A direction over the whole sphere, uniform.
public float3 SampleUniformSphere(float2 xi)
{
    float z = 1.0 - 2.0 * xi.x;
    float radius = sqrt(max(0.0, 1.0 - z * z));
    float angle = TwoPi * xi.y;
    return float3(radius * cos(angle), radius * sin(angle), z);
}

// An orthonormal frame whose third axis is the unit vector n, with no branch and no singular direction (Duff et al.,
// "Building an Orthonormal Basis, Revisited", 2017). Rows are the frame's axes.
public float3x3 CreateBasis(float3 n)
{
    float sign = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (sign + n.z);
    float b = n.x * n.y * a;
    float3 tangent = float3(1.0 + sign * n.x * n.x * a, sign * b, -sign * n.x);
    float3 bitangent = float3(b, sign + n.y * n.y * a, -n.y);
    return float3x3(tangent, bitangent, n);
}

// A direction given about a frame's axes, as a world direction.
public float3 ToWorld(float3x3 basis, float3 local) { return mul(local, basis); }

// A world direction as given about a frame's axes.
public float3 ToLocal(float3x3 basis, float3 world) { return mul(basis, world); }
