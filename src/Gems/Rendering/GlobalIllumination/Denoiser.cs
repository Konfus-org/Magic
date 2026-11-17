using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The textures NVIDIA's NRD reads and writes for one view: its inputs, which the diffuse trace writes, and its output,
/// which shading reads.
/// </summary>
internal readonly record struct DenoiserTextures(
    GpuTexture Motion,
    GpuTexture NormalRoughness,
    GpuTexture ViewZ,
    GpuTexture Radiance,
    GpuTexture Output
);

/// <summary>
/// Denoises the traced diffuse light with NVIDIA's NRD, RELAX_DIFFUSE: its passes, with NRD's own SPIR-V, run as
/// bound shaders over the textures of its pools, which the graph keeps.
/// </summary>
/// <remarks>
/// One for each view: its NRD instance and last frame's matrices are the view's own.
/// </remarks>
internal sealed unsafe class Denoiser : IDisposable
{
    private const uint Identifier = 0;

    /// <summary>
    /// A bound shader's constants start at a multiple of this many bytes in the staging ring.
    /// </summary>
    private const int ConstantsAlignment = 256;

    private readonly IGraphicsApi _gpu;
    private readonly Nrd.Instance* _instance;
    private readonly ImmutableArray<ReadOnlyMemory<byte>> _spirv;
    private readonly string _entryPoint;

    /// <summary>
    /// Last frame's view of the world, projection and traced size, which NRD compares with this frame's.
    /// </summary>
    private (Matrix4x4 WorldToView, Matrix4x4 ViewToClip, int Width, int Height)? _previous;

    /// <summary>
    /// Makes the view's NRD instance. It takes the SPIR-V of <paramref name="first"/>, the first view's denoiser, so
    /// all views share one set of pipelines: the GPU provider keys a pipeline by its SPIR-V's memory.
    /// </summary>
    public Denoiser(IGraphicsApi gpu, Denoiser? first)
    {
        _gpu = gpu;
        Nrd.DenoiserDesc denoiser = new() { identifier = Identifier, denoiser = Nrd.Denoiser.RELAX_DIFFUSE };
        Nrd.InstanceCreationDesc creation = new() { denoisers = &denoiser, denoisersNum = 1 };
        Nrd.Instance* instance;
        Nrd.Result created = Nrd.NRD.CreateInstance(&creation, &instance);
        if (created != Nrd.Result.SUCCESS)
            throw new InvalidOperationException($"NRD made no instance: {created}.");

        _instance = instance;
        Nrd.RelaxSettings relax = Relax;
        Nrd.Result relaxSet = Nrd.NRD.SetDenoiserSettings(instance, Identifier, &relax);
        if (relaxSet != Nrd.Result.SUCCESS)
            throw new InvalidOperationException($"NRD took no RELAX settings: {relaxSet}.");

        Nrd.InstanceDesc* desc = Nrd.NRD.GetInstanceDesc(instance);
        _entryPoint = Marshal.PtrToStringUTF8((nint)desc->shaderEntryPoint) ?? "main";
        if (first is not null)
        {
            _spirv = first._spirv;

            return;
        }

        ImmutableArray<ReadOnlyMemory<byte>>.Builder spirv = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        for (int i = 0; i < desc->pipelinesNum; i++)
        {
            Nrd.ComputeShaderDesc code = desc->pipelines[i].computeShaderSPIRV;
            spirv.Add(new ReadOnlySpan<byte>(code.bytecode, (int)code.size).ToArray());
        }

        _spirv = spirv.ToImmutable();
    }

    /// <summary>
    /// RELAX_DIFFUSE's settings: every field, as the binding drops NRD's C++ defaults. NRD 4.18's defaults, but a
    /// shorter history and the anti-firefly filter, so the light settles in fewer frames after fast motion. The
    /// history fix (3 frames, NRD's most) and the anti-lag are NRD's defaults.
    /// </summary>
    private static Nrd.RelaxSettings Relax =>
        new()
        {
            antilagSettings = new()
            {
                accelerationAmount = 0.3f,
                spatialSigmaScale = 4.5f,
                temporalSigmaScale = 0.5f,
                resetAmount = 0.5f,
            },
            diffuseMaxAccumulatedFrameNum = 20,
            specularMaxAccumulatedFrameNum = 30,
            diffuseMaxFastAccumulatedFrameNum = 4,
            specularMaxFastAccumulatedFrameNum = 6,
            historyFixFrameNum = 3,
            historyFixBasePixelStride = 14,
            historyFixAlternatePixelStride = 14,
            historyFixEdgeStoppingNormalPower = 8,
            fastHistoryClampingSigmaScale = 2,
            diffusePrepassBlurRadius = 30,
            specularPrepassBlurRadius = 50,
            minHitDistanceWeight = 0.1f,
            spatialVarianceEstimationHistoryThreshold = 3,
            diffusePhiLuminance = 2,
            specularPhiLuminance = 1,
            lobeAngleFraction = 0.5f,
            roughnessFraction = 0.15f,
            specularVarianceBoost = 0,
            specularLobeAngleSlack = 0.15f,
            atrousIterationNum = 5,
            diffuseMinLuminanceWeight = 0,
            specularMinLuminanceWeight = 0,
            depthThreshold = 0.003f,
            confidenceDrivenRelaxationMultiplier = 0,
            confidenceDrivenLuminanceEdgeStoppingRelaxation = 0,
            confidenceDrivenNormalEdgeStoppingRelaxation = 0,
            luminanceEdgeStoppingRelaxation = 0.5f,
            normalEdgeStoppingRelaxation = 0.3f,
            roughnessEdgeStoppingRelaxation = 1,
            checkerboardMode = Nrd.CheckerboardMode.OFF,
            hitDistanceReconstructionMode = Nrd.HitDistanceReconstructionMode.OFF,
            minMaterialForDiffuse = 4,
            minMaterialForSpecular = 4,
            enableAntiFirefly = true,
            enableRoughnessEdgeStopping = true,
        };

    public void Dispose() => Nrd.NRD.DestroyInstance(_instance);

    /// <summary>
    /// Gets the view's input and output textures at the traced size.
    /// </summary>
    public static Result<DenoiserTextures> Prepare(in GIView view)
    {
        RenderGraph graph = view.Graph;
        Result<GpuTexture> motion = graph.Texture(view.GetTraced("NRD motion", GpuFormat.R16G16Sfloat));
        Result<GpuTexture> normal = graph.Texture(view.GetTraced("NRD normal", GpuFormat.A2B10G10R10Unorm));
        Result<GpuTexture> viewZ = graph.Texture(view.GetTraced("NRD view depth", GpuFormat.R32Sfloat));
        Result<GpuTexture> radiance = graph.Texture(view.GetTraced("NRD radiance", GpuFormat.R16G16B16A16Sfloat));
        Result<GpuTexture> output = graph.Texture(view.GetTraced("NRD output", GpuFormat.R16G16B16A16Sfloat));
        if (Result.FindFailure(motion, normal, viewZ, radiance, output) is { Ok: false } failed)
            return Result.Failure(failed.Error);

        return Result.Success(
            new DenoiserTextures(motion.Value, normal.Value, viewZ.Value, radiance.Value, output.Value)
        );
    }

    /// <summary>
    /// Records NRD's passes for this frame: its settings from the view's rows, then each of its dispatches.
    /// </summary>
    /// <remarks>
    /// With <paramref name="isRestart"/>, NRD drops its history: a new size, a camera cut or a domain load.
    /// </remarks>
    public Result Record(GIView view, DenoiserTextures textures, uint frameIndex, float deltaSeconds, bool isRestart)
    {
        RenderGraph graph = view.Graph;
        DeferredRendering.View.FrameConstants rows = view.Gpu.Constants;
        (int width, int height) = view.TracedSize;

        // NRD's world is the world itself, not the camera's: the rows' view turns about the camera alone.
        Matrix4x4 worldToView =
            Matrix4x4.CreateTranslation(
                -new Vector3(rows.CameraPosition.X, rows.CameraPosition.Y, rows.CameraPosition.Z)
            ) * rows.View;
        // The trace takes a random pixel of each footprint and does not follow the camera's jitter, so NRD gets the
        // projection without the jitter, and a jitter of 0.
        Matrix4x4 viewToClip = rows.Projection with
        {
            M31 = 0,
            M32 = 0,
        };
        var previous = _previous ?? (worldToView, viewToClip, width, height);
        _previous = (worldToView, viewToClip, width, height);

        Nrd.CommonSettings settings = new()
        {
            viewZScale = 1,
            timeDeltaBetweenFrames = deltaSeconds * 1000,
            denoisingRange = 500000,
            disocclusionThreshold = 0.01f,
            disocclusionThresholdAlternate = 0.05f,
            cameraAttachedReflectionMaterialID = 999,
            strandMaterialID = 999,
            historyFixAlternatePixelStrideMaterialID = 999,
            strandThickness = 80e-6f,
            frameIndex = frameIndex,
            accumulationMode =
                isRestart || previous.Width != width || previous.Height != height
                    ? Nrd.AccumulationMode.CLEAR_AND_RESTART
                    : Nrd.AccumulationMode.CONTINUE,
        };
        Write(settings.viewToClipMatrix, viewToClip);
        Write(settings.viewToClipMatrixPrev, previous.ViewToClip);
        Write(settings.worldToViewMatrix, worldToView);
        Write(settings.worldToViewMatrixPrev, previous.WorldToView);
        Write(settings.worldPrevToWorldMatrix, Matrix4x4.Identity);
        settings.motionVectorScale[0] = 1;
        settings.motionVectorScale[1] = 1;
        settings.resourceSize[0] = settings.rectSize[0] = (ushort)width;
        settings.resourceSize[1] = settings.rectSize[1] = (ushort)height;
        settings.resourceSizePrev[0] = settings.rectSizePrev[0] = (ushort)previous.Width;
        settings.resourceSizePrev[1] = settings.rectSizePrev[1] = (ushort)previous.Height;
        settings.printfAt[0] = settings.printfAt[1] = 9999;
        Nrd.Result set = Nrd.NRD.SetCommonSettings(_instance, &settings);
        if (set != Nrd.Result.SUCCESS)
            return Result.Failure($"NRD took no settings: {set}.");

        uint identifier = Identifier;
        Nrd.DispatchDesc* dispatches;
        uint count;
        Nrd.Result got = Nrd.NRD.GetComputeDispatches(_instance, &identifier, 1, &dispatches, &count);
        if (got != Nrd.Result.SUCCESS)
            return Result.Failure($"NRD gave no passes: {got}.");

        Nrd.InstanceDesc* desc = Nrd.NRD.GetInstanceDesc(_instance);
        Nrd.LibraryDesc* library = Nrd.NRD.GetLibraryDesc();
        Result<GpuSampler> nearest = graph.Sampler(
            new SamplerDescription { Filter = TextureFilter.Nearest, Wrap = TextureWrap.Clamp }
        );
        Result<GpuSampler> linear = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        if (Result.FindFailure(nearest, linear) is { Ok: false } noSampler)
            return Result.Failure(noSampler.Error);

        for (int i = 0; i < count; i++)
        {
            Nrd.DispatchDesc dispatch = dispatches[i];
            Nrd.PipelineDesc pipeline = desc->pipelines[dispatch.pipelineIndex];
            ImmutableArray<GpuBinding>.Builder bindings = ImmutableArray.CreateBuilder<GpuBinding>();
            uint cbSet = desc->constantBufferAndSamplersSpaceIndex;
            for (uint s = 0; s < desc->samplersNum; s++)
            {
                GpuSampler sampler = desc->samplers[s] == Nrd.Sampler.NEAREST_CLAMP ? nearest.Value : linear.Value;
                uint slot = library->spirvBindingOffsets.samplerOffset + desc->samplersBaseRegisterIndex + s;
                bindings.Add(new GpuBinding(cbSet, slot, GpuBindingKind.Sampler, sampler.Id));
            }

            if (pipeline.hasConstantData)
            {
                int bytes = (int)dispatch.constantBufferDataSize;
                GpuUpload upload = _gpu.BeginUpload(bytes + ConstantsAlignment);
                if (upload.Bytes.IsEmpty)
                    return Result.Failure("the staging ring has no room for NRD's constants.");

                int skip = (int)(
                    ((upload.Offset + ConstantsAlignment - 1) / ConstantsAlignment * ConstantsAlignment) - upload.Offset
                );
                new ReadOnlySpan<byte>(dispatch.constantBufferData, bytes).CopyTo(upload.Bytes[skip..]);
                uint slot = library->spirvBindingOffsets.constantBufferOffset + desc->constantBufferRegisterIndex;
                bindings.Add(
                    new GpuBinding(
                        cbSet,
                        slot,
                        GpuBindingKind.Constants,
                        upload.Buffer.Id,
                        upload.Offset + (ulong)skip,
                        (ulong)bytes
                    )
                );
            }

            // The resources, in the order of the pipeline's ranges: its textures, then its storage textures.
            uint reads = 0;
            uint writes = 0;
            for (int r = 0; r < dispatch.resourcesNum; r++)
            {
                Nrd.ResourceDesc resource = dispatch.resources[r];
                Result<GpuTexture> texture = GetTexture(resource);
                if (!texture.Ok)
                    return Result.Failure(texture.Error);

                bool isStorage = resource.descriptorType == Nrd.DescriptorType.STORAGE_TEXTURE;
                uint slot = isStorage
                    ? library->spirvBindingOffsets.storageTextureAndBufferOffset
                        + desc->resourcesBaseRegisterIndex
                        + writes++
                    : library->spirvBindingOffsets.textureOffset + desc->resourcesBaseRegisterIndex + reads++;
                bindings.Add(
                    new GpuBinding(
                        desc->resourcesSpaceIndex,
                        slot,
                        isStorage ? GpuBindingKind.StorageTexture : GpuBindingKind.Texture,
                        texture.Value.Id
                    )
                );
            }

            string name = Marshal.PtrToStringUTF8((nint)dispatch.name) ?? "NRD";
            graph
                .Compute(
                    name,
                    new BoundShader
                    {
                        Spirv = _spirv[dispatch.pipelineIndex],
                        EntryPoint = _entryPoint,
                        Bindings = bindings.ToImmutable(),
                    },
                    RenderStages.Light
                )
                .Dispatch(dispatch.gridWidth, dispatch.gridHeight);
        }

        return Result.Success();

        Result<GpuTexture> GetTexture(Nrd.ResourceDesc resource) =>
            resource.type switch
            {
                Nrd.ResourceType.IN_MV => Result.Success(textures.Motion),
                Nrd.ResourceType.IN_NORMAL_ROUGHNESS => Result.Success(textures.NormalRoughness),
                Nrd.ResourceType.IN_VIEWZ => Result.Success(textures.ViewZ),
                Nrd.ResourceType.IN_DIFF_RADIANCE_HITDIST => Result.Success(textures.Radiance),
                Nrd.ResourceType.OUT_DIFF_RADIANCE_HITDIST => Result.Success(textures.Output),
                Nrd.ResourceType.TRANSIENT_POOL => GetPooled(
                    graph,
                    view.TracedSize,
                    "NRD transient",
                    desc->transientPool[resource.indexInPool],
                    resource.indexInPool
                ),
                Nrd.ResourceType.PERMANENT_POOL => GetPooled(
                    graph,
                    view.TracedSize,
                    "NRD permanent",
                    desc->permanentPool[resource.indexInPool],
                    resource.indexInPool
                ),
                _ => Result.Failure($"NRD asks for {resource.type}, which the diffuse light does not give."),
            };
    }

    private static Result<GpuTexture> GetPooled(
        RenderGraph graph,
        (int Width, int Height) size,
        string pool,
        Nrd.TextureDesc texture,
        int index
    )
    {
        GpuFormat format = texture.format switch
        {
            Nrd.Format.R8_UNORM => GpuFormat.R8Unorm,
            Nrd.Format.RGBA8_UNORM => GpuFormat.R8G8B8A8Unorm,
            Nrd.Format.RG16_SFLOAT => GpuFormat.R16G16Sfloat,
            Nrd.Format.RGBA16_SFLOAT => GpuFormat.R16G16B16A16Sfloat,
            Nrd.Format.R32_SFLOAT => GpuFormat.R32Sfloat,
            Nrd.Format.R32_UINT => GpuFormat.R32Uint,
            Nrd.Format.R10_G10_B10_A2_UNORM => GpuFormat.A2B10G10R10Unorm,
            _ => GpuFormat.Unknown,
        };
        if (format == GpuFormat.Unknown)
            return Result.Failure($"NRD's pool asks for {texture.format}, which Magic has no format for.");

        int down = Math.Max((int)texture.downsampleFactor, 1);
        (int width, int height) = size;

        return graph.Texture(
            new TextureDescription($"{pool} {index}", (width + down - 1) / down, (height + down - 1) / down, format)
        );
    }

    private static void Write(Span<float> to, Matrix4x4 matrix) =>
        MemoryMarshal.CreateReadOnlySpan(ref matrix.M11, 16).CopyTo(to);
}
