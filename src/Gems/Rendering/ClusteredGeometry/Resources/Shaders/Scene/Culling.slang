// The tests a cluster or an instance passes to be drawn, each over a sphere in the camera-relative world: against
// the frustum, the projected error against the pixel threshold, and against the depth pyramid of what was drawn
// before (the sphere's screen rectangle after Mara and McGuire, "2D Polyhedral Bounds of a Clipped,
// Perspective-Projected 3D Sphere", 2013).
module "Scene/Culling";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Scene.Clusters;

// The pixel radius under which an instance's sphere is less than five pixels across on screen, too small to notice,
// and draws nothing.
public static const float MinPixelRadius = 2.5;

// The sphere's radius in pixels on screen.
public float GetPixelRadius(FrameConstants frame, float3 center, float radius)
{
    return radius / max(length(center), 1e-3) * frame.pixelsPerRadian;
}

// Pixels an error of error metres covers at the sphere's nearest point: how wrong the cluster looks on screen.
public float ProjectError(FrameConstants frame, float3 center, float radius, float error)
{
    float distance = max(length(center) - radius, frame.near);
    return error * frame.pixelsPerRadian / distance;
}

// Whether everything drawn in the pyramid is nearer than the sphere's nearest point, so nothing of it can show. The
// pyramid holds the farthest reversed depth of each region, and reversed depth is greater nearer.
public bool IsOccluded(FrameConstants frame, float3 center, float radius)
{
    // A sphere that crosses the near plane has no rectangle to test.
    float3 viewCenter = mul(float4(center, 1.0), frame.view).xyz;
    if (viewCenter.z - radius < frame.near)
        return false;

    // The rectangle of clip-space xy, with y up, that the sphere's silhouette fits.
    float2 cx = viewCenter.xz;
    float2 vx = float2(sqrt(dot(cx, cx) - radius * radius), radius);
    float2 minx = float2(vx.x * cx.x - vx.y * cx.y, vx.y * cx.x + vx.x * cx.y);
    float2 maxx = float2(vx.x * cx.x + vx.y * cx.y, -vx.y * cx.x + vx.x * cx.y);
    float2 cy = viewCenter.yz;
    float2 vy = float2(sqrt(dot(cy, cy) - radius * radius), radius);
    float2 miny = float2(vy.x * cy.x - vy.y * cy.y, vy.y * cy.x + vy.x * cy.y);
    float2 maxy = float2(vy.x * cy.x + vy.y * cy.y, -vy.y * cy.x + vy.x * cy.y);
    float x0 = minx.x / minx.y * frame.projection[0][0];
    float x1 = maxx.x / maxx.y * frame.projection[0][0];
    float y0 = miny.x / miny.y * frame.projection[1][1];
    float y1 = maxy.x / maxy.y * frame.projection[1][1];
    float4 rectangle = float4(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1));

    ClusterConstants clusters = GetClusters();

    float2 uvMin = saturate(ToUv(rectangle.xw));
    float2 uvMax = saturate(ToUv(rectangle.zy));
    float2 pixels = (uvMax - uvMin) * float2(frame.width, frame.height);
    float level = clamp(ceil(log2(max(max(pixels.x, pixels.y), 1.0))), 0.0, float(clusters.depthPyramidLevels - 1u));
    TextureRef depthPyramid = { clusters.depthPyramid };
    uint2 size = depthPyramid.GetSize(uint(level));
    uint2 t0 = GetTexelAt(uvMin, size);
    uint2 t1 = GetTexelAt(uvMax, size);
    float farthest = min(
        min(depthPyramid.Load(t0, uint(level)).x, depthPyramid.Load(uint2(t1.x, t0.y), uint(level)).x),
        min(depthPyramid.Load(uint2(t0.x, t1.y), uint(level)).x, depthPyramid.Load(t1, uint(level)).x)
    );
    float nearest = ToReversedDepth(viewCenter.z - radius, frame.near);
    return nearest < farthest;
}
