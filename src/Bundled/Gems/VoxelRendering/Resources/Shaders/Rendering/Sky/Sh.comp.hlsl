// The sky's light from everywhere as nine spherical-harmonic coefficients, for Core's ShIrradiance (Lighting/Ibl.hlsli):
// the captured sky projected on the real SH basis, each texel weighted by the solid angle it covers (Sloan 2008,
// "Stupid Spherical Harmonics Tricks"). One group reads a small level of every face and sums.

#include "Include/Cube.hlsli"
#include "Include/Heap.hlsli"
#include "Include/Math.hlsli"

struct ShConstants
{
    uint cube;               // the captured sky, with its levels
    uint sampler;            // linear
    uint sh;                 // written, nine float4s
};

CONSTANTS(ShConstants, c);

// Texels a side of the level read, and threads in the group.
static const uint FaceSize = 16u;
static const uint Threads = 64u;

groupshared float3 Sums[Threads][9];

[numthreads(64, 1, 1)]
void main(uint thread : SV_GroupIndex)
{
    float width;
    float height;
    float levels;
    CUBE(c.cube).GetDimensions(0u, width, height, levels);
    float level = clamp(log2(width / (float)FaceSize), 0.0, levels - 1.0);

    float3 sums[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sums[i] = float3(0.0, 0.0, 0.0);
    }

    for (uint texel = thread; texel < 6u * FaceSize * FaceSize; texel += Threads)
    {
        uint face = texel / (FaceSize * FaceSize);
        uint inFace = texel % (FaceSize * FaceSize);
        float2 uv = (float2(inFace % FaceSize, inFace / FaceSize) + 0.5) / (float)FaceSize;
        float2 st = uv * 2.0 - 1.0;
        float solidAngle = 4.0 / (Pow2((float)FaceSize) * pow(1.0 + dot(st, st), 1.5));
        float3 direction = CubeDirection(face, uv);
        float3 radiance = CUBE(c.cube).SampleLevel(SAMPLER_STATE(c.sampler), direction, level).rgb * solidAngle;
        sums[0] += radiance * 0.282095;
        sums[1] += radiance * (0.488603 * direction.y);
        sums[2] += radiance * (0.488603 * direction.z);
        sums[3] += radiance * (0.488603 * direction.x);
        sums[4] += radiance * (1.092548 * direction.x * direction.y);
        sums[5] += radiance * (1.092548 * direction.y * direction.z);
        sums[6] += radiance * (0.315392 * (3.0 * direction.z * direction.z - 1.0));
        sums[7] += radiance * (1.092548 * direction.x * direction.z);
        sums[8] += radiance * (0.546274 * (direction.x * direction.x - direction.y * direction.y));
    }

    [unroll] for (uint j = 0u; j < 9u; j++)
    {
        Sums[thread][j] = sums[j];
    }

    GroupMemoryBarrierWithGroupSync();
    [branch] if (thread < 9u)
    {
        float3 total = float3(0.0, 0.0, 0.0);
        for (uint other = 0u; other < Threads; other++)
        {
            total += Sums[other][thread];
        }

        BUFFER(c.sh).Store<float4>(thread * 16u, float4(total, 0.0));
    }
}
