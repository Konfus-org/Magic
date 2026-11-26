using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.AmbientOcclusion;

/// <summary>
/// What the ambient occlusion takes for each view of the frame: the view as the GPU holds it.
/// </summary>
public readonly record struct AmbientOcclusionInput(GpuView View);

/// <summary>
/// Finds how much of the ambient light reaches each pixel of each view, and the mean direction it comes from, into
/// <see cref="DeferredRendering.AmbientOcclusion"/>, with ground-truth ambient occlusion ported from XeGTAO.
/// </summary>
/// <remarks>
/// Three passes in the light stage: the view depth in 5 levels, the visibility along XeGTAO's slices with the edges
/// between pixels, and one blur that keeps to the edges (Jimenez et al., "Practical Real-Time Strategies for Accurate
/// Indirect Occlusion", 2016). The noise turns each frame, for the TAA to blend.
/// </remarks>
public sealed class Renderer : IRender<AmbientOcclusionInput, DeferredRendering.AmbientOcclusion>
{
    /// <summary>
    /// XeGTAO's multiplier of the radius, against the biases of the screen.
    /// </summary>
    private const float RadiusMultiplier = 1.457f;

    /// <summary>
    /// The largest radius: times <see cref="RadiusMultiplier"/>, 1 m, the farthest that a screen-space term may reach.
    /// </summary>
    private const float MaxRadius = 0.68f;

    private const int DepthMipLevels = 5;

    /// <summary>
    /// The share of the picture's width and height at each side over which the occlusion fades to none.
    /// </summary>
    private const float EdgeFade = 0.05f;

    private readonly AmbientOcclusionSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _prefilterShader;

    private readonly ShaderEntry _mainShader;

    private readonly ShaderEntry _denoiseShader;

    internal Renderer(AmbientOcclusionSettings settings, AssetManager assets, IGraphicsApi gpu)
    {
        _settings = settings;
        _gpu = gpu;
        _prefilterShader = PassShaders.GetEntry(assets, "Gtao", "PrefilterDepths");
        _mainShader = PassShaders.GetEntry(assets, "Gtao", "GtaoMainPass");
        _denoiseShader = PassShaders.GetEntry(assets, "Gtao", "GtaoDenoise");
    }

    /// <summary>
    /// Records the view's ambient occlusion.
    /// </summary>
    /// <returns>
    /// The view's occlusion and bent normal, or none while it is off, its shaders build, or what it writes is not made.
    /// </returns>
    public DeferredRendering.AmbientOcclusion? Render(
        in RenderFrame frame,
        RenderGraph graph,
        in AmbientOcclusionInput input
    )
    {
        if (!_settings.IsAmbientOcclusionOn)
            return null;

        GpuState state = _gpu.GetState(_prefilterShader, _mainShader, _denoiseShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Ambient occlusion is not found", "a shader of it was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        int width = view.Width;
        int height = view.Height;
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Ambient occlusion constants", Unsafe.SizeOf<GtaoConstants>()),
            frame.View
        );
        Result<GpuTexture> viewDepths = _gpu.GetTexture(
            new("Ambient occlusion depths", width, height, GpuFormat.R32Sfloat, Levels: DepthMipLevels),
            frame.View
        );
        Result<GpuTexture> working = _gpu.GetTexture(
            new("Ambient occlusion working", width, height, GpuFormat.R8G8B8A8Unorm),
            frame.View
        );
        Result<GpuTexture> edges = _gpu.GetTexture(
            new("Ambient occlusion edges", width, height, GpuFormat.R8Unorm),
            frame.View
        );
        Result<GpuTexture> occlusion = _gpu.GetTexture(
            new("Ambient occlusion", width, height, GpuFormat.R8Unorm),
            frame.View
        );
        Result<GpuTexture> bentNormal = _gpu.GetTexture(
            new("Bent normal", width, height, GpuFormat.R8G8B8A8Unorm),
            frame.View
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(
            new SamplerDescription
            {
                Filter = TextureFilter.Nearest,
                MipFilter = TextureFilter.Nearest,
                Wrap = TextureWrap.Clamp,
            }
        );
        Result asked = Result.FindFailure(constants, viewDepths, working, edges, occlusion, bentNormal, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Ambient occlusion is not found", asked.Error);

            return null;
        }

        (int slices, int steps) = _settings.Quality switch
        {
            AmbientOcclusionQuality.Low => (1, 2),
            AmbientOcclusionQuality.Medium => (2, 2),
            AmbientOcclusionQuality.High => (3, 3),
            AmbientOcclusionQuality.Ultra => (9, 3),
            _ => (3, 3),
        };

        // From texture coordinates to the view direction at a depth of 1, through the jittered projection: x right, y
        // up, z forward, as XeGTAO's view space is.
        Matrix4x4 projection = view.Constants.Projection;
        Vector2 pixelSize = new(1f / width, 1f / height);
        Vector2 ndcToViewMul = new(2 / projection.M11, -2 / projection.M22);
        GtaoConstants gtao = new()
        {
            ViewDepths = viewDepths.Value.Id,
            Working = working.Value.Id,
            Edges = edges.Value.Id,
            Occlusion = occlusion.Value.Id,
            BentNormal = _settings.IsBentNormalOn ? bentNormal.Value.Id : 0,
            Sampler = sampler.Value.Id,
            IsBentNormalOn = _settings.IsBentNormalOn ? 1u : 0u,
            NoiseIndex = view.Constants.FrameIndex % 64,
            ViewportPixelSize = pixelSize,
            NdcToViewMul = ndcToViewMul,
            NdcToViewAdd = new((-1 - projection.M31) / projection.M11, (1 - projection.M32) / projection.M22),
            NdcToViewMulPixel = ndcToViewMul * pixelSize,
            EffectRadius = Math.Clamp(_settings.Radius, 0, MaxRadius) * RadiusMultiplier,
            FalloffRange = 0.615f,
            FinalValuePower = 2.2f,
            DenoiseBlurBeta = 1.2f,
            DepthMipSamplingOffset = 3.3f,
            SliceCount = slices,
            StepsPerSlice = steps,
            EdgeFade = EdgeFade,
        };
        if (!graph.Upload(constants.Value, [gtao]))
            return null;

        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        graph
            .Compute("GTAO prefilter depths", _prefilterShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, view.Depth)
            .Writes(viewDepths.Value)
            .DispatchOver((width + 1) / 2, (height + 1) / 2);
        graph
            .Compute("GTAO main pass", _mainShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, view.Depth, view.GBufferNormal, viewDepths.Value)
            .Writes(working.Value, edges.Value)
            .DispatchOver(width, height);
        graph
            .Compute("GTAO denoise", _denoiseShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, working.Value, edges.Value)
            .Writes(occlusion.Value, bentNormal.Value)
            .DispatchOver(width, height);

        return new DeferredRendering.AmbientOcclusion(
            occlusion.Value,
            _settings.IsBentNormalOn ? bentNormal.Value : GpuTexture.None
        );
    }
}

/// <summary>
/// What the passes read beyond the frame, 96 bytes, the twin of <c>Gtao.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GtaoConstants
{
    public uint ViewDepths;

    public uint Working;

    public uint Edges;

    public uint Occlusion;

    public uint BentNormal;

    public uint Sampler;

    public uint IsBentNormalOn;

    public uint NoiseIndex;

    public Vector2 ViewportPixelSize;

    public Vector2 NdcToViewMul;

    public Vector2 NdcToViewAdd;

    public Vector2 NdcToViewMulPixel;

    public float EffectRadius;

    public float FalloffRange;

    public float FinalValuePower;

    public float DenoiseBlurBeta;

    public float DepthMipSamplingOffset;

    public float SliceCount;

    public float StepsPerSlice;

    public float EdgeFade;
}
