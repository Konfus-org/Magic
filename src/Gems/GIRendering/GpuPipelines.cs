using System.Collections.Immutable;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The library shaders a pass's entry is specialised with, in this order.
/// </summary>
[Flags]
internal enum PassShaders : byte
{
    None = 0,
    Standard = 1 << 0,
    Atmosphere = 1 << 1,
}

/// <summary>
/// The renderer's pipelines: each area declares its passes here as it is made, and they are all compiled together as the
/// run starts and built for the GPU.
/// </summary>
internal sealed class GpuPipelines(IGraphicsApi gpu, RenderGraph graph, IShaderCompiler shaders, Assets assets)
    : IDisposable
{
    private const string Folder = "Shaders/";

    private const string StandardMaterial = "Shaders/Material/Standard.slang";

    private const string AtmosphereSky = "Shaders/Sky/Atmosphere.slang";

    private readonly List<GpuPass> _passes = [];

    public void Dispose()
    {
        foreach (GpuPass pass in _passes)
        {
            gpu.Destroy(pass.Pipeline);
        }
    }

    /// <summary>
    /// Declares a compute pass running <paramref name="entry"/> of the gem's shader <paramref name="module"/>.
    /// </summary>
    public GpuPass Compute(string name, string module, string entry, PassShaders passShaders = PassShaders.None) =>
        Add(
            new GpuPass
            {
                Graph = graph,
                Name = name,
                Module = module,
                Entry = entry,
                Shaders = passShaders,
            }
        );

    /// <summary>
    /// Declares a draw pass over its vertex and pixel entries, with <paramref name="state"/> setting its fixed state.
    /// </summary>
    public GpuPass Draw(
        string name,
        string module,
        string vertexEntry,
        string pixelEntry,
        Func<GraphicsPipelineDescription, GraphicsPipelineDescription> state
    ) =>
        Add(
            new GpuPass
            {
                Graph = graph,
                Name = name,
                Module = module,
                Entry = pixelEntry,
                VertexEntry = vertexEntry,
                DrawState = state,
                Shaders = PassShaders.None,
            }
        );

    /// <summary>
    /// Every declared pass's entries compiled, on workers, in the order <see cref="Create"/> reads them.
    /// </summary>
    public async Task<Result<ImmutableArray<CompiledShader>>> CompileAsync(CancellationToken cancel)
    {
        Result<ImmutableArray<ShaderEntry>> entries = GetEntries();
        if (!entries.Ok)
            return Result.Failure(entries.Error);

        ImmutableArray<ShaderEntry> all = entries.Value;
        Task<Result<CompiledShader>>[] compiling = new Task<Result<CompiledShader>>[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            compiling[i] = shaders.CompileAsync(all[i], null, cancel);
        }

        Result<CompiledShader>[] compiled = await Task.WhenAll(compiling).ConfigureAwait(false);
        ImmutableArray<CompiledShader>.Builder results = ImmutableArray.CreateBuilder<CompiledShader>(compiled.Length);
        for (int i = 0; i < compiled.Length; i++)
        {
            Result<CompiledShader> result = compiled[i];
            if (!result.Ok)
                return Result.Failure($"{all[i].Name} did not compile: {result.Error}");

            results.Add(result.Value);
        }

        return Result.Success(results.MoveToImmutable());
    }

    /// <summary>
    /// The pipelines built from what <see cref="CompileAsync"/> gave. Render thread.
    /// </summary>
    public Result Create(ImmutableArray<CompiledShader> compiled)
    {
        int next = 0;
        foreach (GpuPass pass in _passes)
        {
            CompiledShader entry = compiled[next++];
            pass.GroupSizeX = entry.GroupSizeX;
            pass.GroupSizeY = entry.GroupSizeY;
            Result<GpuPipeline> made;
            if (pass.DrawState is { } state)
            {
                // A draw pass's pixel entry was compiled after its vertex entry.
                CompiledShader pixel = compiled[next++];
                made = gpu.CreatePipeline(
                    state(
                        new GraphicsPipelineDescription
                        {
                            VertexSpirv = entry.Spirv,
                            VertexEntryPoint = entry.EntryPoint,
                            PixelSpirv = pixel.Spirv,
                            PixelEntryPoint = pixel.EntryPoint,
                            Name = pass.Name,
                        }
                    )
                );
            }
            else
            {
                if (entry.GroupSizeX == 0)
                    return Result.Failure($"{pass.Name} reported no group size.");

                made = gpu.CreatePipeline(
                    new ComputePipelineDescription
                    {
                        Spirv = entry.Spirv,
                        EntryPoint = entry.EntryPoint,
                        Name = pass.Name,
                    }
                );
            }

            if (!made.Ok)
                return Result.Failure($"the {pass.Name} pipeline was not built: {made.Error}");

            pass.Pipeline = made.Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// The library shaders <paramref name="wanted"/> asks for, in the order the entry's generic parameters take them.
    /// </summary>
    private static ImmutableArray<ShaderTypeArgument> GetArguments(
        PassShaders wanted,
        ShaderTypeArgument standard,
        ShaderTypeArgument atmosphere
    )
    {
        ImmutableArray<ShaderTypeArgument>.Builder arguments = ImmutableArray.CreateBuilder<ShaderTypeArgument>();
        if (wanted.HasFlag(PassShaders.Standard))
            arguments.Add(standard);

        if (wanted.HasFlag(PassShaders.Atmosphere))
            arguments.Add(atmosphere);

        return arguments.ToImmutable();
    }

    private GpuPass Add(GpuPass pass)
    {
        _passes.Add(pass);

        return pass;
    }

    /// <summary>
    /// Every entry to compile, in the order <see cref="Create"/> reads them; or which shader is missing.
    /// </summary>
    private Result<ImmutableArray<ShaderEntry>> GetEntries()
    {
        Handle<Shader> standard = assets.Find<Shader>(StandardMaterial);
        Handle<Shader> atmosphere = assets.Find<Shader>(AtmosphereSky);
        if (standard == Handle<Shader>.None || atmosphere == Handle<Shader>.None)
            return Result.Failure($"there is no {StandardMaterial} or {AtmosphereSky}.");

        ShaderTypeArgument standardArgument = new(standard, "Standard");
        ShaderTypeArgument atmosphereArgument = new(atmosphere, "Atmosphere");
        ImmutableArray<ShaderEntry>.Builder entries = ImmutableArray.CreateBuilder<ShaderEntry>();
        foreach (GpuPass pass in _passes)
        {
            if (pass.VertexEntry is { } vertex)
                entries.Add(GetEntry(pass.Module, vertex, ShaderStage.Vertex, []));

            ShaderStage stage = pass.DrawState is null ? ShaderStage.Compute : ShaderStage.Pixel;
            entries.Add(
                GetEntry(
                    pass.Module,
                    pass.Entry,
                    stage,
                    GetArguments(pass.Shaders, standardArgument, atmosphereArgument)
                )
            );
        }

        foreach (ShaderEntry entry in entries)
        {
            if (entry.Module == Handle<Shader>.None)
                return Result.Failure($"the shader of {entry.Name} is missing from {Folder}.");
        }

        return Result.Success(entries.ToImmutable());
    }

    private ShaderEntry GetEntry(
        string module,
        string name,
        ShaderStage stage,
        ImmutableArray<ShaderTypeArgument> arguments
    ) =>
        new()
        {
            Module = assets.Find<Shader>($"{Folder}{module}.slang"),
            Name = name,
            Stage = stage,
            TypeArguments = arguments,
        };
}
