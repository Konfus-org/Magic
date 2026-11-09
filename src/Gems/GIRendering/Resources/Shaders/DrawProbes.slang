// The probe grid's debug view: each probe drawn where it is as a small sphere, each point of it the light the probe
// holds from that direction, drawn into the lit picture before its effects and behind what is nearer. One draw, an
// instance a probe. The pass header's x is the frame's buffer, which holds the grid's constants.
module "DrawProbes";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Probes;

// The sphere's bands from pole to pole and its slices around: C#'s Probes.SphereVertices is their product times six.
static const uint SphereStacks = 8u;
static const uint SphereSlices = 16u;

// A quad's two triangles, by the slice and stack each corner is on from the quad's first.
static const uint2 QuadCorners[6] = { uint2(0u, 0u), uint2(1u, 0u), uint2(1u, 1u), uint2(0u, 0u), uint2(1u, 1u), uint2(0u, 1u) };

struct Varyings
{
    float4 position : SV_Position;
    // Out of the probe, unit at each vertex.
    float3 direction : DIRECTION;
    nointerpolation uint probe : PROBE;
};

// A point of the unit sphere, by its stack and slice.
float3 GetSpherePoint(uint stack, uint slice)
{
    float theta = Pi * float(stack) / float(SphereStacks);
    float phi = TwoPi * float(slice) / float(SphereSlices);
    return float3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
}

// Six vertices a quad of the sphere, two triangles.
[shader("vertex")]
Varyings DrawProbesVertex(uint vertexIndex : SV_VertexID, uint probe : SV_InstanceID)
{
    FrameConstants frame = GetFrame();
    ProbeGridConstants grid = GetProbeGrid();
    uint quad = vertexIndex / 6u;
    uint2 corner = QuadCorners[vertexIndex % 6u];
    float3 direction = GetSpherePoint(quad / SphereSlices + corner.y, quad % SphereSlices + corner.x);
    float3 center = GetProbePosition(grid, GetProbeCoord(grid, probe), LoadProbe(grid, probe));
    float3 relative = Relative(frame, center + direction * grid.maxDistance * 0.05);

    Varyings output;
    output.position = mul(float4(relative, 1.0), frame.viewProjection);
    output.direction = direction;
    output.probe = probe;
    return output;
}

[shader("pixel")]
float4 DrawProbesPixel(Varyings input) : SV_Target0
{
    ProbeSh sh = CreateProbeSh(LoadProbe(GetProbeGrid(), input.probe));
    float3 radiance = max(EvaluateSh(sh, normalize(input.direction)), 0.0);
    return float4(radiance, 1.0);
}
