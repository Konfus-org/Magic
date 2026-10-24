using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The sky's passes (<see cref="IRendering.CaptureSky"/>, <see cref="IRendering.DrawSky"/>), those that draw the sky
/// specialized with the world's sky (Shaders/Renderer/Sky.slang): its light captured each frame into the renderer's own
/// textures, kept from frame to frame, the sky as a cube and its levels, its light from everywhere as harmonics, and
/// its reflections for each roughness, beside the DFG table every shading reads, written once; and the sky drawn over
/// what nothing was drawn on. Render thread.
/// </summary>
internal sealed class SkyPasses : IDisposable
{
    private const GpuTextureUsage Computed = GpuTextureUsage.ComputeWrite | GpuTextureUsage.Sampled;

    /// <summary>
    /// The renderer's own sky, which the passes that draw the sky are specialized with for a world without one.
    /// </summary>
    private const string FallbackSky = "FallbackSky";

    // The sky's reflections' levels, roughness 0 to 1, and the samples of the lobe each texel takes.
    private const uint SpecularLevels = 6;
    private const uint SpecularSamples = 48;

    private readonly IGpuDriver _gpu;
    private readonly Passes _passes;
    private readonly GpuPipelines _pipelines;
    private readonly Assets _assets;
    private readonly GpuTexture _dfg;
    private readonly GpuTexture _cube;
    private readonly GpuTexture _specular;
    private readonly GpuSampler _sampler;
    private readonly ShaderEntry _writeDfgTable;
    private readonly ShaderEntry _filterSkyLight;
    private ShaderEntry _captureSky;
    private ShaderEntry _drawSky;

    /// <summary>
    /// The pipeline the DFG table was last written with, which writes it again once it is made again.
    /// </summary>
    private GpuPipeline _dfgWrittenWith;

    internal SkyPasses(IGpuDriver gpu, Passes passes, GpuPipelines pipelines, Assets assets)
    {
        _gpu = gpu;
        _passes = passes;
        _pipelines = pipelines;
        _assets = assets;
        _dfg = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.Rgba16Float, Computed, 32, 32));
        _cube = gpu.CreateTexture(
            new GpuTextureDescription(GpuFormat.Rgba16Float, Computed, 128, 128, Levels: 8, Kind: GpuTextureKind.Cube));
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
        Handle<Shader> module = assets.Find<Shader>("Shaders/Renderer/Sky.slang");
        ShaderType[] fallback = [new ShaderType(Handle<Shader>.None, FallbackSky)];
        _writeDfgTable = new ShaderEntry(module, "WriteDfgTable");
        _filterSkyLight = new ShaderEntry(module, "FilterSkyLight");
        _captureSky = new ShaderEntry(module, "CaptureSky") { Types = fallback };
        _drawSky = new ShaderEntry(module, "DrawSky") { Types = fallback };
    }

    /// <summary>
    /// The sky's light from everywhere as nine harmonics, a float4 each, as last captured.
    /// </summary>
    internal GpuBuffer Sh { get; }

    /// <summary>
    /// The world's sky's shader, which the passes that draw the sky are specialized with, or none for the renderer's
    /// own: this frame's.
    /// </summary>
    internal Handle<Shader> Sky
    {
        get;
        set
        {
            if (value == field) return;

            field = value;
            ShaderType[] sky =
            [
                value == Handle<Shader>.None
                    ? new ShaderType(Handle<Shader>.None, FallbackSky)
                    : ShaderType.Of(value, _assets.GetPath(value) ?? string.Empty),
            ];
            _captureSky = _captureSky with { Types = sky };
            _drawSky = _drawSky with { Types = sky };
        }
    }

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
        GpuPipeline dfg = _pipelines.GetCompute(_writeDfgTable);
        if (dfg != _dfgWrittenWith && dfg != GpuPipeline.None)
        {
            _passes.Dispatch(seenFrom, _writeDfgTable, new DfgConstants(_dfg), new GpuGroups(4, 4), [], [_dfg]);
            _dfgWrittenWith = dfg;
        }

        _passes.Dispatch(seenFrom, _captureSky, new CaptureConstants(_cube), new GpuGroups(16, 16, 6), [], [_cube]);
        _gpu.Commands.GenerateMipmaps(_cube);
        _passes.Dispatch(
            seenFrom,
            _filterSkyLight,
            new FilterConstants(_cube, _sampler, _specular, Sh, SpecularLevels, SpecularSamples),
            new GpuGroups(16, 16, (6 * SpecularLevels) + 1),
            [_cube],
            [_specular, Sh]);

        return new SkyLight(_dfg, Sh, _specular, _sampler);
    }

    internal void Draw(RenderView view, GpuTexture depth, GpuTexture target)
        => _passes.Dispatch(
            view,
            _drawSky,
            new SkyConstants(depth, target),
            GpuGroups.For(view.Size, 8),
            [depth, view.Exposure],
            [target]);

    [ShaderStruct("Shaders/Renderer/Sky.slang", "DfgConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DfgConstants(GpuTexture Dfg);

    [ShaderStruct("Shaders/Renderer/Sky.slang", "CaptureConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CaptureConstants(GpuTexture Cube);

    [ShaderStruct("Shaders/Renderer/Sky.slang", "FilterConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FilterConstants(
        GpuTexture Cube,
        GpuSampler Sampler,
        GpuTexture Specular,
        GpuBuffer Sh,
        uint Levels,
        uint Samples);

    [ShaderStruct("Shaders/Renderer/Sky.slang", "SkyConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SkyConstants(GpuTexture Depth, GpuTexture Target);
}
