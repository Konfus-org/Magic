using System.Numerics;
using Magic.Projects;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// A view: its targets, all the window's size, and its constants, its matrices, its targets and the frame's tables,
/// remembering last frame's so a thing that moved carries where it was.
/// </summary>
internal sealed class FrameView(GpuResources resources)
{
    private const float ErrorThresholdPixels = 1;
    private const float SkyBrightness = 0.3f;
    private const uint JitterFrames = 256;

    /// <summary>
    /// Last frame's view and its size, while a frame was drawn since the view was last forgotten: what a thing that
    /// moved carries where it was from, and whether last frame's depth pyramid is there to cull against.
    /// </summary>
    private (Matrix4x4 ViewProjection, Vector4 CameraPosition, int Width, int Height)? _previous;

    /// <summary>
    /// The jitter of frame <paramref name="frameIndex"/>, in pixels from the pixel's middle: a point in the pixel from
    /// the Halton sequence in bases 2 and 3 that starts again every 256 frames, so an effect that blends frames sees
    /// another point of each pixel each frame.
    /// </summary>
    public static Vector2 GetJitter(uint frameIndex)
    {
        uint term = (frameIndex % JitterFrames) + 1;

        return new Vector2(GetHalton(term, 2) - 0.5f, GetHalton(term, 3) - 0.5f);
    }

    /// <summary>
    /// Forgets the last frame's camera, depth pyramid and shadows, so the next frame moves nothing on from where a view
    /// that is gone looked, culls against no pyramid of it and blends no shadows of it.
    /// </summary>
    public void ClearHistory() => _previous = null;

    /// <summary>
    /// The main view at a size, its targets asked for, and its constants, the camera shifted by
    /// <paramref name="jitter"/>, the sun and the frame's resources, copied in a transfer pass the caller opened: the
    /// instance and material tables, the cull's buffers and the world's acceleration structure and hit materials, which
    /// the caller asked for before.
    /// </summary>
    public Result<RenderView> Upload(
        RenderGraph graph,
        RenderCamera camera,
        RenderSun sun,
        in Frame frame,
        Vector2 jitter,
        int width,
        int height,
        int instanceCount,
        GpuBuffer instances,
        GpuBuffer materials,
        in CullBuffers cull,
        GpuAcceleration acceleration,
        GpuBuffer hitMaterials,
        float impostorPixels,
        float impostorDistance
    )
    {
        Result<RenderView> made = TryGetView(graph, width, height, instanceCount, instances, materials, cull);
        if (!made.Ok)
            return made;

        RenderView targets = made.Value;
        float aspect = (float)width / height;
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 view);
        Matrix4x4 projection = CreatePerspective(camera.FieldOfView, aspect, camera.Near);
        Matrix4x4 unjittered = view * projection;
        projection.M31 = jitter.X * 2 / width;
        projection.M32 = -jitter.Y * 2 / height;
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        FrameConstants constants = new()
        {
            View = view,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new Vector4(camera.Transform.Position, 0),
            Width = (uint)width,
            Height = (uint)height,
            Near = camera.Near,
            InstanceCount = (uint)instanceCount,
            Instances = instances.Id,
            Vertices = resources.VertexPool.Id,
            Indices = resources.IndexPool.Id,
            Materials = materials.Id,
            Depth = targets.Depth.Id,
            GBufferNormal = targets.GBufferNormal.Id,
            GBufferBase = targets.GBufferBase.Id,
            GBufferSurface = targets.GBufferSurface.Id,
            Velocity = targets.Velocity.Id,
            Output = targets.Output.Id,
            Hdr = targets.Hdr.Id,
            HitMaterials = hitMaterials.Id,
            TimeSec = (float)frame.AppSec,
            DeltaSec = (float)frame.AppDeltaSec,
            SunDirection = new Vector4(sun.Direction, 0),
            SunRadiance = new Vector4(sun.Radiance, 0),
            SkyRadiance = new Vector4(SkyBrightness * 0.8f, SkyBrightness * 0.9f, SkyBrightness, 0),
            PixelsPerRadian = height / (2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360)),
            FrameIndex = frame.Index,
            Jitter = jitter,
            Acceleration = acceleration.Id,
        };
        ClusterConstants clusters = new()
        {
            HzbLevels = (uint)targets.HzbLevels,
            HasHzb = targets.HasHistory ? 1u : 0u,
            ErrorThreshold = ErrorThresholdPixels,
            ImpostorPixels = impostorPixels,
            ImpostorDistance = impostorDistance,
            MaxVisibleClusters = Visibility.MaxVisibleClusters,
            Clusters = resources.ClusterPool.Id,
            LeafClusters = resources.LeafClusters.Id,
            VisibleClusters = cull.VisibleClusters.Id,
            DrawArguments = cull.DrawArguments.Id,
            Counts = cull.Counts.Id,
            RetestList = cull.RetestList.Id,
            RetestArguments = cull.RetestArguments.Id,
            Hzb = targets.Hzb.Id,
            Visibility = targets.Visibility.Id,
            GlassVisibility = targets.GlassVisibility.Id,
        };

        // The frustum's planes from the view-projection, for row vectors: each a column added to or taken from the
        // last, scaled so its normal is a unit; the far plane is the third column alone, which a perspective with no
        // far plane never rejects by.
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
        constants.FrustumPlane0 = Normalize(c3 + c0);
        constants.FrustumPlane1 = Normalize(c3 - c0);
        constants.FrustumPlane2 = Normalize(c3 + c1);
        constants.FrustumPlane3 = Normalize(c3 - c1);
        constants.FrustumPlane4 = Normalize(c3 - c2);
        constants.FrustumPlane5 = Normalize(c2);

        // Where the view was last frame, or where it is now when no frame was drawn at this size since it was
        // forgotten, with no jitter, so a velocity is the surface's move alone.
        (constants.PreviousViewProjection, constants.PreviousCameraPosition) =
            _previous is { } last && targets.HasHistory
                ? (last.ViewProjection, last.CameraPosition)
                : (unjittered, constants.CameraPosition);
        _previous = (unjittered, constants.CameraPosition, width, height);
        graph.Upload(targets.Constants, [constants]);
        graph.Upload(targets.Constants, [clusters], FrameConstants.Bytes);

        return made;
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
    /// The Halton sequence's term in a base, 0 to 1: a different one each frame that fills the pixel evenly.
    /// </summary>
    private static float GetHalton(uint index, uint radix)
    {
        float result = 0;
        float fraction = 1f / radix;
        while (index > 0)
        {
            result += fraction * (index % radix);
            index /= radix;
            fraction /= radix;
        }

        return result;
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

    /// <summary>
    /// The view's constants and targets at a size, each asked for, beside the frame's tables. It has history while the
    /// last frame drew it at this size: targets made again at another hold nothing of it.
    /// </summary>
    private Result<RenderView> TryGetView(
        RenderGraph graph,
        int width,
        int height,
        int instanceCount,
        GpuBuffer instances,
        GpuBuffer materials,
        in CullBuffers cull
    )
    {
        Result<GpuBuffer> constants = graph.Buffer(
            GpuDescriptions.GetBuffer("Frame constants", FrameConstants.Bytes + ClusterConstants.Bytes)
        );
        Result<GpuTexture> visibilityBuffer = graph.Texture(
            GpuDescriptions.GetTexture(
                "Visibility",
                width,
                height,
                GpuFormat.R32Uint,
                TextureUsages.ColorTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> depth = graph.Texture(
            GpuDescriptions.GetTexture(
                "Depth",
                width,
                height,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> glassVisibility = graph.Texture(
            GpuDescriptions.GetTexture(
                "Glass visibility",
                width,
                height,
                GpuFormat.R32Uint,
                TextureUsages.ColorTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> glassDepth = graph.Texture(
            GpuDescriptions.GetTexture("Glass depth", width, height, GpuFormat.D32Sfloat, TextureUsages.DepthTarget)
        );
        Result<GpuTexture> hzb = graph.Texture(
            GpuDescriptions.GetTexture(
                "Depth pyramid",
                width,
                height,
                GpuFormat.R32Sfloat,
                levels: TextureDescription.GetFullLevels(width, height)
            )
        );
        Result<GpuTexture> normal = graph.Texture(GetTarget("GBuffer normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> baseColor = graph.Texture(GetTarget("GBuffer base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> surface = graph.Texture(GetTarget("GBuffer surface", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> velocity = graph.Texture(GetTarget("Velocity", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> hdr = graph.Texture(
            GpuDescriptions.GetTexture(
                "HDR",
                width,
                height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Storage
                    | TextureUsages.ColorTarget
                    | TextureUsages.Sampled
                    | TextureUsages.CopyDestination
            )
        );
        Result<GpuTexture> output = graph.Texture(
            GpuDescriptions.GetTexture(
                "Output",
                width,
                height,
                GpuFormat.R8G8B8A8Unorm,
                TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.CopySource
            )
        );
        foreach (
            Result asked in (ReadOnlySpan<Result>)
                [
                    constants,
                    visibilityBuffer,
                    depth,
                    glassVisibility,
                    glassDepth,
                    hzb,
                    normal,
                    baseColor,
                    surface,
                    velocity,
                    hdr,
                    output,
                ]
        )
        {
            if (!asked.Ok)
                return Result.Failure(asked.Error);
        }

        return Result.Success(
            new RenderView(
                constants.Value,
                instances,
                materials,
                instanceCount,
                cull,
                width,
                height,
                HasHistory: _previous is { } last && last.Width == width && last.Height == height,
                visibilityBuffer.Value,
                depth.Value,
                glassVisibility.Value,
                glassDepth.Value,
                hzb.Value,
                normal.Value,
                baseColor.Value,
                surface.Value,
                velocity.Value,
                hdr.Value,
                output.Value,
                graph
            )
        );

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);
    }
}
