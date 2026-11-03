using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The renderer's pipelines: its entries compiled together as the run starts, and each built for the GPU. A compute
/// entry's group size is what its dispatches are sized by.
/// </summary>
internal sealed class RendererPipelines(IGraphicsApi gpu, IShaderCompiler shaders, AssetDatabase assets) : IDisposable
{
    private const string Folder = "Shaders/WorldRenderer/";
    private const string StandardMaterial = "Shaders/Material/Standard.slang";

    public GpuPipeline CullClusters { get; private set; }

    public GpuPipeline DrawVisibility { get; private set; }

    public GpuPipeline BuildHzb { get; private set; }

    public GpuPipeline ResolveStandard { get; private set; }

    public GpuPipeline ShadeFlat { get; private set; }

    public GpuPipeline CaptureCell { get; private set; }

    public GpuPipeline ShadowMarkPages { get; private set; }

    public GpuPipeline ShadowInvalidatePages { get; private set; }

    public GpuPipeline ShadowFreePages { get; private set; }

    public GpuPipeline ShadowAllocatePages { get; private set; }

    public GpuPipeline ShadowClearPages { get; private set; }

    public GpuPipeline ShadowFinishPages { get; private set; }

    public GpuPipeline ShadowCull { get; private set; }

    public GpuPipeline DrawShadow { get; private set; }

    public uint CullThreads { get; private set; }

    public uint TileSize { get; private set; }

    public void Dispose()
    {
        foreach (
            GpuPipeline pipeline in (ReadOnlySpan<GpuPipeline>)
                [
                    CullClusters,
                    DrawVisibility,
                    BuildHzb,
                    ResolveStandard,
                    ShadeFlat,
                    CaptureCell,
                    ShadowMarkPages,
                    ShadowInvalidatePages,
                    ShadowFreePages,
                    ShadowAllocatePages,
                    ShadowClearPages,
                    ShadowFinishPages,
                    ShadowCull,
                    DrawShadow,
                ]
        )
        {
            if (!pipeline.IsNone)
                gpu.Destroy(pipeline);
        }
    }

    /// <summary>
    /// Every entry compiled, on workers, in the order <see cref="Create"/> reads them.
    /// </summary>
    public async Task<Result<ImmutableArray<CompiledShader>>> CompileAsync(CancellationToken cancel)
    {
        Handle<Shader> standard = assets.Find<Shader>(StandardMaterial);
        if (standard == Handle<Shader>.None)
            return Result.Failure($"there is no {StandardMaterial}.");

        ImmutableArray<ShaderEntry> entries =
        [
            Entry("CullClusters", "CullClusters", ShaderStage.Compute),
            Entry("DrawVisibility", "DrawVisibilityVertex", ShaderStage.Vertex),
            Entry("DrawVisibility", "DrawVisibilityPixel", ShaderStage.Pixel),
            Entry("BuildHzb", "BuildHzb", ShaderStage.Compute),
            Entry("ResolveMaterial", "ResolveMaterial", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(standard, "Standard")],
            },
            Entry("ShadeFlat", "ShadeFlat", ShaderStage.Compute),
            Entry("CaptureCell", "CaptureCell", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowMarkPages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowInvalidatePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowFreePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowAllocatePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowClearPages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowFinishPages", ShaderStage.Compute),
            Entry("ShadowCull", "ShadowCull", ShaderStage.Compute),
            Entry("DrawShadow", "DrawShadowVertex", ShaderStage.Vertex),
            Entry("DrawShadow", "DrawShadowPixel", ShaderStage.Pixel),
        ];
        foreach (ShaderEntry entry in entries)
        {
            if (entry.Module == Handle<Shader>.None)
                return Result.Failure($"a module of {Folder} is missing.");
        }

        Task<Result<CompiledShader>>[] compiling = new Task<Result<CompiledShader>>[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            compiling[i] = shaders.CompileAsync(entries[i], null, cancel);
        }

        Result<CompiledShader>[] compiled = await Task.WhenAll(compiling).ConfigureAwait(false);
        ImmutableArray<CompiledShader>.Builder results = ImmutableArray.CreateBuilder<CompiledShader>(compiled.Length);
        for (int i = 0; i < compiled.Length; i++)
        {
            Result<CompiledShader> result = compiled[i];
            if (!result.Ok)
                return Result.Failure($"{entries[i].Name} did not compile: {result.Error}");

            results.Add(result.Value);
        }

        return Result.Success(results.MoveToImmutable());
    }

    /// <summary>
    /// The pipelines built from what <see cref="CompileAsync"/> gave. Main thread.
    /// </summary>
    public Result Create(ImmutableArray<CompiledShader> compiled)
    {
        Result<GpuPipeline> cull = Compute(compiled[0], "Cull clusters");
        if (!cull.Ok)
            return Result.Failure(cull.Error);

        CullClusters = cull.Value;
        CullThreads = compiled[0].GroupSizeX;
        Result<GpuPipeline> draw = gpu.CreatePipeline(
            new GraphicsPipelineDescription
            {
                VertexSpirv = compiled[1].Spirv,
                VertexEntryPoint = compiled[1].EntryPoint,
                PixelSpirv = compiled[2].Spirv,
                PixelEntryPoint = compiled[2].EntryPoint,
                ColorFormats = [GpuFormat.R32Uint],
                DepthFormat = GpuFormat.D32Sfloat,
                Depth = DepthMode.TestAndWrite,
                Name = "Draw visibility",
            }
        );
        if (!draw.Ok)
            return Result.Failure($"the visibility pipeline was not built: {draw.Error}");

        DrawVisibility = draw.Value;
        Result<GpuPipeline> hzb = Compute(compiled[3], "Build depth pyramid");
        if (!hzb.Ok)
            return Result.Failure(hzb.Error);

        BuildHzb = hzb.Value;
        Result<GpuPipeline> resolve = Compute(compiled[4], "Resolve standard material");
        if (!resolve.Ok)
            return Result.Failure(resolve.Error);

        ResolveStandard = resolve.Value;
        Result<GpuPipeline> shade = Compute(compiled[5], "Shade flat");
        if (!shade.Ok)
            return Result.Failure(shade.Error);

        ShadeFlat = shade.Value;
        Result<GpuPipeline> capture = Compute(compiled[6], "Capture cell");
        if (!capture.Ok)
            return Result.Failure(capture.Error);

        CaptureCell = capture.Value;
        (string Name, int Index)[] shadowEntries =
        [
            ("Shadow mark pages", 7),
            ("Shadow invalidate pages", 8),
            ("Shadow free pages", 9),
            ("Shadow allocate pages", 10),
            ("Shadow clear pages", 11),
            ("Shadow finish pages", 12),
            ("Shadow cull", 13),
        ];
        GpuPipeline[] shadowPipelines = new GpuPipeline[shadowEntries.Length];
        for (int i = 0; i < shadowEntries.Length; i++)
        {
            Result<GpuPipeline> made = Compute(compiled[shadowEntries[i].Index], shadowEntries[i].Name);
            if (!made.Ok)
                return Result.Failure(made.Error);

            shadowPipelines[i] = made.Value;
        }

        (
            ShadowMarkPages,
            ShadowInvalidatePages,
            ShadowFreePages,
            ShadowAllocatePages,
            ShadowClearPages,
            ShadowFinishPages,
            ShadowCull
        ) = (
            shadowPipelines[0],
            shadowPipelines[1],
            shadowPipelines[2],
            shadowPipelines[3],
            shadowPipelines[4],
            shadowPipelines[5],
            shadowPipelines[6]
        );
        Result<GpuPipeline> drawShadow = gpu.CreatePipeline(
            new GraphicsPipelineDescription
            {
                VertexSpirv = compiled[14].Spirv,
                VertexEntryPoint = compiled[14].EntryPoint,
                PixelSpirv = compiled[15].Spirv,
                PixelEntryPoint = compiled[15].EntryPoint,
                Cull = CullMode.None,
                Name = "Draw shadow pages",
            }
        );
        if (!drawShadow.Ok)
            return Result.Failure($"the shadow pipeline was not built: {drawShadow.Error}");

        DrawShadow = drawShadow.Value;
        TileSize = compiled[5].GroupSizeX;
        bool areTiled =
            compiled[3].GroupSizeX == TileSize
            && compiled[4].GroupSizeX == TileSize
            && compiled[6].GroupSizeX == TileSize
            && compiled[7].GroupSizeX == TileSize
            && compiled[11].GroupSizeX == TileSize
            && TileSize > 0;
        if (!areTiled || CullThreads == 0)
            return Result.Failure("the compute entries reported no group size, or the full-screen ones differ.");

        return Result.Success();
    }

    private ShaderEntry Entry(string module, string name, ShaderStage stage) =>
        new()
        {
            Module = assets.Find<Shader>($"{Folder}{module}.slang"),
            Name = name,
            Stage = stage,
        };

    private Result<GpuPipeline> Compute(CompiledShader compiled, string name)
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
}
