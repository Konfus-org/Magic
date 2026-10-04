// Rasterises one mesh into its occupancy brick: one thread per triangle, which marks every cell of the brick's
// GI_BRICK^3 grid over the mesh's box that the triangle touches (a separating-axis test against the cell's box),
// so a hollow mesh gives a hollow brick, with walls at least a cell thick. Racing threads write the same 1, which
// is fine. Runs once per mesh when it is first drawn, GiSettings.BricksPerFrame of them a frame.

#include "Gi/Brick.hlsli"
#include "Gi/Common.hlsli"

StructuredBuffer<GpuVertexRaw> Vertices : READ(0);
StructuredBuffer<uint> Indices : READ(1);

[[vk::image_format("r8")]]
RWTexture3D<float> BrickAtlas : WRITE(0);

// Whether the projections of the triangle (by its three edge-plane distances) and the box (half extents h) onto an
// axis are apart.
bool AxisSeparates(float3 axis, float3 v0, float3 v1, float3 v2, float3 h)
{
    float p0 = dot(v0, axis), p1 = dot(v1, axis), p2 = dot(v2, axis);
    float radius = dot(h, abs(axis));
    return max(max(p0, p1), p2) < -radius || min(min(p0, p1), p2) > radius;
}

// Akenine-Moller's triangle against an axis-aligned box centred at the origin with half extents h: the box's three
// axes, the triangle's plane, and the nine edge cross products.
bool TriangleTouchesBox(float3 v0, float3 v1, float3 v2, float3 h)
{
    if (AxisSeparates(float3(1.0, 0.0, 0.0), v0, v1, v2, h)) return false;
    if (AxisSeparates(float3(0.0, 1.0, 0.0), v0, v1, v2, h)) return false;
    if (AxisSeparates(float3(0.0, 0.0, 1.0), v0, v1, v2, h)) return false;

    float3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;
    float3 normal = cross(e0, e1);
    if (AxisSeparates(normal, v0, v1, v2, h)) return false;

    float3 axes[3] = { float3(1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, 0.0, 1.0) };
    float3 edges[3] = { e0, e1, e2 };
    [unroll] for (uint i = 0u; i < 3u; i++)
    {
        [unroll] for (uint j = 0u; j < 3u; j++)
        {
            float3 axis = cross(edges[i], axes[j]);
            if (dot(axis, axis) > 1e-12 && AxisSeparates(axis, v0, v1, v2, h))
                return false;
        }
    }

    return true;
}

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint tri = threadId.x;
    if (tri * 3u + 2u >= BrickJob.y)
        return;

    uint first = BrickJob.x + tri * 3u;
    float3 size = max(BrickBoxMax.xyz - BrickBoxMin.xyz, 1e-4);
    float3 scale = (float)GI_BRICK / size;
    float3 v0 = (Vertices[Indices[first] + BrickJob.z].a.xyz - BrickBoxMin.xyz) * scale;
    float3 v1 = (Vertices[Indices[first + 1u] + BrickJob.z].a.xyz - BrickBoxMin.xyz) * scale;
    float3 v2 = (Vertices[Indices[first + 2u] + BrickJob.z].a.xyz - BrickBoxMin.xyz) * scale;

    int3 lowest = clamp((int3)floor(min(min(v0, v1), v2)), 0, (int)GI_BRICK - 1);
    int3 highest = clamp((int3)floor(max(max(v0, v1), v2)), 0, (int)GI_BRICK - 1);
    uint3 offset = GiBrickOffset(BrickJob.w);
    float3 halfCell = float3(0.5, 0.5, 0.5);
    [loop] for (int z = lowest.z; z <= highest.z; z++)
    {
        [loop] for (int y = lowest.y; y <= highest.y; y++)
        {
            [loop] for (int x = lowest.x; x <= highest.x; x++)
            {
                float3 center = float3(x, y, z) + halfCell;
                if (TriangleTouchesBox(v0 - center, v1 - center, v2 - center, halfCell))
                    BrickAtlas[offset + uint3(x, y, z)] = 1.0;
            }
        }
    }
}
