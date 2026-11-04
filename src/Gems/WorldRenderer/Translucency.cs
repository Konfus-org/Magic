using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// What the translucent clusters leave for the frame: the sun's glass shadow, their tints multiplied in the sun's
/// view; the moments of their depths and their light, drawn after the lighting over the opaque depth, that the
/// tonemap composes over the picture; and the list of translucent instances the rays cross.
/// </summary>
internal sealed class Translucency(IGraphicsApi gpu) : IDisposable
{
    public const int GlassMax = 64;

    private const int ShadowTexels = 1024;
    private const float ShadowHalfExtent = 32;
    private const float MomentsNear = 0.1f;
    private const float MomentsFar = 500;

    private readonly GpuGlass[] _glass = new GpuGlass[GlassMax];
    private GpuTexture _glassShadow;
    private GpuBuffer _glassList;
    private GpuSampler _sampler;
    private int _glassCount;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Constants, _glassList])
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        if (!_glassShadow.IsNone)
            gpu.Destroy(_glassShadow);

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);
    }

    public Result Create()
    {
        Result<GpuBuffer> constants = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = TranslucencyConstants.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Translucency constants",
            }
        );
        Result<GpuBuffer> list = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)GlassMax * GpuGlass.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Glass list",
            }
        );
        if (!constants.Ok || !list.Ok)
            return Result.Failure($"a translucency buffer was not made: {constants.Error}{list.Error}");

        (Constants, _glassList) = (constants.Value, list.Value);
        Result<GpuTexture> shadow = gpu.CreateTexture(
            new TextureDescription
            {
                Width = ShadowTexels,
                Height = ShadowTexels,
                Format = GpuFormat.R16G16B16A16Sfloat,
                Usage = TextureUsages.ColorTarget | TextureUsages.Sampled,
                Name = "Glass shadow",
            }
        );
        if (!shadow.Ok)
            return Result.Failure($"the glass shadow was not made: {shadow.Error}");

        _glassShadow = shadow.Value;
        Result<GpuSampler> sampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
        );
        if (!sampler.Ok)
            return Result.Failure($"the glass shadow sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// A translucent instance for the frame's list, until it is full: what the rays cross, as a sphere with a tint.
    /// </summary>
    public void Add(Vector3 center, float radius, Vector3 tint)
    {
        if (_glassCount >= GlassMax)
            return;

        _glass[_glassCount++] = new GpuGlass
        {
            Center = center,
            Radius = radius,
            Tint = tint,
        };
    }

    /// <summary>
    /// The frame's constants and glass list uploaded, in a transfer pass the caller opened; the list starts over for
    /// the next frame's adds.
    /// </summary>
    public void Prepare(RenderGraph graph, Vector3 sunDirection, Vector3 cameraPosition, FrameTargets targets)
    {
        if (_glassCount > 0)
        {
            int bytes = _glassCount * GpuGlass.Bytes;
            GpuUpload list = gpu.BeginUpload(bytes);
            MemoryMarshal.AsBytes(_glass.AsSpan(0, _glassCount)).CopyTo(list.Bytes);
            graph.Use(_glassList, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(list.Buffer, list.Offset, _glassList, 0, (ulong)bytes);
        }

        // The sun's view: orthographic, centred on the camera, looking along the light.
        Vector3 forward = Vector3.Normalize(-sunDirection);
        Vector3 up = Math.Abs(forward.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Cross(forward, right);
        Matrix4x4 view = new(
            right.X,
            up.X,
            forward.X,
            0,
            right.Y,
            up.Y,
            forward.Y,
            0,
            right.Z,
            up.Z,
            forward.Z,
            0,
            -Vector3.Dot(right, cameraPosition),
            -Vector3.Dot(up, cameraPosition),
            -Vector3.Dot(forward, cameraPosition),
            1
        );
        Matrix4x4 projection = Matrix4x4.Identity;
        projection.M11 = 1 / ShadowHalfExtent;
        projection.M22 = 1 / ShadowHalfExtent;
        projection.M33 = 0;
        projection.M43 = 0.5f;
        TranslucencyConstants constants = new()
        {
            SunViewProjection = view * projection,
            Moments = targets.Moments.Id,
            OpticalDepth = targets.OpticalDepth.Id,
            Accumulation = targets.Accumulation.Id,
            Offsets = targets.Offsets.Id,
            GlassShadow = _glassShadow.Id,
            GlassList = _glassList.Id,
            GlassCount = (uint)_glassCount,
            Sampler = _sampler.Id,
            Near = MomentsNear,
            Far = MomentsFar,
            HasTranslucent = _glassCount > 0 ? 1u : 0u,
        };
        GpuUpload upload = gpu.BeginUpload(TranslucencyConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, TranslucencyConstants.Bytes);
        _glassCount = 0;
    }

    /// <summary>
    /// The glass shadow drawn, after the cull: what the lighting and the cards read the sun through.
    /// </summary>
    public void RecordShadow(RenderGraph graph, RendererPipelines pipelines, in DrawList draws)
    {
        graph.BeginDrawPass(
            "Glass shadow",
            new GpuRenderTargets
            {
                Colors =
                [
                    new GpuColorTarget
                    {
                        Texture = _glassShadow,
                        Load = TargetLoad.Clear,
                        ClearColor = GpuConstants.FromFloats(1, 1, 1, 1),
                    },
                ],
            }
        );
        UseDraws(graph, draws);
        graph.Recorder.BindPipeline(pipelines.GlassShadow);
        graph.Recorder.PushConstants(draws.Header);
        graph.Recorder.DrawIndirectCount(
            draws.Arguments,
            draws.ArgumentsOffset,
            draws.Counts,
            draws.CountOffset,
            draws.MaxDraws,
            draws.ArgumentsStride
        );
        graph.EndPass();
    }

    /// <summary>
    /// The translucent clusters drawn after the lighting: their moments, then their light, over the opaque depth.
    /// </summary>
    public void RecordDraws(
        RenderGraph graph,
        RendererPipelines pipelines,
        in DrawList draws,
        FrameTargets targets,
        SunShadows shadows,
        Lighting lighting,
        GlobalIllumination gi
    )
    {
        graph.BeginDrawPass(
            "Translucent: moments",
            new GpuRenderTargets
            {
                Colors =
                [
                    new GpuColorTarget { Texture = targets.OpticalDepth, Load = TargetLoad.Clear },
                    new GpuColorTarget { Texture = targets.Moments, Load = TargetLoad.Clear },
                ],
                Depth = new GpuDepthTarget { Texture = targets.Depth },
            }
        );
        UseDraws(graph, draws);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Recorder.BindPipeline(pipelines.TranslucentMoments);
        graph.Recorder.PushConstants(draws.Header);
        graph.Recorder.DrawIndirectCount(
            draws.Arguments,
            draws.ArgumentsOffset,
            draws.Counts,
            draws.CountOffset,
            draws.MaxDraws,
            draws.ArgumentsStride
        );
        graph.EndPass();

        graph.BeginDrawPass(
            "Translucent: resolve",
            new GpuRenderTargets
            {
                Colors =
                [
                    new GpuColorTarget { Texture = targets.Accumulation, Load = TargetLoad.Clear },
                    new GpuColorTarget { Texture = targets.Offsets, Load = TargetLoad.Clear },
                ],
                Depth = new GpuDepthTarget { Texture = targets.Depth },
            }
        );
        UseDraws(graph, draws);
        graph.Use(targets.OpticalDepth, GpuAccess.ShaderRead);
        graph.Use(targets.Moments, GpuAccess.ShaderRead);
        graph.Use(_glassShadow, GpuAccess.ShaderRead);
        shadows.UseForSampling(graph);
        lighting.UseForShading(graph);
        gi.UseForSampling(graph);
        graph.Recorder.BindPipeline(pipelines.TranslucentResolve);
        graph.Recorder.PushConstants(draws.Header);
        graph.Recorder.DrawIndirectCount(
            draws.Arguments,
            draws.ArgumentsOffset,
            draws.Counts,
            draws.CountOffset,
            draws.MaxDraws,
            draws.ArgumentsStride
        );
        graph.EndPass();
    }

    /// <summary>
    /// Says a pass reads the sun through the glass and the panes a ray crosses: what the lighting, the cards and
    /// the probes declare.
    /// </summary>
    public void UseForSampling(RenderGraph graph)
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_glassShadow, GpuAccess.ShaderRead);
        graph.Use(_glassList, GpuAccess.ShaderRead);
    }

    /// <summary>
    /// Says a pass composes the panes over the picture: what the tonemap declares.
    /// </summary>
    public void UseForComposite(RenderGraph graph, FrameTargets targets)
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(targets.OpticalDepth, GpuAccess.ShaderRead);
        graph.Use(targets.Accumulation, GpuAccess.ShaderRead);
        graph.Use(targets.Offsets, GpuAccess.ShaderRead);
    }

    private void UseDraws(RenderGraph graph, in DrawList draws)
    {
        graph.Use(draws.FrameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(draws.Instances, GpuAccess.ShaderRead);
        graph.Use(draws.Vertices, GpuAccess.ShaderRead);
        graph.Use(draws.Clusters, GpuAccess.ShaderRead);
        graph.Use(draws.Indices, GpuAccess.ShaderRead);
        graph.Use(draws.Materials, GpuAccess.ShaderRead);
        graph.Use(draws.VisibleClusters, GpuAccess.ShaderRead);
        graph.Use(draws.Arguments, GpuAccess.Indirect);
        graph.Use(draws.Counts, GpuAccess.Indirect);
    }
}

/// <summary>
/// The translucent draws the cull left and what they fetch: what a translucent pass is handed.
/// </summary>
internal readonly record struct DrawList(
    GpuConstants Header,
    GpuBuffer FrameConstants,
    GpuBuffer Instances,
    GpuBuffer Arguments,
    ulong ArgumentsOffset,
    GpuBuffer Counts,
    ulong CountOffset,
    uint MaxDraws,
    uint ArgumentsStride,
    GpuBuffer Vertices,
    GpuBuffer Indices,
    GpuBuffer Clusters,
    GpuBuffer Materials,
    GpuBuffer VisibleClusters
);

/// <summary>
/// What the translucent passes read, the twin of <c>Scene/Translucency.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TranslucencyConstants
{
    public const int Bytes = 112;

    public Matrix4x4 SunViewProjection;

    public uint Moments;

    public uint OpticalDepth;

    public uint Accumulation;

    public uint Offsets;

    public uint GlassShadow;

    public uint GlassList;

    public uint GlassCount;

    public uint Sampler;

    public float Near;

    public float Far;

    public uint HasTranslucent;

    public uint Padding;
}

/// <summary>
/// A translucent instance as a sphere with a tint, the twin of <c>Scene/Translucency.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuGlass
{
    public const int Bytes = 32;

    public Vector3 Center;

    public float Radius;

    public Vector3 Tint;

    public float Padding;
}
