using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The sky's passes (<see cref="IRendering.CaptureSky"/>, <see cref="IRendering.DrawSky"/>), each composed with the
/// world's sky: its light captured each frame into the renderer's own textures, kept from frame to frame, the DFG table
/// every shading reads, the sky as a cube and its levels, its light from everywhere as harmonics, and its reflections
/// for each roughness; and the sky drawn over what nothing was drawn on. Render thread.
/// </summary>
internal sealed class SkyPasses : IDisposable
{
    private const GpuTextureUsage Target = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled;
    private const GpuTextureUsage Computed = GpuTextureUsage.ComputeWrite | GpuTextureUsage.Sampled;

    // The sky's reflections' levels, roughness 0 to 1, and the samples of the lobe each texel takes.
    private const uint SpecularLevels = 6;
    private const uint SpecularSamples = 48;

    private readonly IGpuDriver _gpu;
    private readonly Passes _passes;
    private readonly GpuTexture _dfg;
    private readonly GpuTexture _cube;
    private readonly GpuTexture _specular;
    private readonly GpuSampler _sampler;
    private readonly Handle<Shader> _makeDfg;
    private readonly Handle<Shader> _capture;
    private readonly Handle<Shader> _project;
    private readonly Handle<Shader> _prefilter;
    private readonly Handle<Shader> _draw;

    internal SkyPasses(IGpuDriver gpu, Passes passes, Assets assets)
    {
        _gpu = gpu;
        _passes = passes;
        _dfg = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.Rgba16Float, Computed, 32, 32));
        _cube = gpu.CreateTexture(
            new GpuTextureDescription(GpuFormat.Rgba16Float, Target, 128, 128, Levels: 8, Kind: GpuTextureKind.Cube));
        _specular = gpu.CreateTexture(new GpuTextureDescription(
            GpuFormat.Rgba16Float,
            Computed,
            128,
            128,
            Levels: SpecularLevels,
            Kind: GpuTextureKind.Cube));
        Sh = gpu.CreateBuffer(
            GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite,
            9 * 16);
        _sampler = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
        _makeDfg = assets.Find<Shader>("Shaders/Rendering/Sky/Dfg.comp.hlsl");
        _capture = assets.Find<Shader>("Shaders/Rendering/Sky/Capture.frag.hlsl");
        _project = assets.Find<Shader>("Shaders/Rendering/Sky/Sh.comp.hlsl");
        _prefilter = assets.Find<Shader>("Shaders/Rendering/Sky/Prefilter.comp.hlsl");
        _draw = assets.Find<Shader>("Shaders/Rendering/Sky/Sky.frag.hlsl");
    }

    /// <summary>
    /// The sky's light from everywhere as nine harmonics, a float4 each, as last captured.
    /// </summary>
    internal GpuBuffer Sh { get; }

    public void Dispose()
    {
        _gpu.Release(_dfg);
        _gpu.Release(_cube);
        _gpu.Release(_specular);
        _gpu.Release(Sh);
        _gpu.Release(_sampler);
    }

    internal SkyLight Capture(RenderView seenFrom)
    {
        _passes.Dispatch(_makeDfg, new DfgConstants(_dfg), new GpuGroups(4, 4), [], [_dfg]);
        for (uint face = 0; face < 6; face++)
        {
            _passes.Fullscreen(
                _capture,
                new CaptureConstants(seenFrom.Constants, face),
                new GpuTextureLevel(_cube, 0, face),
                GpuContents.Keep,
                []);
        }

        _gpu.Commands.GenerateMipmaps(_cube);
        _passes.Dispatch(_project, new ShConstants(_cube, _sampler, Sh), new GpuGroups(1), [_cube], [Sh]);
        _passes.Dispatch(
            _prefilter,
            new PrefilterConstants(_cube, _sampler, _specular, SpecularLevels, SpecularSamples),
            new GpuGroups(16, 16, 6 * SpecularLevels),
            [_cube],
            [_specular]);

        return new SkyLight(_dfg, Sh, _specular, _sampler);
    }

    internal void Draw(RenderView view, GpuTexture depth, GpuTexture target)
        => _passes.Fullscreen(
            _draw,
            new SkyConstants(view.Constants, depth, view.Exposure),
            new GpuTextureLevel(target),
            GpuContents.Keep,
            [depth, view.Exposure]);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DfgConstants(GpuTexture Dfg);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CaptureConstants(GpuBuffer View, uint Face);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ShConstants(GpuTexture Cube, GpuSampler Sampler, GpuBuffer Sh);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PrefilterConstants(
        GpuTexture Cube,
        GpuSampler Sampler,
        GpuTexture Specular,
        uint Levels,
        uint Samples);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SkyConstants(GpuBuffer View, GpuTexture Depth, GpuBuffer Exposure);
}
