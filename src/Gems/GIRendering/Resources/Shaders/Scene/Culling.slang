// The tests a cluster or an instance passes to be drawn, each over a sphere in the camera-relative world: against
// the frustum, the projected error against the pixel threshold, and against the depth pyramid of what was drawn
// before (the sphere's screen rectangle after Mara and McGuire, "2D Polyhedral Bounds of a Clipped,
// Perspective-Projected 3D Sphere", 2013).
module "Scene/Culling";

import Core.Heap;
import Core.Frame;
import Core.Math;

public bool IsSphereInFrustum(FrameConstants frame, float3 center, float radius)
{
    [unroll]
    for (uint i = 0u; i < 6u; i++)
    {
        float4 plane = frame.frustumPlanes[i];
        if (dot(plane.xyz, center) + plane.w < -radius)
            return false;
    }

    return true;
}

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

// Whether the sphere's silhouette fits a rectangle of clip-space xy, with y up, and that rectangle: false when the
// sphere crosses the near plane.
public bool ProjectSphere(float3 viewCenter, float radius, float near, float p00, float p11, out float4 rectangle)
{
    rectangle = float4(0.0);
    if (viewCenter.z - radius < near)
        return false;

    float2 cx = viewCenter.xz;
    float2 vx = float2(sqrt(dot(cx, cx) - radius * radius), radius);
    float2 minx = float2(vx.x * cx.x - vx.y * cx.y, vx.y * cx.x + vx.x * cx.y);
    float2 maxx = float2(vx.x * cx.x + vx.y * cx.y, -vx.y * cx.x + vx.x * cx.y);

    float2 cy = viewCenter.yz;
    float2 vy = float2(sqrt(dot(cy, cy) - radius * radius), radius);
    float2 miny = float2(vy.x * cy.x - vy.y * cy.y, vy.y * cy.x + vy.x * cy.y);
    float2 maxy = float2(vy.x * cy.x + vy.y * cy.y, -vy.y * cy.x + vy.x * cy.y);

    float x0 = minx.x / minx.y * p00;
    float x1 = maxx.x / maxx.y * p00;
    float y0 = miny.x / miny.y * p11;
    float y1 = maxy.x / maxy.y * p11;
    rectangle = float4(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1));
    return true;
}

// Whether everything drawn in the pyramid is nearer than the sphere's nearest point, so nothing of it can show. The
// pyramid holds the farthest reversed depth of each region, and reversed depth is greater nearer.
public bool IsOccluded(FrameConstants frame, float3 center, float radius)
{
    float3 viewCenter = mul(float4(center, 1.0), frame.view).xyz;
    float4 rectangle;
    if (!ProjectSphere(viewCenter, radius, frame.near, frame.projection[0][0], frame.projection[1][1], rectangle))
        return false;

    float2 uvMin = saturate(ToUv(rectangle.xw));
    float2 uvMax = saturate(ToUv(rectangle.zy));
    float2 pixels = (uvMax - uvMin) * float2(frame.width, frame.height);
    float level = clamp(ceil(log2(max(max(pixels.x, pixels.y), 1.0))), 0.0, float(frame.hzbLevels - 1u));
    TextureRef hzb = { frame.hzb };
    uint2 size = hzb.GetSize(uint(level));
    uint2 t0 = min(uint2(uvMin * float2(size)), size - 1u);
    uint2 t1 = min(uint2(uvMax * float2(size)), size - 1u);
    float farthest = min(
        min(hzb.Load(t0, uint(level)).x, hzb.Load(uint2(t1.x, t0.y), uint(level)).x),
        min(hzb.Load(uint2(t0.x, t1.y), uint(level)).x, hzb.Load(t1, uint(level)).x)
    );
    float nearest = ToReversedDepth(viewCenter.z - radius, frame.near);
    return nearest < farthest;
}
