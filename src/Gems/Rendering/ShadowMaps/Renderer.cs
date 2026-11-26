using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ShadowMaps;

/// <summary>
/// What the cascade fit takes for each view of the frame: the view as the GPU holds it.
/// </summary>
public readonly record struct CascadeFitInput(GpuView View);

/// <summary>
/// What the sun's resolve takes for each view: the view, its cascades and their depth.
/// </summary>
public readonly record struct SunShadowInput(GpuView View, CascadeViews Cascades, CascadeDepth Depth);

/// <summary>
/// Fits the sun's cascades to each view, and shadows each pixel of the view from their depth, into the frame's
/// <see cref="ShadowMask"/>.
/// </summary>
/// <remarks>
/// Each frame it splits the view into cascades from the near plane to the shadow distance, fits a sphere around each
/// cascade's slice of the view, and gives each the sphere's square from the sun, snapped to whole texels, so a shadow
/// keeps still as the camera turns and moves. A geometry gem draws their depth. The resolve then writes the sun's
/// slot of the mask and the ids of the mask into the frame's buffer.
/// </remarks>
public sealed class Renderer : IRender<CascadeFitInput, CascadeViews>, IRender<SunShadowInput, ShadowMask>
{
    /// <summary>
    /// The most cascades a view has: two by two in the atlas.
    /// </summary>
    private const int MaxCascades = 4;

    private const int BytesPerPixel = 16;

    /// <summary>
    /// The levels of the blocker mips, from half the atlas to a sixteenth.
    /// </summary>
    private const int BlockerMipLevels = 4;

    /// <summary>
    /// The most steps of the contact shadow's march.
    /// </summary>
    private const int MostContactSteps = 64;

    /// <summary>
    /// The metres towards the sun from a cascade's centre that its near plane is at, so every caster up to there is
    /// drawn where it is. A caster further off is drawn at the near plane.
    /// </summary>
    private const float CasterReach = 1000;

    /// <summary>
    /// The steps a cascade's radius is rounded up to, in metres, so that the rounding of the sphere's fit does not
    /// change its size from frame to frame.
    /// </summary>
    private const float RadiusStep = 1f / 16;

    // A name for each level of the blocker mips: GPU timings are kept by name.
    private static readonly ImmutableArray<string> _blockerMipPasses =
    [
        "Build blocker mips 0",
        "Build blocker mips 1",
        "Build blocker mips 2",
        "Build blocker mips 3",
    ];

    private readonly ShadowMapsSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _resolveShader;

    private readonly ShaderEntry _blockerMipShader;

    internal Renderer(ShadowMapsSettings settings, AssetManager assets, IGraphicsApi gpu)
    {
        _settings = settings;
        _gpu = gpu;
        _resolveShader = PassShaders.GetEntry(assets, "ResolveSunShadow", "ResolveSunShadow");
        _blockerMipShader = PassShaders.GetEntry(assets, "BuildBlockerMips", "BuildBlockerMips");
    }

    /// <summary>
    /// Fits the sun's cascades to the view of this turn, and uploads their rows.
    /// </summary>
    /// <returns>
    /// The view's cascades, or none while shadow maps are off or their rows are not made.
    /// </returns>
    public CascadeViews? Render(in RenderFrame frame, RenderGraph graph, in CascadeFitInput input)
    {
        if (!_settings.IsShadowMapsOn)
            return null;

        int count = Math.Clamp(_settings.CascadeCount, 1, MaxCascades);
        int resolution = Math.Max(_settings.CascadeResolution, 1);
        Result<GpuBuffer> views = _gpu.GetBuffer(new("Cascade views", MaxCascades * GpuShadowView.Bytes), frame.View);
        if (!views.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Shadows are not drawn", views.Error);

            return null;
        }

        Span<GpuShadowView> rows = graph.Upload<GpuShadowView>(views.Value, count);
        if (rows.Length < count)
            return null;

        FitCascades(input.View, resolution, rows);

        return new CascadeViews(views.Value, count, resolution * 2);
    }

    /// <summary>
    /// Shadows each pixel of the view of this turn from its cascades' depth, and writes the mask's ids into the frame's
    /// buffer.
    /// </summary>
    /// <returns>
    /// The view's mask, or none while the shader builds or what it writes is not made.
    /// </returns>
    public ShadowMask? Render(in RenderFrame frame, RenderGraph graph, in SunShadowInput input)
    {
        GpuState state = _gpu.GetState(_resolveShader, _blockerMipShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Shadows are not resolved", "a shader of the sun's shadow was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        GpuTexture atlas = input.Depth.Atlas;
        int mipSize = Math.Max(input.Cascades.AtlasSize / 2, 1);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Sun shadow constants", Unsafe.SizeOf<SunShadowConstants>()),
            frame.View
        );
        Result<GpuBuffer> mask = _gpu.GetBuffer(
            new("Shadow mask", (long)view.Width * view.Height * BytesPerPixel),
            frame.View
        );
        Result<GpuTexture> minMips = _gpu.GetTexture(
            new("Cascade min mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels),
            frame.View
        );
        Result<GpuTexture> maxMips = _gpu.GetTexture(
            new("Cascade max mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels),
            frame.View
        );
        Result<GpuTexture> details = _gpu.GetTexture(
            new("Sun shadow details", view.Width, view.Height, GpuFormat.R8G8B8A8Unorm),
            frame.View
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(
            new SamplerDescription { Filter = TextureFilter.Nearest, Wrap = TextureWrap.Clamp }
        );
        Result asked = Result.FindFailure(constants, mask, minMips, maxMips, details, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Shadows are not resolved", asked.Error);

            return null;
        }

        SunShadowConstants resolve = new()
        {
            Views = input.Cascades.Views.Id,
            ViewCount = (uint)input.Cascades.Count,
            Atlas = atlas.Id,
            Sampler = sampler.Value.Id,
            MinMips = minMips.Value.Id,
            MaxMips = maxMips.Value.Id,
            MipLevels = BlockerMipLevels,
            Mask = mask.Value.Id,
            Details = details.Value.Id,
            NormalBias = _settings.NormalBias,
            CascadeBlend = Math.Clamp(_settings.CascadeBlend, 0, 0.5f),
            BlockerTaps = (uint)Math.Clamp(_settings.BlockerTaps, 1, 64),
            FilterTaps = (uint)Math.Clamp(_settings.FilterTaps, 1, 64),
            MaxPenumbra = Math.Clamp(_settings.MaxPenumbraTexels, 1, ShadowMask.WidestPenumbra),
            ContactLength = Math.Max(_settings.ContactShadowLength, 0),
            ContactSteps = _settings.IsContactShadowsOn
                ? (uint)Math.Clamp(_settings.ContactShadowSteps, 1, MostContactSteps)
                : 0,
            ContactThickness = Math.Max(_settings.ContactShadowThickness, 0),
        };
        // Not staged: nothing is recorded, and the frame's row stays the view's default, which the shaders read as none.
        GpuShadows row = new()
        {
            Mask = mask.Value.Id,
            CascadeAtlas = atlas.Id,
            SunDetails = details.Value.Id,
        };
        if (!graph.Upload(constants.Value, [resolve]) || !graph.Upload(view.Frame, [row], GpuShadows.Offset))
            return null;

        // The min and max mips of the atlas, a level a pass, for the blocker search where it reads wide.
        for (int level = 0; level < BlockerMipLevels; level++)
        {
            ComputePass build = graph.Compute(
                _blockerMipPasses[level],
                _blockerMipShader,
                new GpuConstants(atlas.Id, (uint)level, minMips.Value.Id, maxMips.Value.Id),
                RenderStages.Geometry
            );
            if (level == 0)
                build.Reads(atlas);
            else
                build.Reads(minMips.Value, level - 1).Reads(maxMips.Value, level - 1);

            build
                .Writes(minMips.Value, level)
                .Writes(maxMips.Value, level)
                .DispatchOver(Math.Max(mipSize >> level, 1), Math.Max(mipSize >> level, 1));
        }

        graph
            .Compute(
                "Resolve sun shadow",
                _resolveShader,
                new GpuConstants(view.Frame.Id, constants.Value.Id),
                RenderStages.Light
            )
            .Reads(
                view.Frame,
                constants.Value,
                input.Cascades.Views,
                atlas,
                minMips.Value,
                maxMips.Value,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface
            )
            .Writes(mask.Value, details.Value)
            .DispatchOver(view.Width, view.Height);

        return new ShadowMask(mask.Value, GpuTexture.None, atlas, details.Value);
    }

    /// <summary>
    /// The depth of split <paramref name="index"/> of <paramref name="count"/> from the near to the far end: the mix
    /// of the logarithmic and the even split by <paramref name="lambda"/>.
    /// </summary>
    private static float GetSplit(int index, int count, float near, float far, float lambda)
    {
        float part = (float)index / count;
        float logarithmic = near * MathF.Pow(far / near, part);
        float even = near + ((far - near) * part);

        return (lambda * logarithmic) + ((1 - lambda) * even);
    }

    private static double Dot(Vector3 a, Vector3 b) => ((double)a.X * b.X) + ((double)a.Y * b.Y) + ((double)a.Z * b.Z);

    /// <summary>
    /// The plane scaled so its normal is a unit.
    /// </summary>
    private static Vector4 ToPlane(Vector4 plane) => plane / new Vector3(plane.X, plane.Y, plane.Z).Length();

    /// <summary>
    /// Writes a row into <paramref name="rows"/> for each cascade of <paramref name="view"/>, nearest first, each in its
    /// square of the atlas of two by two of <paramref name="resolution"/> texels.
    /// </summary>
    /// <remarks>
    /// The splits mix even and logarithmic ones by the settings' lambda (Zhang and others, "Parallel-Split Shadow Maps on
    /// Programmable GPUs", GPU Gems 3, ch. 10.2.1). Each cascade is the square around the sphere that holds its slice
    /// of the view, so its size does not change as the camera turns (Valient, "Stable Rendering of Cascaded Shadow
    /// Maps", ShaderX6, 2008), and its centre moves in whole texels across the sun's view.
    /// </remarks>
    private void FitCascades(in GpuView view, int resolution, Span<GpuShadowView> rows)
    {
        FrameConstants camera = view.Constants;
        float near = camera.Near;
        float far = Math.Max(_settings.MaxShadowDistance, near * 2);
        float lambda = Math.Clamp(_settings.SplitLambda, 0, 1);

        // How far the view's corners spread at a metre's depth, squared: the projection's x and y focal lengths.
        float spread =
            (1 / (camera.Projection.M11 * camera.Projection.M11))
            + (1 / (camera.Projection.M22 * camera.Projection.M22));
        Matrix4x4.Invert(camera.View, out Matrix4x4 viewToWorld);
        Vector3 cameraForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, viewToWorld));
        Vector3 cameraPosition = new(camera.CameraPosition.X, camera.CameraPosition.Y, camera.CameraPosition.Z);

        // The sun's view: it looks along the light, with the frame's sides as the camera's are, so a triangle keeps its
        // winding.
        Vector3 towards = view.SunDirection;
        Vector3 forward = -towards;
        Vector3 up0 = MathF.Abs(forward.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up0, forward));
        Vector3 up = Vector3.Cross(forward, right);

        int atlas = resolution * 2;
        for (int i = 0; i < rows.Length; i++)
        {
            float start = GetSplit(i, rows.Length, near, far, lambda);
            float end = GetSplit(i + 1, rows.Length, near, far, lambda);

            // The smallest sphere around a slice of the view lies on its axis, as far from its near corners as from its
            // far ones, or at its far end's centre when that is further out.
            float along = Math.Min((end + start) * (1 + spread) / 2, end);
            float nearReach = ((along - start) * (along - start)) + (start * start * spread);
            float farReach = ((end - along) * (end - along)) + (end * end * spread);
            float radius = MathF.Ceiling(MathF.Sqrt(Math.Max(nearReach, farReach)) / RadiusStep) * RadiusStep;
            float texelSize = 2 * radius / resolution;

            // The centre in whole texels across the sun's view, from the world's origin, in doubles so a camera far
            // from the origin keeps the snap.
            Vector3 centerRelative = cameraForward * along;
            double centerX = Dot(cameraPosition, right) + Vector3.Dot(centerRelative, right);
            double centerY = Dot(cameraPosition, up) + Vector3.Dot(centerRelative, up);
            double snappedX = Math.Floor(centerX / texelSize) * texelSize;
            double snappedY = Math.Floor(centerY / texelSize) * texelSize;
            Vector3 snapped =
                centerRelative + (right * (float)(snappedX - centerX)) + (up * (float)(snappedY - centerY));

            // Camera-relative to the cascade's clip space: across and up over the radius, and reversed depth from the
            // near plane towards the sun to the far side of the sphere.
            float depthRange = CasterReach + radius;
            Matrix4x4 viewProjection = new(
                right.X / radius,
                up.X / radius,
                -forward.X / depthRange,
                0,
                right.Y / radius,
                up.Y / radius,
                -forward.Y / depthRange,
                0,
                right.Z / radius,
                up.Z / radius,
                -forward.Z / depthRange,
                0,
                -Vector3.Dot(snapped, right) / radius,
                -Vector3.Dot(snapped, up) / radius,
                (radius + Vector3.Dot(snapped, forward)) / depthRange,
                1
            );

            // The planes from the columns, for row vectors. The near plane never rejects: a caster nearer the sun is
            // drawn at it.
            Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
            Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
            Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
            Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
            uint x = (uint)(i % 2 * resolution);
            uint y = (uint)(i / 2 * resolution);
            rows[i] = new GpuShadowView
            {
                ViewProjection = viewProjection,
                Plane0 = ToPlane(c3 + c0),
                Plane1 = ToPlane(c3 - c0),
                Plane2 = ToPlane(c3 + c1),
                Plane3 = ToPlane(c3 - c1),
                Plane4 = new Vector4(0, 0, 0, 1),
                Plane5 = ToPlane(c2),
                RectX = x,
                RectY = y,
                RectWidth = (uint)resolution,
                RectHeight = (uint)resolution,
                AtlasTransform = new Vector4(
                    (float)resolution / atlas,
                    (float)resolution / atlas,
                    (((2f * x) + resolution) / atlas) - 1,
                    1 - (((2f * y) + resolution) / atlas)
                ),
                Towards = towards,
                TexelSize = texelSize,
                IsOrthographic = 1,
                DepthRange = depthRange,
            };
        }
    }
}

/// <summary>
/// What the sun's resolve reads beyond the frame, 64 bytes, the twin of <c>ResolveSunShadow.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct SunShadowConstants
{
    public uint Views;

    public uint ViewCount;

    public uint Atlas;

    public uint Sampler;

    public uint MinMips;

    public uint MaxMips;

    public uint MipLevels;

    public uint Mask;

    public uint Details;

    public float NormalBias;

    public float CascadeBlend;

    public uint BlockerTaps;

    public uint FilterTaps;

    public float MaxPenumbra;

    public float ContactLength;

    /// <summary>
    /// The steps of the contact shadow's march, 0 for none.
    /// </summary>
    public uint ContactSteps;

    public float ContactThickness;
}
