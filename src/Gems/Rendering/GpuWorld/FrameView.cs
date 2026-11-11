using System.Numerics;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// A view: its targets, all the picture's size, and its constants, its matrices, its targets and the frame's tables,
/// remembering last frame's so a thing that moved carries where it was.
/// </summary>
internal sealed class FrameView(GpuResources resources)
{
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
    /// <paramref name="jitter"/>, and the frame's models, uploaded: the instance and material tables.
    /// </summary>
    public Result<FrameTargets> Upload(
        RenderGraph graph,
        RenderCamera camera,
        in Frame frame,
        Vector2 jitter,
        int width,
        int height,
        int instanceCount,
        GpuBuffer instances,
        GpuBuffer materials
    )
    {
        Result<FrameTargets> made = TryGetTargets(graph, width, height);
        if (!made.Ok)
            return made;

        FrameTargets targets = made.Value;
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
            Depth = targets.Depth.Id,
            GBufferNormal = targets.GBufferNormal.Id,
            GBufferBase = targets.GBufferBase.Id,
            GBufferSurface = targets.GBufferSurface.Id,
            Velocity = targets.Velocity.Id,
            Output = targets.Output.Id,
            Hdr = targets.Hdr.Id,
            TimeSec = (float)frame.AppSec,
            DeltaSec = (float)frame.AppDeltaSec,
            GlassDepth = targets.GlassDepth.Id,
            GlassBase = targets.GlassBase.Id,
            GlassNormal = targets.GlassNormal.Id,
            GlassSurface = targets.GlassSurface.Id,
            PixelsPerRadian = height / (2 * MathF.Tan(camera.FieldOfView * MathF.PI / 360)),
            FrameIndex = frame.Index,
            Jitter = jitter,
        };
        GpuModels models = new()
        {
            Instances = instances.Id,
            Count = (uint)instanceCount,
            Vertices = resources.VertexPool.Id,
            Indices = resources.IndexPool.Id,
            Materials = materials.Id,
            Meshes = resources.Meshes.Id,
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
        graph.Upload(targets.Constants, [models], GpuModels.Offset);

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
    /// The view's constants and targets at a size, each asked for, the glass targets only while a material draws
    /// translucent, so the graph frees them in a world with none. It has history while the last frame drew it at this
    /// size: targets made again at another hold nothing of it.
    /// </summary>
    private Result<FrameTargets> TryGetTargets(RenderGraph graph, int width, int height)
    {
        Result<GpuBuffer> constants = graph.Buffer(SharedResources.Frame);
        Result<GpuTexture> depth = graph.Texture(GetShared(SharedResources.Depth));
        bool hasGlass = resources.HasTranslucent;
        Result<GpuTexture> glassDepth = GetGlass(
            GpuDescriptions.GetTexture(
                "Glass depth",
                width,
                height,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            )
        );
        Result<GpuTexture> glassBase = GetGlass(GetTarget("Glass base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> glassNormal = GetGlass(GetTarget("Glass normal", GpuFormat.R16G16Unorm));
        Result<GpuTexture> glassSurface = GetGlass(GetTarget("Glass surface", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> normal = graph.Texture(GetShared(SharedResources.GBufferNormal));
        Result<GpuTexture> baseColor = graph.Texture(GetTarget("GBuffer base", GpuFormat.R8G8B8A8Unorm));
        Result<GpuTexture> surface = graph.Texture(GetShared(SharedResources.GBufferSurface));
        Result<GpuTexture> velocity = graph.Texture(GetShared(SharedResources.Velocity));
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
                TextureUsages.Storage | TextureUsages.ColorTarget | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        foreach (
            Result asked in (ReadOnlySpan<Result>)
                [
                    constants,
                    depth,
                    glassDepth,
                    glassBase,
                    glassNormal,
                    glassSurface,
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
            new FrameTargets(
                constants.Value,
                width,
                height,
                HasHistory: _previous is { } last && last.Width == width && last.Height == height,
                depth.Value,
                glassDepth.Value,
                glassBase.Value,
                glassNormal.Value,
                glassSurface.Value,
                normal.Value,
                baseColor.Value,
                surface.Value,
                velocity.Value,
                hdr.Value,
                output.Value
            )
        );

        TextureDescription GetTarget(string name, GpuFormat format) =>
            GpuDescriptions.GetTexture(name, width, height, format);

        TextureDescription GetShared(in TextureDescription shared) => shared with { Width = width, Height = height };

        Result<GpuTexture> GetGlass(in TextureDescription glass) =>
            hasGlass ? graph.Texture(glass) : Result.Success(GpuTexture.None);
    }
}

/// <summary>
/// The frame's buffer and the view's targets at the picture's size, and whether last frame drew them.
/// </summary>
internal readonly record struct FrameTargets(
    GpuBuffer Constants,
    int Width,
    int Height,
    bool HasHistory,
    GpuTexture Depth,
    GpuTexture GlassDepth,
    GpuTexture GlassBase,
    GpuTexture GlassNormal,
    GpuTexture GlassSurface,
    GpuTexture GBufferNormal,
    GpuTexture GBufferBase,
    GpuTexture GBufferSurface,
    GpuTexture Velocity,
    GpuTexture Hdr,
    GpuTexture Output
);
