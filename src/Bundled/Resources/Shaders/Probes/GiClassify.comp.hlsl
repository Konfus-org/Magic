// Which probes light anything this frame, a thread a probe of every level (Lighting/Gi.hlsli's GiIsUsed): one that does
// not is not traced, and counts for nothing. A probe keeps what it holds (GiHistory) for as long as its slot holds it,
// used or not, so one that goes out of use and back, as something moves past it, picks up where it left off. One that
// comes into use holding nothing, as a level moves and its slots take new probes, is given the next coarser level's light
// where it is, from the eight probes of it around as a place between them would be lit, and the sky's at the coarsest,
// so it lights at once and near enough rather than counting for nothing until it is traced; what it sees is taken to be
// open until then. Its irradiance tile is written again whenever that changes: from what it holds, counting, while it is
// used and holds anything, else blank, counting for nothing. In the frame a new world takes the old one's place
// (header.forgets), every probe holds nothing and none is given another's light or the sky's, so none of the old world's
// light is carried into the new one: not through a coarser probe, nor the sky as it was last captured, which a world
// with no sky of its own never captures again, nor the light a ray's hit bounces.

#include "Lighting/Gi.hlsli"
#include "Lighting/Ibl.hlsli"

// Gives the probe at probeIndex the next coarser level's light where it is; false when none of that level's probes
// around it has been traced yet. Only a traced one is read: one seeded, or a slot taking another probe, may be written
// by another thread of this dispatch.
bool Seed(GpuGi header, uint index, GpuGiLevel level, int3 probe, uint probeIndex)
{
    float3 sh[9];
    uint around[8];
    float weights[8];
    float total = 0.0;
    bool isCoarsest = index + 1u >= header.levels;
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = isCoarsest ? LOAD(SkySh, float4, i).rgb : float3(0.0, 0.0, 0.0);
    }

    GpuGiLevel coarse = GiLevel(min(index + 1u, header.levels - 1u));
    float3 grid = GiProbePosition(level, probe) / ((float)GiStride * coarse.voxel) - 0.5;
    int3 base = (int3)floor(grid);
    float3 alpha = grid - float3(base);
    [unroll] for (uint corner = 0u; corner < 8u; corner++)
    {
        int3 offset = int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        int3 coarseProbe = base + offset;
        around[corner] = GiProbeIndex(coarseProbe, index + 1u);
        int4 state = isCoarsest ? int4(0, 0, 0, 0) : LOAD(GiProbeState, int4, around[corner]);
        float3 trilinear = select(offset != 0, alpha, 1.0 - alpha);
        weights[corner] = all(state.xyz == coarseProbe) && (state.w & GiProbeHeld) != 0 ? trilinear.x * trilinear.y * trilinear.z : 0.0;
        total += weights[corner];
    }

    if (!isCoarsest && total <= 0.0)
    {
        return false;
    }

    [loop] for (uint texel = 0u; texel < GiIrradianceInside * GiIrradianceInside; texel++)
    {
        float3 irradiance = float3(0.0, 0.0, 0.0);
        if (isCoarsest)
        {
            irradiance = ShIrradiance(GiTexelDirection(uint2(1u + texel % GiIrradianceInside, 1u + texel / GiIrradianceInside), GiIrradianceSide), sh) * GiStoreScale;
        }
        else
        {
            [unroll] for (uint coarseCorner = 0u; coarseCorner < 8u; coarseCorner++)
            {
                irradiance += weights[coarseCorner] > 0.0 ? GiUnpackHalf(GiHistory.Load2(GiIrradianceHistory(around[coarseCorner], texel) * 4u)).rgb * weights[coarseCorner] : float3(0.0, 0.0, 0.0);
            }

            irradiance /= total;
        }

        GiHistory.Store2(GiIrradianceHistory(probeIndex, texel) * 4u, GiPackHalf(float4(irradiance, 0.0)));
    }

    float open = GiMaxSpacings * (float)GiStride * level.voxel;
    uint2 distanceCorner = GiTile(probe, index) * GiDistanceSide;
    [loop] for (uint distanceTexel = 0u; distanceTexel < GiDistanceSide * GiDistanceSide; distanceTexel++)
    {
        GiDistance[distanceCorner + uint2(distanceTexel % GiDistanceSide, distanceTexel / GiDistanceSide)] = float2(open, open * open);
    }

    return true;
}

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    GpuGi header = GiHeader();
    if (thread.x >= GiProbes * GiProbes * GiProbes * header.levels)
    {
        return;
    }

    uint index;
    GpuGiLevel level;
    int3 probe = GiProbeOf(thread.x, index, level);
    int4 state = LOAD(GiProbeState, int4, thread.x);
    bool isSame = all(state.xyz == probe);
    int held = isSame ? state.w & (GiProbeHeld | GiProbeSeeded) : 0;
    bool wasShown = isSame && (state.w & GiProbeUsed) != 0 && held != 0;
    int holds = header.forgets != 0u ? 0 : held;
    bool isUsed = GiIsUsed(level, index, probe);
    if (isUsed && holds == 0 && header.forgets == 0u && Seed(header, index, level, probe, thread.x))
    {
        holds = GiProbeSeeded;
    }

    STORE(GiProbeState, int4, thread.x, int4(probe, (isUsed ? GiProbeUsed : 0) | holds));
    bool isShown = isUsed && holds != 0;
    if (isSame && isShown == wasShown)
    {
        return;
    }

    uint2 corner = GiTile(probe, index) * GiIrradianceSide;
    [loop] for (uint texel = 0u; texel < GiIrradianceSide * GiIrradianceSide; texel++)
    {
        uint2 at = uint2(texel % GiIrradianceSide, texel / GiIrradianceSide);
        uint2 source = GiBorderSource(at, GiIrradianceSide) - 1u;
        uint inside = source.y * GiIrradianceInside + source.x;
        float3 held = isShown ? GiUnpackHalf(GiHistory.Load2(GiIrradianceHistory(thread.x, inside) * 4u)).rgb : float3(0.0, 0.0, 0.0);
        GiIrradiance[corner + at] = float4(held, isShown ? 1.0 : 0.0);
    }
}
