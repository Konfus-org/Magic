using Magic.Debugging;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.VulkanGraphics;

/// <summary>
/// A compute shader as the provider holds it: where its build is, then its pipeline and the threads a group of it runs.
/// </summary>
internal sealed class VulkanShader
{
    public GpuState State { get; set; }

    public GpuPipeline Pipeline { get; set; }

    public (uint X, uint Y, uint Z) GroupSize { get; set; }
}

/// <summary>
/// The compute shaders the graph's passes name, each compiled for Vulkan and built into a pipeline on its first ask.
/// Render thread, but the compile on its worker.
/// </summary>
internal sealed class VulkanShaders(IShaderCompiler compiler, VulkanResources resources, Threads threads) : IDisposable
{
    private readonly Dictionary<ShaderEntry, VulkanShader> _shaders = [];

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
    /// The pipeline <paramref name="compiled"/> builds, put in <paramref name="shader"/> with its group size.
    /// </summary>
    private Result Build(in ShaderEntry entry, CompiledShader compiled, VulkanShader shader)
    {
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
