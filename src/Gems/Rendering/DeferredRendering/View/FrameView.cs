using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering.World;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.View;

/// <summary>
/// Writes the frame's rows from its view: the view's constants, its matrices and targets, the frame's tables and its
/// lights.
/// </summary>
/// <remarks>
/// It remembers last frame's view, so a thing that moved carries where it was.
/// </remarks>
internal sealed class FrameView(IGraphicsApi gpu)
{
    private const uint JitterFrames = 256;

    /// <summary>
    /// Last frame's view and its size, while a frame was drawn since the view was last forgotten.
    /// </summary>
    /// <remarks>
    /// A thing that moved carries where it was from this view, and it tells whether the view has a history.
    /// </remarks>
    private (Matrix4x4 ViewProjection, Vector4 CameraPosition, int Width, int Height)? _previous;

    /// <summary>
    /// The rows the last <see cref="Upload"/> wrote.
    /// </summary>
    internal FrameConstants Constants { get; private set; }

    /// <summary>
    /// Gets the jitter of frame <paramref name="frameIndex"/>, in pixels from the pixel's middle.
    /// </summary>
    /// <remarks>
    /// It is a point in the pixel from the Halton sequence in bases 2 and 3 that starts again every 256 frames, so an
    /// effect that blends frames sees another point of each pixel each frame.
    /// </remarks>
    internal static Vector2 GetJitter(uint frameIndex)
    {
        uint term = (frameIndex % JitterFrames) + 1;

        return new Vector2(GetHalton(term, 2) - 0.5f, GetHalton(term, 3) - 0.5f);
    }

    /// <summary>
    /// Gets the frame's main camera as the work the views share sees it, at the size of the whole picture, and its
    /// constants as the world's buffer holds them: no jitter, no targets, and no motion since last frame.
    /// </summary>
    internal static (WorldEye Eye, FrameConstants Constants) CreateEye(
        (Transform Transform, Camera Camera) camera,
        System.Drawing.Size picture
    )
    {
        int width = Math.Max(picture.Width, 1);
        int height = Math.Max(picture.Height, 1);
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 worldToView);
        Matrix4x4 projection = CreatePerspective(camera.Camera.FieldOfView, (float)width / height, camera.Camera.Near);
        Matrix4x4 viewProjection = worldToView * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        float pixelsPerRadian = height / (2 * MathF.Tan(camera.Camera.FieldOfView * MathF.PI / 360));
        Vector4 position = new(camera.Transform.Position, 0);
        FrameConstants constants = new()
        {
            View = worldToView,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = position,
            PreviousViewProjection = viewProjection,
            PreviousCameraPosition = position,
            Width = (uint)width,
            Height = (uint)height,
            Near = camera.Camera.Near,
            PixelsPerRadian = pixelsPerRadian,
        };
        SetFrustum(ref constants);

        return (
            new WorldEye(camera.Transform.Position, viewProjection, pixelsPerRadian, camera.Camera.Near),
            constants
        );
    }

    /// <summary>
    /// Forgets the last frame's camera and size, so the next frame moves nothing on from where a view that is gone
    /// looked, and each gem that keeps a history of the view starts it again.
    /// </summary>
    internal void ClearHistory() => _previous = null;

    /// <summary>
    /// Uploads the frame's rows into its buffer.
    /// </summary>
    /// <remarks>
    /// The rows are the view's constants, with the camera shifted by <paramref name="jitter"/> and textures read at
    /// <paramref name="mipBias"/>; the models, the
    /// instance and material tables of <paramref name="world"/>; the lights: the sun, the open sky at
    /// <paramref name="skyBrightness"/>, the sampler of the sky's tables, and the light table's lights and its tiles;
    /// the world as rays see it, <paramref name="traced"/>; and, at 0, the rows the gems after the view write.
    /// </remarks>
    internal Result Upload(
        RenderGraph graph,
        in GpuView view,
        in GpuWorld world,
        (Transform Transform, Camera Camera) camera,
        in RenderFrame frame,
        Vector2 jitter,
        float mipBias,
        (Vector3 Direction, Vector3 Illuminance) sun,
        float skyBrightness,
        GpuTracedWorld traced
    )
    {
        Result<GpuSampler> sampler = gpu.GetSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure(sampler.Error);

        int width = view.Width;
        int height = view.Height;
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(camera.Transform.Rotation), out Matrix4x4 worldToView);
        Matrix4x4 projection = CreatePerspective(camera.Camera.FieldOfView, (float)width / height, camera.Camera.Near);
        Matrix4x4 unjittered = worldToView * projection;
        projection.M31 = jitter.X * 2 / width;
        projection.M32 = -jitter.Y * 2 / height;
        Matrix4x4 viewProjection = worldToView * projection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        FrameConstants constants = new()
        {
            View = worldToView,
            Projection = projection,
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            CameraPosition = new Vector4(camera.Transform.Position, 0),
            Width = (uint)width,
            Height = (uint)height,
            Near = camera.Camera.Near,
            Depth = view.Depth.Id,
            GBufferNormal = view.GBufferNormal.Id,
            GBufferBase = view.GBufferBase.Id,
            GBufferSurface = view.GBufferSurface.Id,
            Velocity = view.Velocity.Id,
            Output = view.Output.Id,
            Hdr = view.Hdr.Id,
            TimeSec = (float)frame.AppSec,
            DeltaSec = (float)frame.AppDeltaSec,
            GlassDepth = view.GlassDepth.Id,
            GlassBase = view.GlassBase.Id,
            GlassNormal = view.GlassNormal.Id,
            GlassSurface = view.GlassSurface.Id,
            PixelsPerRadian = height / (2 * MathF.Tan(camera.Camera.FieldOfView * MathF.PI / 360)),
            FrameIndex = frame.Index,
            Jitter = jitter,
            MipBias = mipBias,
        };
        GpuLights frameLights = new()
        {
            SunDirection = new Vector4(sun.Direction, 0),
            SunIlluminance = new Vector4(sun.Illuminance, 0),
            SkyRadiance = new Vector4(Renderer.GetSkyRadiance(skyBrightness), 0),
            CameraAltitude = Math.Max(camera.Transform.Position.Y, 0),
            LutSampler = sampler.Value.Id,
            Table = view.LightTable.Id,
            Count = (uint)world.Lights.Count,
            Tiles = view.LightTiles.Id,
        };

        SetFrustum(ref constants);

        // Where the view was last frame, or where it is now when no frame was drawn at this size since it was
        // forgotten, with no jitter, so a velocity is the surface's move alone.
        (constants.PreviousViewProjection, constants.PreviousCameraPosition) =
            _previous is { } last && view.HasHistory
                ? (last.ViewProjection, last.CameraPosition)
                : (unjittered, constants.CameraPosition);
        _previous = (unjittered, constants.CameraPosition, width, height);
        Constants = constants;
        bool isSent = graph.Upload(view.Frame, [constants]);
        isSent &= graph.Upload(view.Frame, [world.ModelsRow], GpuModels.Offset);
        isSent &= graph.Upload(view.Frame, [frameLights], GpuLights.Offset);

        // The rows the gems after the view write, 0 until one does, so the shaders that read them shadow and light
        // nothing indirectly, the open sky lights the surfaces, and the air changes nothing.
        isSent &= graph.Upload(view.Frame, [traced], GpuTracedWorld.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuSkyTables)], GpuSkyTables.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuShadows)], GpuShadows.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuLocalShadows)], GpuLocalShadows.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuAmbient)], GpuAmbient.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuIndirectLight)], GpuIndirectLight.Offset);
        isSent &= graph.Upload(view.Frame, [default(GpuAir)], GpuAir.Offset);

        return isSent ? Result.Success() : Result.Failure("the frame's constants were not staged.");
    }

    /// <summary>
    /// Whether the last frame drew the view at this size: targets made again at another hold nothing of it.
    /// </summary>
    internal bool HasHistory(int width, int height) =>
        _previous is { } last && last.Width == width && last.Height == height;

    /// <summary>
    /// Sets the frustum's planes from the constants' view-projection, for row vectors: each a column added to or taken
    /// from the last, scaled so its normal is a unit. The far plane is the third column alone, which a perspective with
    /// no far plane never rejects by.
    /// </summary>
    private static void SetFrustum(ref FrameConstants constants)
    {
        Matrix4x4 viewProjection = constants.ViewProjection;
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
    }

    /// <summary>
    /// Scales the plane so its normal is a unit; the far plane's is zero, and stays so.
    /// </summary>
    private static Vector4 Normalize(Vector4 plane)
    {
        float length = MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));

        return length > 0 ? plane / length : plane;
    }

    /// <summary>
    /// Gets the Halton sequence's term in a base, 0 to 1: a different one each frame that fills the pixel evenly.
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
    /// Creates a perspective with no far plane and reversed depth, for row vectors.
    /// </summary>
    /// <remarks>
    /// Depth is the near distance over the view depth, 1 at the near plane and 0 at infinity.
    /// </remarks>
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
