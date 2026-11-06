using System.Numerics;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A view's constants: its matrices, its targets and the frame's tables, remembering last frame's so a thing that moved
/// carries where it was.
/// </summary>
internal sealed class FrameView(
    IGraphicsApi gpu,
    GeometryPools pools,
    MaterialTable materials,
    Visibility visibility,
    RayTracing? rayTracing
) : IDisposable
{
    private const float ErrorThresholdPixels = 1;
    private const float SkyBrightness = 0.3f;

    private GpuSampler _sampler;

    /// <summary>
    /// Last frame's view, while a frame was drawn since the view was last forgotten: what a thing that moved carries
    /// where it was from, and whether last frame's depth pyramid is there to cull against.
    /// </summary>
    private (Matrix4x4 ViewProjection, Vector4 CameraPosition)? _previous;

    /// <summary>
    /// The main view's constants.
    /// </summary>
    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        gpu.Destroy(Constants);
        gpu.Destroy(_sampler);
    }

    public Result Create()
    {
        Result<GpuSampler> sampler = gpu.CreateSampler(new SamplerDescription { Anisotropy = 8 });
        if (!sampler.Ok)
            return Result.Failure($"the default sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;
        Result<GpuBuffer> constants = gpu.CreateBuffer("Frame constants", FrameConstants.Bytes);
        if (!constants.Ok)
            return Result.Failure(constants.Error);

        Constants = constants.Value;

        return Result.Success();
    }

    /// <summary>
    /// Forgets the last frame's camera and depth pyramid, so the next frame moves nothing on from where a view that is
    /// gone looked, and culls against no pyramid of it.
    /// </summary>
    public void ClearHistory() => _previous = null;

    /// <summary>
    /// The main view's constants, the camera, the sun and the frame's resources, copied into <see cref="Constants"/>
    /// in a transfer pass the caller opened.
    /// </summary>
    public void Upload(
        RenderGraph graph,
        RenderCamera camera,
        RenderSun sun,
        uint frameIndex,
        FrameTargets targets,
        GpuBuffer instances,
        int instanceCount
    )
    {
        float aspect = (float)targets.Width / targets.Height;
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 view);
        Matrix4x4 projection = CreatePerspective(camera.FieldOfView, aspect, camera.Near);
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        FrameConstants frame = new()
        {
            View = view,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new Vector4(camera.Transform.Position, 0),
            Width = (uint)targets.Width,
            Height = (uint)targets.Height,
            HzbLevels = (uint)targets.HzbLevels,
            Near = camera.Near,
            InstanceCount = (uint)instanceCount,
            MaxVisibleClusters = Visibility.MaxVisibleClusters,
            DefaultSampler = _sampler.Id,
            Instances = instances.Id,
            Clusters = pools.Clusters.Id,
            Vertices = pools.Vertices.Id,
            Indices = pools.Indices.Id,
            Materials = materials.Buffer.Id,
            VisibleClusters = visibility.VisibleClusters.Id,
            DrawArguments = visibility.DrawArguments.Id,
            Counts = visibility.Counts.Id,
            RetestList = visibility.RetestList.Id,
            RetestArguments = visibility.RetestArguments.Id,
            Hzb = targets.Hzb.Id,
            Visibility = targets.Visibility.Id,
            Depth = targets.Depth.Id,
            GBufferNormal = targets.GBufferNormal.Id,
            GBufferBase = targets.GBufferBase.Id,
            GBufferSurface = targets.GBufferSurface.Id,
            Velocity = targets.Velocity.Id,
            Output = targets.Output.Id,
            SunDirection = new Vector4(sun.Direction, 0),
            SunRadiance = new Vector4(sun.Radiance, 0),
            SkyRadiance = new Vector4(SkyBrightness * 0.8f, SkyBrightness * 0.9f, SkyBrightness, 0),
            HasHzb = _previous is null ? 0u : 1u,
            PixelsPerRadian = targets.Height / (2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360)),
            ErrorThreshold = ErrorThresholdPixels,
            FrameIndex = frameIndex,
            Acceleration = rayTracing?.Acceleration.Id ?? 0,
            LeafClusters = pools.LeafClusters.Id,
        };

        // The frustum's planes from the view-projection, for row vectors: each a column added to or taken from the
        // last, scaled so its normal is a unit; the far plane is the third column alone, which a perspective with no
        // far plane never rejects by.
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
        frame.FrustumPlane0 = Normalize(c3 + c0);
        frame.FrustumPlane1 = Normalize(c3 - c0);
        frame.FrustumPlane2 = Normalize(c3 + c1);
        frame.FrustumPlane3 = Normalize(c3 - c1);
        frame.FrustumPlane4 = Normalize(c3 - c2);
        frame.FrustumPlane5 = Normalize(c2);

        // Where the view was last frame, or where it is now when no frame was drawn since it was forgotten.
        (Matrix4x4 ViewProjection, Vector4 CameraPosition) previous =
            _previous ?? (viewProjection, frame.CameraPosition);
        frame.PreviousViewProjection = previous.ViewProjection;
        frame.PreviousCameraPosition = previous.CameraPosition;
        _previous = (viewProjection, frame.CameraPosition);
        graph.Upload(gpu, Constants, in frame);
    }

    /// <summary>
    /// The plane scaled so its normal is a unit; the far plane's is zero, and stays so.
    /// </summary>
    private static Vector4 Normalize(Vector4 plane)
    {
        float length = MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));

        return length > 0 ? plane / length : plane;
    }

    /// <summary>
    /// A perspective with no far plane and reversed depth, for row vectors: depth is the near distance over the view
    /// depth, 1 at the near plane and 0 at infinity.
    /// </summary>
    private static Matrix4x4 CreatePerspective(float fieldOfViewDegrees, float aspect, float near)
    {
        float focal = 1 / MathF.Tan(fieldOfViewDegrees * MathF.PI / 360);
        Matrix4x4 projection = default;
        projection.M11 = focal / aspect;
        projection.M22 = focal;
        projection.M34 = 1;
        projection.M43 = near;

        return projection;
    }
}
