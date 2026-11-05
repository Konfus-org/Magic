using System.Collections.Immutable;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// The library shaders a pass's entry is specialised with, in this order.
/// </summary>
[Flags]
internal enum PassShaders : byte
{
    None = 0,
    Glass = 1 << 0,
    Standard = 1 << 1,
    Atmosphere = 1 << 2,

    /// <summary>
    /// It traces rays: built once for the field tracer and, where the GPU can, once for the ray-query tracer.
    /// </summary>
    Tracer = 1 << 3,

    /// <summary>
    /// Every effect slot of the final post pass empty.
    /// </summary>
    NoEffects = 1 << 4,
}

/// <summary>
/// The renderer's pipelines: each area declares its passes here as it is made, and they are all compiled together as the
/// run starts and built for the GPU.
/// </summary>
internal sealed class GpuPipelines(IGraphicsApi gpu, IShaderCompiler shaders, Assets assets) : IDisposable
{
    private const string Folder = "Shaders/";
    private const string StandardMaterial = "Shaders/Material/Standard.slang";
    private const string AtmosphereSky = "Shaders/Sky/Atmosphere.slang";
    private const string GlassMaterial = "Shaders/Material/Glass.slang";
    private const string NoEffect = "Shaders/Post/NoEffect.slang";
    private const string FieldTracer = "Shaders/Trace/DistanceField.slang";
    private const string RayQueryTracer = "Shaders/Trace/RayQuery.slang";
    private const string PostModule = "PostPasses";
    private const int EffectSlots = 4;

    private readonly List<GpuPass> _passes = [];

    /// <summary>
    /// The tile every full-screen pass runs in, known once they are built.
    /// </summary>
    public uint TileSize { get; private set; }

    public void Dispose()
    {
        foreach (GpuPass pass in _passes)
        {
            if (!pass.Pipeline.IsNone)
                gpu.Destroy(pass.Pipeline);

            if (!pass.RayQueryPipeline.IsNone)
                gpu.Destroy(pass.RayQueryPipeline);
        }
    }

    /// <summary>
    /// Declares a compute pass running <paramref name="entry"/> of the gem's shader <paramref name="module"/>;
    /// <paramref name="isTiled"/> for a full-screen one.
    /// </summary>
    public GpuPass Compute(
        string name,
        string module,
        string entry,
        PassShaders passShaders = PassShaders.None,
        bool isTiled = false
    ) =>
        Add(
            new GpuPass
            {
                Name = name,
                Module = module,
                Entry = entry,
                Shaders = passShaders,
                IsTiled = isTiled,
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
        Func<GraphicsPipelineDescription, GraphicsPipelineDescription> state,
        PassShaders passShaders = PassShaders.None
    ) =>
        Add(
            new GpuPass
            {
                Name = name,
                Module = module,
                Entry = pixelEntry,
                VertexEntry = vertexEntry,
                DrawState = state,
                Shaders = passShaders,
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
    /// The pipelines built from what <see cref="CompileAsync"/> gave. Main thread.
    /// </summary>
    public Result Create(ImmutableArray<CompiledShader> compiled)
    {
        int next = 0;
        foreach (GpuPass pass in _passes)
        {
            Result made = pass.DrawState is not { } state
                ? CreateCompute(pass, compiled, ref next)
                : CreateDraw(pass, state, compiled, ref next);
            if (!made.Ok)
                return made;
        }

        foreach (GpuPass pass in _passes)
        {
            if (pass.DrawState is null && pass.GroupSizeX == 0)
                return Result.Failure($"{pass.Name} reported no group size.");

            if (!pass.IsTiled)
                continue;

            TileSize = TileSize == 0 ? pass.GroupSizeX : TileSize;
            if (pass.GroupSizeX != TileSize)
                return Result.Failure($"{pass.Name} runs in tiles of {pass.GroupSizeX}, not {TileSize}.");
        }

        return TileSize > 0 ? Result.Success() : Result.Failure("no full-screen pass reported a group size.");
    }

    /// <summary>
    /// Compiles the final post pass for a camera's effects, on a worker: an empty slot runs no effect, and a module's
    /// effect is the struct named as its file is.
    /// </summary>
    public Task<Result<CompiledShader>> CompileFinalAsync(
        ImmutableArray<Handle<Shader>> effects,
        CancellationToken cancel
    )
    {
        Handle<Shader> noEffect = assets.Find<Shader>(NoEffect);
        ImmutableArray<ShaderTypeArgument>.Builder arguments = ImmutableArray.CreateBuilder<ShaderTypeArgument>(
            EffectSlots
        );
        for (int i = 0; i < EffectSlots; i++)
        {
            Handle<Shader> effect = i < effects.Length && effects[i] != Handle<Shader>.None ? effects[i] : noEffect;
            string type = System.IO.Path.GetFileNameWithoutExtension(assets.GetPath(effect) ?? "NoEffect");
            arguments.Add(new ShaderTypeArgument(effect, type));
        }

        ShaderEntry entry = GetEntry(PostModule, "PostFinal", ShaderStage.Compute, arguments.MoveToImmutable());

        return shaders.CompileAsync(entry, null, cancel);
    }

    /// <summary>
    /// A compute pipeline from a compiled entry, for a pass built after the start.
    /// </summary>
    public Result<GpuPipeline> CreateCompute(CompiledShader compiled, string name)
    {
        Result<GpuPipeline> pipeline = gpu.CreatePipeline(
            new ComputePipelineDescription
            {
                Spirv = compiled.Spirv,
                EntryPoint = compiled.EntryPoint,
                Name = name,
            }
        );

        return pipeline.Ok ? pipeline : Result.Failure($"the {name} pipeline was not built: {pipeline.Error}");
    }

    private GpuPass Add(GpuPass pass)
    {
        _passes.Add(pass);

        return pass;
    }

    private Result CreateCompute(GpuPass pass, ImmutableArray<CompiledShader> compiled, ref int next)
    {
        CompiledShader entry = compiled[next++];
        pass.GroupSizeX = entry.GroupSizeX;
        Result<GpuPipeline> made = CreateCompute(entry, pass.Name);
        if (!made.Ok)
            return Result.Failure(made.Error);

        pass.Pipeline = made.Value;
        if (!HasRayQueryVariant(pass))
            return Result.Success();

        Result<GpuPipeline> rayQuery = CreateCompute(compiled[next++], $"{pass.Name} with ray queries");
        if (!rayQuery.Ok)
            return Result.Failure(rayQuery.Error);

        pass.RayQueryPipeline = rayQuery.Value;

        return Result.Success();
    }

    private Result CreateDraw(
        GpuPass pass,
        Func<GraphicsPipelineDescription, GraphicsPipelineDescription> state,
        ImmutableArray<CompiledShader> compiled,
        ref int next
    )
    {
        CompiledShader vertex = compiled[next++];
        CompiledShader pixel = compiled[next++];
        GraphicsPipelineDescription description = new()
        {
            VertexSpirv = vertex.Spirv,
            VertexEntryPoint = vertex.EntryPoint,
            PixelSpirv = pixel.Spirv,
            PixelEntryPoint = pixel.EntryPoint,
            Name = pass.Name,
        };
        Result<GpuPipeline> made = gpu.CreatePipeline(state(description));
        if (!made.Ok)
            return Result.Failure($"the {pass.Name} pipeline was not built: {made.Error}");

        pass.Pipeline = made.Value;

        return Result.Success();
    }

    private bool HasRayQueryVariant(GpuPass pass) =>
        pass.Shaders.HasFlag(PassShaders.Tracer) && gpu.Features.HasRayQuery;

    /// <summary>
    /// Every entry to compile, in the order <see cref="Create"/> reads them; or which shader is missing.
    /// </summary>
    private Result<ImmutableArray<ShaderEntry>> GetEntries()
    {
        Result<LibraryShaders> library = FindLibrary();
        if (!library.Ok)
            return Result.Failure(library.Error);

        ImmutableArray<ShaderEntry>.Builder entries = ImmutableArray.CreateBuilder<ShaderEntry>();
        foreach (GpuPass pass in _passes)
        {
            if (pass.VertexEntry is { } vertex)
                entries.Add(GetEntry(pass.Module, vertex, ShaderStage.Vertex, []));

            ShaderStage stage = pass.DrawState is null ? ShaderStage.Compute : ShaderStage.Pixel;
            entries.Add(
                GetEntry(pass.Module, pass.Entry, stage, library.Value.GetArguments(pass.Shaders, library.Value.Field))
            );
            if (HasRayQueryVariant(pass))
                entries.Add(
                    GetEntry(
                        pass.Module,
                        pass.Entry,
                        stage,
                        library.Value.GetArguments(pass.Shaders, library.Value.RayQuery)
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

    private Result<LibraryShaders> FindLibrary()
    {
        foreach (
            string path in (ReadOnlySpan<string>)
                [StandardMaterial, AtmosphereSky, GlassMaterial, NoEffect, FieldTracer, RayQueryTracer]
        )
        {
            if (assets.Find<Shader>(path) == Handle<Shader>.None)
                return Result.Failure($"there is no {path}.");
        }

        return Result.Success(
            new LibraryShaders(
                new ShaderTypeArgument(assets.Find<Shader>(GlassMaterial), "Glass"),
                new ShaderTypeArgument(assets.Find<Shader>(StandardMaterial), "Standard"),
                new ShaderTypeArgument(assets.Find<Shader>(AtmosphereSky), "Atmosphere"),
                new ShaderTypeArgument(assets.Find<Shader>(FieldTracer), "DistanceFieldTracer"),
                new ShaderTypeArgument(assets.Find<Shader>(RayQueryTracer), "RayQueryTracer"),
                new ShaderTypeArgument(assets.Find<Shader>(NoEffect), "NoEffect")
            )
        );
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

    /// <summary>
    /// The library's shaders an entry is specialised with.
    /// </summary>
    private readonly record struct LibraryShaders(
        ShaderTypeArgument Glass,
        ShaderTypeArgument Standard,
        ShaderTypeArgument Atmosphere,
        ShaderTypeArgument Field,
        ShaderTypeArgument RayQuery,
        ShaderTypeArgument NoEffect
    )
    {
        /// <summary>
        /// What <paramref name="wanted"/> asks for, with <paramref name="tracer"/> as the tracer.
        /// </summary>
        public ImmutableArray<ShaderTypeArgument> GetArguments(PassShaders wanted, ShaderTypeArgument tracer)
        {
            ImmutableArray<ShaderTypeArgument>.Builder arguments = ImmutableArray.CreateBuilder<ShaderTypeArgument>();
            if (wanted.HasFlag(PassShaders.Glass))
                arguments.Add(Glass);

            if (wanted.HasFlag(PassShaders.Standard))
                arguments.Add(Standard);

            if (wanted.HasFlag(PassShaders.Atmosphere))
                arguments.Add(Atmosphere);

            if (wanted.HasFlag(PassShaders.Tracer))
                arguments.Add(tracer);

            if (wanted.HasFlag(PassShaders.NoEffects))
                arguments.AddRange(NoEffect, NoEffect, NoEffect, NoEffect);

            return arguments.ToImmutable();
        }
    }
}
