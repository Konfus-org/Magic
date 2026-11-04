// The world trace contract: what follows a ray through the world and says what it hit, as the distance field does by
// sphere tracing, or as a ray query against the GPU's own structures would.
module "Trace/IWorldTracer";

public struct Ray
{
    public float3 origin;
    // Unit.
    public float3 direction;
}

public struct TraceLimits
{
    public float start;
    public float end;
    // The tangent of the half angle of the cone the ray widens into, 0 for a line.
    public float coneTangent;
}

public struct TraceHit
{
    public bool isHit;
    // Metres along the ray.
    public float distance;
    // Unit, in the world.
    public float3 normal;
    // How much of the cone got through, 1 where nothing was near it: a soft shadow's visibility.
    public float visibility;
    // The instance hit, when the tracer knows it; TraceNoInstance when it does not.
    public uint instance;
}

public static const uint TraceNoInstance = 0xFFFFFFFFu;

public interface IWorldTracer
{
    static TraceHit Trace(Ray ray, TraceLimits limits);
}
