using System.Collections.Immutable;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The renderer's pipelines: its entries compiled together as the run starts, and each built for the GPU. A compute
/// entry's group size is what its dispatches are sized by.
/// </summary>
internal sealed class RendererPipelines(IGraphicsApi gpu, IShaderCompiler shaders, Assets assets) : IDisposable
{
    private const string Folder = "Shaders/WorldRenderer/";
    private const string StandardMaterial = "Shaders/Material/Standard.slang";
    private const string AtmosphereSky = "Shaders/Sky/Atmosphere.slang";
    private const string GlassMaterial = "Shaders/Material/Glass.slang";
    private const string NoEffect = "Shaders/Post/NoEffect.slang";
    private const string FieldTracer = "Shaders/Trace/DistanceField.slang";
    private const string RayQueryTracer = "Shaders/Trace/RayQuery.slang";
    private const string PostModule = "Shaders/WorldRenderer/Post.slang";

    public GpuPipeline CullClusters { get; private set; }

    public GpuPipeline DrawVisibility { get; private set; }

    public GpuPipeline BuildHzb { get; private set; }

    public GpuPipeline ResolveStandard { get; private set; }

    public GpuPipeline SkyTransmittance { get; private set; }

    public GpuPipeline SkyMultiScattering { get; private set; }

    public GpuPipeline SkyView { get; private set; }

    public GpuPipeline BinLights { get; private set; }

    public GpuPipeline FogInject { get; private set; }

    public GpuPipeline FogIntegrate { get; private set; }

    public GpuPipeline ShadeLighting { get; private set; }

    public GpuPipeline PostCompose { get; private set; }

    public GpuPipeline PostResolve { get; private set; }

    public GpuPipeline PostHistogram { get; private set; }

    public GpuPipeline PostExposure { get; private set; }

    public GpuPipeline PostBloomDown { get; private set; }

    public GpuPipeline PostBloomUp { get; private set; }

    /// <summary>
    /// The final pass with no effects: what a camera without any runs.
    /// </summary>
    public GpuPipeline PostFinal { get; private set; }

    public GpuPipeline FieldWriteModel { get; private set; }

    public GpuPipeline FieldScroll { get; private set; }

    public GpuPipeline FieldInvalidate { get; private set; }

    public GpuPipeline FieldCollect { get; private set; }

    public GpuPipeline FieldList { get; private set; }

    public GpuPipeline FieldUpdate { get; private set; }

    public GpuPipeline CaptureCard { get; private set; }

    public GpuPipeline ShadowMarkPages { get; private set; }

    public GpuPipeline ShadowInvalidatePages { get; private set; }

    public GpuPipeline ShadowFreePages { get; private set; }

    public GpuPipeline ShadowAllocatePages { get; private set; }

    public GpuPipeline ShadowClearPages { get; private set; }

    public GpuPipeline ShadowFinishPages { get; private set; }

    public GpuPipeline ShadowCull { get; private set; }

    public GpuPipeline DrawShadow { get; private set; }

    public GpuPipeline LitCardLights { get; private set; }

    public GpuPipeline LitCardRadiosity { get; private set; }

    public GpuPipeline LitCardShade { get; private set; }

    public GpuPipeline WorldProbeScroll { get; private set; }

    public GpuPipeline WorldProbeSelect { get; private set; }

    public GpuPipeline WorldProbeTrace { get; private set; }

    public GpuPipeline ScreenProbePlace { get; private set; }

    public GpuPipeline ScreenProbeTrace { get; private set; }

    public GpuPipeline ScreenProbeTemporal { get; private set; }

    public GpuPipeline ScreenProbeFilter { get; private set; }

    public GpuPipeline ReflectionTrace { get; private set; }

    public GpuPipeline ReflectionResolve { get; private set; }

    public GpuPipeline ReflectionTemporal { get; private set; }

    public GpuPipeline Occlusion { get; private set; }

    public GpuPipeline OcclusionBlur { get; private set; }

    /// <summary>
    /// The passes that trace, specialised to the ray-query tracer; none on a GPU that traces no rays.
    /// </summary>
    public GpuPipeline ShadeLightingRayQuery { get; private set; }

    public GpuPipeline LitCardRadiosityRayQuery { get; private set; }

    public GpuPipeline WorldProbeTraceRayQuery { get; private set; }

    public GpuPipeline ScreenProbeTraceRayQuery { get; private set; }

    public GpuPipeline ReflectionTraceRayQuery { get; private set; }

    public GpuPipeline TranslucentMoments { get; private set; }

    public GpuPipeline TranslucentResolve { get; private set; }

    public GpuPipeline GlassShadow { get; private set; }

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
                    CaptureCard,
                    SkyTransmittance,
                    SkyMultiScattering,
                    SkyView,
                    BinLights,
                    FogInject,
                    FogIntegrate,
                    ShadeLighting,
                    PostCompose,
                    PostResolve,
                    PostHistogram,
                    PostExposure,
                    PostBloomDown,
                    PostBloomUp,
                    PostFinal,
                    FieldWriteModel,
                    FieldScroll,
                    FieldInvalidate,
                    FieldCollect,
                    FieldList,
                    FieldUpdate,
                    ShadowMarkPages,
                    ShadowInvalidatePages,
                    ShadowFreePages,
                    ShadowAllocatePages,
                    ShadowClearPages,
                    ShadowFinishPages,
                    ShadowCull,
                    DrawShadow,
                    LitCardLights,
                    LitCardRadiosity,
                    LitCardShade,
                    WorldProbeScroll,
                    WorldProbeSelect,
                    WorldProbeTrace,
                    ScreenProbePlace,
                    ScreenProbeTrace,
                    ScreenProbeTemporal,
                    ScreenProbeFilter,
                    ReflectionTrace,
                    ReflectionResolve,
                    ReflectionTemporal,
                    Occlusion,
                    OcclusionBlur,
                    TranslucentMoments,
                    TranslucentResolve,
                    GlassShadow,
                    ShadeLightingRayQuery,
                    LitCardRadiosityRayQuery,
                    WorldProbeTraceRayQuery,
                    ScreenProbeTraceRayQuery,
                    ReflectionTraceRayQuery,
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

        Handle<Shader> atmosphere = assets.Find<Shader>(AtmosphereSky);
        if (atmosphere == Handle<Shader>.None)
            return Result.Failure($"there is no {AtmosphereSky}.");

        Handle<Shader> glass = assets.Find<Shader>(GlassMaterial);
        if (glass == Handle<Shader>.None)
            return Result.Failure($"there is no {GlassMaterial}.");

        Handle<Shader> noEffect = assets.Find<Shader>(NoEffect);
        if (noEffect == Handle<Shader>.None)
            return Result.Failure($"there is no {NoEffect}.");

        Handle<Shader> fieldTracer = assets.Find<Shader>(FieldTracer);
        Handle<Shader> rayQueryTracer = assets.Find<Shader>(RayQueryTracer);
        if (fieldTracer == Handle<Shader>.None || rayQueryTracer == Handle<Shader>.None)
            return Result.Failure($"there is no {FieldTracer} or {RayQueryTracer}.");

        ShaderTypeArgument field = new(fieldTracer, "DistanceFieldTracer");
        ShaderTypeArgument rayQuery = new(rayQueryTracer, "RayQueryTracer");

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
            Entry("Post", "PostCompose", ShaderStage.Compute),
            Entry("CaptureCard", "CaptureCard", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowMarkPages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowInvalidatePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowFreePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowAllocatePages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowClearPages", ShaderStage.Compute),
            Entry("ShadowPages", "ShadowFinishPages", ShaderStage.Compute),
            Entry("ShadowCull", "ShadowCull", ShaderStage.Compute),
            Entry("DrawShadow", "DrawShadowVertex", ShaderStage.Vertex),
            Entry("DrawShadow", "DrawShadowPixel", ShaderStage.Pixel),
            Entry("SkyLuts", "SkyTransmittance", ShaderStage.Compute),
            Entry("SkyLuts", "SkyMultiScattering", ShaderStage.Compute),
            Entry("SkyLuts", "SkyView", ShaderStage.Compute),
            Entry("BinLights", "BinLights", ShaderStage.Compute),
            Entry("Fog", "FogInject", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere")],
            },
            Entry("Fog", "FogIntegrate", ShaderStage.Compute),
            Entry("ShadeLighting", "ShadeLighting", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), field],
            },
            Entry("Field", "FieldWriteModel", ShaderStage.Compute),
            Entry("Field", "FieldScroll", ShaderStage.Compute),
            Entry("Field", "FieldInvalidate", ShaderStage.Compute),
            Entry("Field", "FieldCollect", ShaderStage.Compute),
            Entry("Field", "FieldList", ShaderStage.Compute),
            Entry("Field", "FieldUpdate", ShaderStage.Compute),
            Entry("LitCards", "LitCardLights", ShaderStage.Compute),
            Entry("LitCards", "LitCardRadiosity", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), field],
            },
            Entry("LitCards", "LitCardShade", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere")],
            },
            Entry("WorldProbes", "WorldProbeScroll", ShaderStage.Compute),
            Entry("WorldProbes", "WorldProbeSelect", ShaderStage.Compute),
            Entry("WorldProbes", "WorldProbeTrace", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), field],
            },
            Entry("ScreenProbes", "ScreenProbePlace", ShaderStage.Compute),
            Entry("ScreenProbes", "ScreenProbeTrace", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), field],
            },
            Entry("ScreenProbes", "ScreenProbeTemporal", ShaderStage.Compute),
            Entry("ScreenProbes", "ScreenProbeFilter", ShaderStage.Compute),
            Entry("Reflections", "ReflectionTrace", ShaderStage.Compute) with
            {
                TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), field],
            },
            Entry("Reflections", "ReflectionResolve", ShaderStage.Compute),
            Entry("Reflections", "ReflectionTemporal", ShaderStage.Compute),
            Entry("DrawTranslucent", "DrawTranslucentVertex", ShaderStage.Vertex),
            Entry("DrawTranslucent", "TranslucentMomentsPixel", ShaderStage.Pixel) with
            {
                TypeArguments = [new ShaderTypeArgument(glass, "Glass")],
            },
            Entry("DrawTranslucent", "TranslucentResolvePixel", ShaderStage.Pixel) with
            {
                TypeArguments =
                [
                    new ShaderTypeArgument(glass, "Glass"),
                    new ShaderTypeArgument(atmosphere, "Atmosphere"),
                ],
            },
            Entry("DrawTranslucent", "GlassShadowVertex", ShaderStage.Vertex),
            Entry("DrawTranslucent", "GlassShadowPixel", ShaderStage.Pixel) with
            {
                TypeArguments = [new ShaderTypeArgument(glass, "Glass")],
            },
            Entry("Post", "PostResolve", ShaderStage.Compute),
            Entry("Post", "PostHistogram", ShaderStage.Compute),
            Entry("Post", "PostExposure", ShaderStage.Compute),
            Entry("Post", "PostBloomDown", ShaderStage.Compute),
            Entry("Post", "PostBloomUp", ShaderStage.Compute),
            GetFinalEntry([noEffect, noEffect, noEffect, noEffect]),
            Entry("Occlusion", "Occlusion", ShaderStage.Compute),
            Entry("Occlusion", "OcclusionBlur", ShaderStage.Compute),
        ];
        if (gpu.Features.HasRayQuery)
        {
            entries =
            [
                .. entries,
                Entry("ShadeLighting", "ShadeLighting", ShaderStage.Compute) with
                {
                    TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), rayQuery],
                },
                Entry("LitCards", "LitCardRadiosity", ShaderStage.Compute) with
                {
                    TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), rayQuery],
                },
                Entry("WorldProbes", "WorldProbeTrace", ShaderStage.Compute) with
                {
                    TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), rayQuery],
                },
                Entry("ScreenProbes", "ScreenProbeTrace", ShaderStage.Compute) with
                {
                    TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), rayQuery],
                },
                Entry("Reflections", "ReflectionTrace", ShaderStage.Compute) with
                {
                    TypeArguments = [new ShaderTypeArgument(atmosphere, "Atmosphere"), rayQuery],
                },
            ];
        }

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
        Result<GpuPipeline> capture = Compute(compiled[6], "Capture card");
        if (!capture.Ok)
            return Result.Failure(capture.Error);

        CaptureCard = capture.Value;
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
        (string Name, int Index)[] lightingEntries =
        [
            ("Sky transmittance", 16),
            ("Sky multi-scattering", 17),
            ("Sky view", 18),
            ("Bin lights", 19),
            ("Fog inject", 20),
            ("Fog integrate", 21),
            ("Shade", 22),
        ];
        GpuPipeline[] lightingPipelines = new GpuPipeline[lightingEntries.Length];
        for (int i = 0; i < lightingEntries.Length; i++)
        {
            Result<GpuPipeline> made = Compute(compiled[lightingEntries[i].Index], lightingEntries[i].Name);
            if (!made.Ok)
                return Result.Failure(made.Error);

            lightingPipelines[i] = made.Value;
        }

        (SkyTransmittance, SkyMultiScattering, SkyView, BinLights, FogInject, FogIntegrate, ShadeLighting) = (
            lightingPipelines[0],
            lightingPipelines[1],
            lightingPipelines[2],
            lightingPipelines[3],
            lightingPipelines[4],
            lightingPipelines[5],
            lightingPipelines[6]
        );
        (string Name, int Index)[] fieldEntries =
        [
            ("Field write model", 23),
            ("Field scroll", 24),
            ("Field invalidate", 25),
            ("Field collect", 26),
            ("Field list", 27),
            ("Field update", 28),
        ];
        GpuPipeline[] fieldPipelines = new GpuPipeline[fieldEntries.Length];
        for (int i = 0; i < fieldEntries.Length; i++)
        {
            Result<GpuPipeline> made = Compute(compiled[fieldEntries[i].Index], fieldEntries[i].Name);
            if (!made.Ok)
                return Result.Failure(made.Error);

            fieldPipelines[i] = made.Value;
        }

        (FieldWriteModel, FieldScroll, FieldInvalidate, FieldCollect, FieldList, FieldUpdate) = (
            fieldPipelines[0],
            fieldPipelines[1],
            fieldPipelines[2],
            fieldPipelines[3],
            fieldPipelines[4],
            fieldPipelines[5]
        );
        (string Name, int Index)[] giEntries =
        [
            ("List card lights", 29),
            ("Card radiosity", 30),
            ("Light cards", 31),
            ("Scroll probes", 32),
            ("Select probes", 33),
            ("Trace probes", 34),
            ("Place screen probes", 35),
            ("Trace screen probes", 36),
            ("Reproject screen probes", 37),
            ("Filter screen probes", 38),
            ("Trace reflections", 39),
            ("Resolve reflections", 40),
            ("Reproject reflections", 41),
            ("Occlusion", 53),
            ("Blur occlusion", 54),
        ];
        GpuPipeline[] giPipelines = new GpuPipeline[giEntries.Length];
        for (int i = 0; i < giEntries.Length; i++)
        {
            Result<GpuPipeline> made = Compute(compiled[giEntries[i].Index], giEntries[i].Name);
            if (!made.Ok)
                return Result.Failure(made.Error);

            giPipelines[i] = made.Value;
        }

        (
            LitCardLights,
            LitCardRadiosity,
            LitCardShade,
            WorldProbeScroll,
            WorldProbeSelect,
            WorldProbeTrace,
            ScreenProbePlace,
            ScreenProbeTrace,
            ScreenProbeTemporal,
            ScreenProbeFilter,
            ReflectionTrace,
            ReflectionResolve,
            ReflectionTemporal,
            Occlusion,
            OcclusionBlur
        ) = (
            giPipelines[0],
            giPipelines[1],
            giPipelines[2],
            giPipelines[3],
            giPipelines[4],
            giPipelines[5],
            giPipelines[6],
            giPipelines[7],
            giPipelines[8],
            giPipelines[9],
            giPipelines[10],
            giPipelines[11],
            giPipelines[12],
            giPipelines[13],
            giPipelines[14]
        );
        (string Name, int Vertex, int Pixel, GpuFormat[] Colors, bool HasDepth, BlendMode Blend)[] translucentEntries =
        [
            (
                "Translucent moments",
                42,
                43,
                [GpuFormat.R16G16B16A16Sfloat, GpuFormat.R16G16B16A16Sfloat],
                true,
                BlendMode.Additive
            ),
            (
                "Translucent resolve",
                42,
                44,
                [GpuFormat.R16G16B16A16Sfloat, GpuFormat.R16G16Sfloat],
                true,
                BlendMode.Additive
            ),
            ("Glass shadow", 45, 46, [GpuFormat.R16G16B16A16Sfloat], false, BlendMode.Multiply),
        ];
        GpuPipeline[] translucentPipelines = new GpuPipeline[translucentEntries.Length];
        for (int i = 0; i < translucentEntries.Length; i++)
        {
            (string name, int vertex, int pixel, GpuFormat[] colors, bool hasDepth, BlendMode blend) =
                translucentEntries[i];
            Result<GpuPipeline> made = gpu.CreatePipeline(
                new GraphicsPipelineDescription
                {
                    VertexSpirv = compiled[vertex].Spirv,
                    VertexEntryPoint = compiled[vertex].EntryPoint,
                    PixelSpirv = compiled[pixel].Spirv,
                    PixelEntryPoint = compiled[pixel].EntryPoint,
                    ColorFormats = [.. colors],
                    DepthFormat = hasDepth ? GpuFormat.D32Sfloat : default,
                    Depth = hasDepth ? DepthMode.TestOnly : DepthMode.None,
                    Blend = blend,
                    Cull = CullMode.None,
                    Name = name,
                }
            );
            if (!made.Ok)
                return Result.Failure($"the {name} pipeline was not built: {made.Error}");

            translucentPipelines[i] = made.Value;
        }

        (TranslucentMoments, TranslucentResolve, GlassShadow) = (
            translucentPipelines[0],
            translucentPipelines[1],
            translucentPipelines[2]
        );
        (string Name, int Index)[] postEntries =
        [
            ("Post compose", 5),
            ("Post resolve", 47),
            ("Post histogram", 48),
            ("Post exposure", 49),
            ("Post bloom down", 50),
            ("Post bloom up", 51),
            ("Post final", 52),
        ];
        GpuPipeline[] postPipelines = new GpuPipeline[postEntries.Length];
        for (int i = 0; i < postEntries.Length; i++)
        {
            Result<GpuPipeline> made = Compute(compiled[postEntries[i].Index], postEntries[i].Name);
            if (!made.Ok)
                return Result.Failure(made.Error);

            postPipelines[i] = made.Value;
        }

        (PostCompose, PostResolve, PostHistogram, PostExposure, PostBloomDown, PostBloomUp, PostFinal) = (
            postPipelines[0],
            postPipelines[1],
            postPipelines[2],
            postPipelines[3],
            postPipelines[4],
            postPipelines[5],
            postPipelines[6]
        );
        if (compiled.Length > 55)
        {
            (string Name, int Index)[] rayQueryEntries =
            [
                ("Shade with ray queries", 55),
                ("Card radiosity with ray queries", 56),
                ("Trace probes with ray queries", 57),
                ("Trace screen probes with ray queries", 58),
                ("Trace reflections with ray queries", 59),
            ];
            GpuPipeline[] rayQueryPipelines = new GpuPipeline[rayQueryEntries.Length];
            for (int i = 0; i < rayQueryEntries.Length; i++)
            {
                Result<GpuPipeline> made = Compute(compiled[rayQueryEntries[i].Index], rayQueryEntries[i].Name);
                if (!made.Ok)
                    return Result.Failure(made.Error);

                rayQueryPipelines[i] = made.Value;
            }

            (
                ShadeLightingRayQuery,
                LitCardRadiosityRayQuery,
                WorldProbeTraceRayQuery,
                ScreenProbeTraceRayQuery,
                ReflectionTraceRayQuery
            ) = (
                rayQueryPipelines[0],
                rayQueryPipelines[1],
                rayQueryPipelines[2],
                rayQueryPipelines[3],
                rayQueryPipelines[4]
            );
        }

        TileSize = compiled[5].GroupSizeX;
        bool areTiled =
            compiled[3].GroupSizeX == TileSize
            && compiled[4].GroupSizeX == TileSize
            && compiled[6].GroupSizeX == TileSize
            && compiled[7].GroupSizeX == TileSize
            && compiled[11].GroupSizeX == TileSize
            && compiled[22].GroupSizeX == TileSize
            && TileSize > 0;
        if (!areTiled || CullThreads == 0)
            return Result.Failure("the compute entries reported no group size, or the full-screen ones differ.");

        return Result.Success();
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
        Handle<Shader>[] slots = new Handle<Shader>[4];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i < effects.Length && effects[i] != Handle<Shader>.None ? effects[i] : noEffect;
        }

        return shaders.CompileAsync(GetFinalEntry(slots), null, cancel);
    }

    /// <summary>
    /// A compute pipeline from a compiled entry, for a pass built after the start.
    /// </summary>
    public Result<GpuPipeline> CreateCompute(CompiledShader compiled, string name) => Compute(compiled, name);

    private ShaderEntry GetFinalEntry(ReadOnlySpan<Handle<Shader>> effects)
    {
        ShaderTypeArgument[] arguments = new ShaderTypeArgument[effects.Length];
        for (int i = 0; i < effects.Length; i++)
        {
            string type = System.IO.Path.GetFileNameWithoutExtension(assets.GetPath(effects[i]) ?? "NoEffect");
            arguments[i] = new ShaderTypeArgument(effects[i], type);
        }

        return new ShaderEntry
        {
            Module = assets.Find<Shader>(PostModule),
            Name = "PostFinal",
            Stage = ShaderStage.Compute,
            TypeArguments = [.. arguments],
        };
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
