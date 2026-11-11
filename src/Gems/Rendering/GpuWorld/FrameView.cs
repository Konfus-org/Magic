using System.Numerics;
using Magic.Assets;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// The frame's rows written from its view: the view's constants, its matrices and targets, the frame's tables and its
/// lights, remembering last frame's view so a thing that moved carries where it was.
/// </summary>
internal sealed class FrameView
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
    /// The frame's rows uploaded into its buffer: the view's constants, the camera shifted by <paramref name="jitter"/>;
    /// the models, the instance and material tables; and the lights, the sun, the open sky at
    /// <paramref name="skyBrightness"/>, the sky's tables, and the light table and its tiles.
    /// </summary>
    public Result Upload(
        RenderGraph graph,
        in GpuView view,
        (Transform Transform, Camera Camera) camera,
        in Frame frame,
        Vector2 jitter,
        (Vector3 Direction, Vector3 Illuminance) sun,
        bool hasSky,
        float skyBrightness
    )
    {
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

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
        };
        GpuModels models = new()
        {
            Instances = view.Instances.Id,
            Count = (uint)view.InstanceCount,
            Vertices = view.Vertices.Id,
            Indices = view.Indices.Id,
            Materials = view.Materials.Id,
            Meshes = view.Meshes.Id,
        };
        GpuLights frameLights = new()
        {
            SunDirection = new Vector4(sun.Direction, 0),
            SunIlluminance = new Vector4(sun.Illuminance, 0),
            SkyRadiance = new Vector4(skyBrightness * 0.8f, skyBrightness * 0.9f, skyBrightness, 0),
            CameraAltitude = Math.Max(camera.Transform.Position.Y, 0),
            TransmittanceLut = view.SkyTransmittance.Id,
            MultiScatteringLut = view.SkyMultiScattering.Id,
            SkyViewLut = view.SkyView.Id,
            LutSampler = sampler.Value.Id,
            HasSky = hasSky ? 1u : 0u,
            Table = view.LightTable.Id,
            Count = (uint)view.LightCount,
            Tiles = view.LightTiles.Id,
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
            _previous is { } last && view.HasHistory
                ? (last.ViewProjection, last.CameraPosition)
                : (unjittered, constants.CameraPosition);
        _previous = (unjittered, constants.CameraPosition, width, height);
        graph.Upload(view.Frame, [constants]);
        graph.Upload(view.Frame, [models], GpuModels.Offset);
        graph.Upload(view.Frame, [frameLights], GpuLights.Offset);

        return Result.Success();
    }

    /// <summary>
    /// Whether the last frame drew the view at this size: targets made again at another hold nothing of it.
    /// </summary>
    public bool HasHistory(int width, int height) =>
        _previous is { } last && last.Width == width && last.Height == height;

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
}
