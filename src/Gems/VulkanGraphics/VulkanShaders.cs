using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.VulkanGraphics;

/// <summary>
/// A shader as the provider holds it: where its build is, and a compute shader's pipeline and the threads a group of it
/// runs.
/// </summary>
internal sealed class VulkanShader
{
    public GpuState State { get; set; }

    /// <summary>
    /// A vertex or pixel shader's compiled code, which draw pipelines are built from.
    /// </summary>
    public CompiledShader? Compiled { get; set; }

    public GpuPipeline Pipeline { get; set; }

    public (uint X, uint Y, uint Z) GroupSize { get; set; }
}

/// <summary>
/// The shaders the graph's passes name, each compiled for Vulkan on its first ask, a compute shader then built into its
/// pipeline; and the draw pipelines, each built on the first draw with its shaders into targets of its formats. Render
/// thread, but the compile on its worker.
/// </summary>
internal sealed class VulkanShaders(IShaderCompiler compiler, VulkanResources resources, Threads threads) : IDisposable
{
    private readonly Dictionary<ShaderEntry, VulkanShader> _shaders = [];

    /// <summary>
    /// The draw pipelines, by their shaders and the formats of the targets they draw into, a colour target's a byte
    /// each; none for one that did not build.
    /// </summary>
    private readonly Dictionary<(DrawShaders Shaders, ulong Colors, GpuFormat Depth), GpuPipeline> _drawPipelines = [];

    /// <summary>
    /// Ends the compiles still running when the provider goes, so nothing is built for a device that is gone.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    public GpuState GetState(in ShaderEntry entry) => GetOrStart(entry).State;

    /// <summary>
    /// The shader once it is built; null while it compiles or after it failed.
    /// </summary>
    public VulkanShader? Get(in ShaderEntry entry) =>
        GetOrStart(entry) is { State: GpuState.Ready } shader ? shader : null;

    /// <summary>
    /// The pipeline <paramref name="shaders"/> draw into <paramref name="targets"/> with, built on the first ask once
    /// both are compiled; none until then, and after a build that failed.
    /// </summary>
    public GpuPipeline Get(in DrawShaders shaders, in GpuRenderTargets targets)
    {
        if (Get(shaders.Vertex)?.Compiled is not { } vertex || Get(shaders.Pixel)?.Compiled is not { } pixel)
            return GpuPipeline.None;

        ulong colors = 0;
        for (int i = 0; i < targets.Colors.Length; i++)
        {
            colors |= (ulong)GetFormat(targets.Colors[i]) << (8 * i);
        }

        GpuFormat depth = targets.Depth is { } target ? GetFormat(target) : GpuFormat.Unknown;
        if (_drawPipelines.TryGetValue((shaders, colors, depth), out GpuPipeline built))
            return built;

        ImmutableArray<GpuFormat>.Builder formats = ImmutableArray.CreateBuilder<GpuFormat>(targets.Colors.Length);
        foreach (GpuTarget color in targets.Colors)
        {
            formats.Add(GetFormat(color));
        }

        Result<GpuPipeline> made = resources.CreatePipeline(
            new GraphicsPipelineDescription
            {
                VertexSpirv = vertex.Spirv,
                VertexEntryPoint = vertex.EntryPoint,
                PixelSpirv = pixel.Spirv,
                PixelEntryPoint = pixel.EntryPoint,
                ColorFormats = formats.MoveToImmutable(),
                DepthFormat = depth,
                Depth = shaders.Depth,
                Blend = shaders.Blend,
                Cull = shaders.Cull,
                Name = shaders.Pixel.Name,
            }
        );
        if (!made.Ok)
            Log.Error($"The passes that draw with {shaders.Pixel.Name} are left out: {made.Error}");

        GpuPipeline pipeline = made.Ok ? made.Value : GpuPipeline.None;
        _drawPipelines[(shaders, colors, depth)] = pipeline;

        return pipeline;
    }

    private GpuFormat GetFormat(in GpuTarget target) =>
        resources.Textures[target.Texture.Id]?.Description.Format ?? GpuFormat.Unknown;

    private VulkanShader GetOrStart(in ShaderEntry entry)
    {
        if (_shaders.TryGetValue(entry, out VulkanShader? shader))
            return shader;

        shader = new VulkanShader();
        _shaders.Add(entry, shader);
        CompileAsync(entry, shader).FireAndForget();

        return shader;
    }

    private async Task CompileAsync(ShaderEntry entry, VulkanShader shader)
    {
        CancellationToken stopping = _stopping.Token;

        // On a worker from the start: a compile runs inline up to its first wait, and its first ask is on the render
        // thread.
        Result<CompiledShader> compiled = await Task.Run(() => compiler.CompileAsync(entry, stopping), stopping)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        if (stopping.IsCancellationRequested)
            return;

        Result built = compiled.Ok ? Build(entry, compiled.Value, shader) : Result.Failure(compiled.Error);
        if (built.Ok)
            return;

        Log.Error($"The passes that run {entry.Name} are left out: {built.Error}");
        shader.State = GpuState.Failed;
    }

    /// <summary>
    /// <paramref name="compiled"/> put in <paramref name="shader"/>, with a compute shader's pipeline and group size.
    /// </summary>
    private Result Build(in ShaderEntry entry, CompiledShader compiled, VulkanShader shader)
    {
        if (entry.Stage != ShaderStage.Compute)
        {
            shader.Compiled = compiled;
            shader.State = GpuState.Ready;

            return Result.Success();
        }

        if (compiled.GroupSizeX == 0)
            return Result.Failure("it reported no group size.");

        Result<GpuPipeline> pipeline = resources.CreatePipeline(
            new ComputePipelineDescription
            {
                Spirv = compiled.Spirv,
                EntryPoint = compiled.EntryPoint,
                Name = entry.Name,
            }
        );
        if (!pipeline.Ok)
            return Result.Failure(pipeline.Error);

        shader.Pipeline = pipeline.Value;
        shader.GroupSize = (compiled.GroupSizeX, compiled.GroupSizeY, compiled.GroupSizeZ);
        shader.State = GpuState.Ready;

        return Result.Success();
    }
}
