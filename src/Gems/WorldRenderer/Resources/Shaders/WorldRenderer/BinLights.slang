// Each light into the froxels it reaches: one thread a light, its sphere against the boxes of the froxels under its
// screen rectangle and depth range, appended to each it touches. The pass header's x is the frame's constants and z
// the lighting constants.
module "WorldRenderer/BinLights";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Lights;
import Scene.Lighting;

[shader("compute")]
[numthreads(64, 1, 1)]
void BinLights(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    if (thread.x >= lighting.lightCount)
        return;

    GpuLight light = LoadLight(lighting.lights, thread.x);
    float3 center = mul(float4(Relative(frame, light.position), 1.0), frame.view).xyz;
    float radius = light.range;
    float nearZ = max(center.z - radius, LightGridNear);
    float farZ = center.z + radius;
    if (farZ <= LightGridNear || nearZ >= LightGridFar)
        return;

    // The screen rectangle: the sphere's extents at whichever of its depths spreads each the widest.
    float p00 = frame.projection[0][0];
    float p11 = frame.projection[1][1];
    float2 low = float2((center.x - radius) * p00, (center.y - radius) * p11);
    float2 high = float2((center.x + radius) * p00, (center.y + radius) * p11);
    float2 minNdc = min(low / nearZ, low / farZ);
    float2 maxNdc = max(high / nearZ, high / farZ);
    if (center.z - radius <= 0.0)
    {
        minNdc = float2(-1.0);
        maxNdc = float2(1.0);
    }

    // Tiles count down the screen while clip space counts up, so the rectangle's top is its greatest y.
    float2 tiles = float2(LightGrid.xy);
    int2 first = clamp(
        int2(floor(float2(minNdc.x * 0.5 + 0.5, 0.5 - maxNdc.y * 0.5) * tiles)),
        int2(0),
        int2(LightGrid.xy) - 1
    );
    int2 last = clamp(
        int2(floor(float2(maxNdc.x * 0.5 + 0.5, 0.5 - minNdc.y * 0.5) * tiles)),
        int2(0),
        int2(LightGrid.xy) - 1
    );
    uint firstSlice = GetLightSlice(nearZ);
    uint lastSlice = GetLightSlice(min(farZ, LightGridFar));
    ArrayRef<uint> lists = { lighting.froxelLists };
    BufferRef counts = { lighting.froxelLists };
    for (uint slice = firstSlice; slice <= lastSlice; slice++)
    {
        float sliceNear = GetLightSliceDepth(slice);
        float sliceFar = GetLightSliceDepth(slice + 1u);
        for (int y = first.y; y <= last.y; y++)
        {
            for (int x = first.x; x <= last.x; x++)
            {
                // The froxel's box in view space: its tile's edges at the slice's far depth hold its near ones too.
                float2 ndcMin = (float2(x, y) / tiles) * 2.0 - 1.0;
                float2 ndcMax = (float2(x + 1, y + 1) / tiles) * 2.0 - 1.0;
                float3 boxMin = float3(min(ndcMin.x * sliceNear, ndcMin.x * sliceFar) / p00, min(-ndcMax.y * sliceNear, -ndcMax.y * sliceFar) / p11, sliceNear);
                float3 boxMax = float3(max(ndcMax.x * sliceNear, ndcMax.x * sliceFar) / p00, max(-ndcMin.y * sliceNear, -ndcMin.y * sliceFar) / p11, sliceFar);
                float3 nearest = clamp(center, boxMin, boxMax);
                if (dot(nearest - center, nearest - center) > radius * radius)
                    continue;

                uint froxel = GetFroxelIndex(uint3(uint(x), uint(y), slice));
                uint count = counts.InterlockedAdd(froxel * (LightsPerFroxel + 1u) * 4u, 1u);
                if (count < LightsPerFroxel)
                    lists[(froxel * (LightsPerFroxel + 1u)) + 1u + count] = thread.x;
            }
        }
    }
}
