using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// SPIR-V to Metal Shading Language, through SPIRV-Cross. Metal has no descriptor sets, so each resource is given the
/// texture, sampler or buffer index the GPU gem's Metal backend expects of its set and binding: textures and samplers
/// number on from 0, uniform buffers from 0, and storage buffers after the uniform buffers, each stage's own; a
/// compute shader's read-only resources come before its read-write ones. The entry point becomes <c>main0</c>, as
/// Metal has no <c>main</c>.
/// </summary>
internal static unsafe class Metal
{
    /// <summary>
    /// Metal Shading Language 2.3, the lowest with everything the shaders use, so the oldest Macs still run them;
    /// SPIRV-Cross counts a version as major × 10000 + minor × 100 + patch.
    /// </summary>
    private const uint Version = (2 * 10000) + (3 * 100);

    public static Result<byte[]> Translate(byte[] spirv, string name, GpuStage stage)
    {
        Spvc.Context* context;
        if (Spvc.ContextCreate(&context) != Spvc.Result.Success)
        {
            return Result.Failure<byte[]>($"{name}: SPIRV-Cross could not start.");
        }

        try
        {
            fixed (byte* code = spirv)
            {
                Spvc.ParsedIr* ir;
                Spvc.Compiler* compiler;
                Spvc.CompilerOptions* options;
                if (Spvc.ContextParseSpirv(context, (uint*)code, (ulong)(spirv.Length / 4), &ir) != Spvc.Result.Success
                    || Spvc.ContextCreateCompiler(context, Spvc.Backend.Msl, ir, Spvc.CaptureMode.TakeOwnership, &compiler) != Spvc.Result.Success
                    || Spvc.CompilerCreateCompilerOptions(compiler, &options) != Spvc.Result.Success
                    || Spvc.CompilerOptionsSetUint(options, Spvc.CompilerOption.MslVersion, Version) != Spvc.Result.Success)
                {
                    return Failed(context, name);
                }

                Result bound = Bind(compiler, stage);
                if (!bound.Ok)
                {
                    return Result.Failure<byte[]>($"{name}: {bound.Error}");
                }

                byte* source;
                if (Spvc.CompilerInstallCompilerOptions(compiler, options) != Spvc.Result.Success || Spvc.CompilerCompile(compiler, &source) != Spvc.Result.Success)
                {
                    return Failed(context, name);
                }

                // The text belongs to the context, which is destroyed below.
                return Result.Success(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(source).ToArray());
            }
        }
        finally
        {
            Spvc.ContextDestroy(context);
        }
    }

    private static Result<byte[]> Failed(Spvc.Context* context, string name)
    {
        return Result.Failure<byte[]>($"{name}: SPIR-V could not be translated to Metal: {CString.Read(Spvc.ContextGetLastErrorString(context))}");
    }

    /// <summary>
    /// Gives every resource the shader uses its Metal index from its set and binding, in the layout the GPU gem lays
    /// its resources out in: a graphics stage has its textures and storage textures in set 0 (vertex) or 2 (fragment),
    /// its uniform buffers in set 1 or 3 and its storage buffers with the textures; a compute shader has read-only
    /// textures and buffers in set 0, read-write ones in set 1 and uniform buffers in set 2.
    /// </summary>
    private static Result Bind(Spvc.Compiler* compiler, GpuStage stage)
    {
        bool compute = stage == GpuStage.Compute;
        Spv.ExecutionModel model = compute ? Spv.ExecutionModel.GLCompute : stage == GpuStage.Vertex ? Spv.ExecutionModel.Vertex : Spv.ExecutionModel.Fragment;
        Spvc.Set* active;
        Spvc.Resources* resources;
        if (Spvc.CompilerGetActiveInterfaceVariables(compiler, &active) != Spvc.Result.Success
            || Spvc.CompilerCreateShaderResourcesForActiveVariables(compiler, &resources, active) != Spvc.Result.Success)
        {
            return Result.Failure("SPIRV-Cross could not reflect the shader's resources.");
        }

        // Textures: texture-samplers (HLSL's separate samplers stand for them), storage textures, and the separate
        // images past the samplers, which are storage textures HLSL left unpaired.
        List<(uint Set, uint Binding)> textures = [];
        List<(uint Set, uint Binding)> buffers = [];
        Spvc.ResourceType samplers = Spvc.ResourceType.SampledImage;
        ulong separateSamplers = 0;
        if (Count(resources, samplers) == 0)
        {
            samplers = Spvc.ResourceType.SeparateSamplers;
            separateSamplers = Count(resources, samplers);
        }

        uint[] textureSets = compute ? [0u, 1u] : [0u, 2u];
        Result collected = Collect(compiler, resources, samplers, 0, textures, compute ? [0u] : [0u, 2u], "texture-sampler");
        if (collected.Ok)
        {
            collected = Collect(compiler, resources, Spvc.ResourceType.StorageImage, 0, textures, textureSets, "storage texture");
        }

        if (collected.Ok)
        {
            collected = Collect(compiler, resources, Spvc.ResourceType.SeparateImage, separateSamplers, textures, textureSets, "storage texture");
        }

        if (collected.Ok)
        {
            collected = Collect(compiler, resources, Spvc.ResourceType.UniformBuffer, 0, buffers, compute ? [2u] : [1u, 3u], "uniform buffer");
        }

        if (collected.Ok)
        {
            collected = Collect(compiler, resources, Spvc.ResourceType.StorageBuffer, 0, buffers, textureSets, "storage buffer");
        }

        if (!collected.Ok)
        {
            return collected;
        }

        uint readOnlyTextures = 0;
        uint readWriteTextures = 0;
        foreach ((uint set, _) in textures)
        {
            if (set == 0)
            {
                readOnlyTextures++;
            }
            else if (set == 1)
            {
                readWriteTextures++;
            }
        }

        uint uniformBuffers = 0;
        uint readOnlyBuffers = 0;
        foreach ((uint set, _) in buffers)
        {
            if (compute ? set == 2 : set is 1 or 3)
            {
                uniformBuffers++;
            }
            else if (set == 0)
            {
                readOnlyBuffers++;
            }
        }

        foreach ((uint set, uint binding) in textures)
        {
            uint index = compute && set == 1 ? readOnlyTextures + binding : binding;
            if (!Add(compiler, model, set, binding, texture: index, buffer: 0))
            {
                return Result.Failure("SPIRV-Cross refused a texture binding.");
            }
        }

        foreach ((uint set, uint binding) in buffers)
        {
            uint index;
            if (compute ? set == 2 : set is 1 or 3)
            {
                index = binding;
            }
            else if (compute && set == 1)
            {
                index = uniformBuffers + readOnlyBuffers + (binding - readWriteTextures);
            }
            else
            {
                // Textures precede the storage buffers in the set.
                index = uniformBuffers + (binding - (compute ? readOnlyTextures : (uint)textures.Count));
            }

            if (!Add(compiler, model, set, binding, texture: 0, buffer: index))
            {
                return Result.Failure("SPIRV-Cross refused a buffer binding.");
            }
        }

        return Result.Success();
    }

    private static Spvc.ReflectedResource* ListOf(Spvc.Resources* resources, Spvc.ResourceType type, out ulong count)
    {
        Spvc.ReflectedResource* list;
        ulong found;
        if (Spvc.ResourcesGetResourceListForType(resources, type, &list, &found) != Spvc.Result.Success)
        {
            count = 0;
            return null;
        }

        count = found;
        return list;
    }

    private static ulong Count(Spvc.Resources* resources, Spvc.ResourceType type)
    {
        ListOf(resources, type, out ulong count);
        return count;
    }

    /// <summary>
    /// Adds the set and binding of each resource of <paramref name="type"/> from the <paramref name="first"/> on,
    /// refusing one outside the <paramref name="sets"/> its kind may be in.
    /// </summary>
    private static Result Collect(Spvc.Compiler* compiler, Spvc.Resources* resources, Spvc.ResourceType type, ulong first, List<(uint Set, uint Binding)> into, uint[] sets, string kind)
    {
        Spvc.ReflectedResource* list = ListOf(resources, type, out ulong count);
        for (ulong i = first; i < count; i++)
        {
            uint id = list[i].Id;
            if (Spvc.CompilerHasDecoration(compiler, id, Spv.Decoration.DescriptorSet) == 0 || Spvc.CompilerHasDecoration(compiler, id, Spv.Decoration.Binding) == 0)
            {
                return Result.Failure($"its {kind} {CString.Read(list[i].Name)} has no descriptor set and binding.");
            }

            uint set = Spvc.CompilerGetDecoration(compiler, id, Spv.Decoration.DescriptorSet);
            if (Array.IndexOf(sets, set) < 0)
            {
                return Result.Failure($"its {kind} {CString.Read(list[i].Name)} is in descriptor set {set}, where Metal's layout has no {kind}.");
            }

            into.Add((set, Spvc.CompilerGetDecoration(compiler, id, Spv.Decoration.Binding)));
        }

        return Result.Success();
    }

    private static bool Add(Spvc.Compiler* compiler, Spv.ExecutionModel model, uint set, uint binding, uint texture, uint buffer)
    {
        Spvc.MslResourceBinding2 resource;
        Spvc.MslResourceBindingInit2(&resource);
        resource.Stage = model;
        resource.DescSet = set;
        resource.Binding = binding;
        resource.Count = 1;
        resource.MslTexture = texture;
        resource.MslSampler = texture;
        resource.MslBuffer = buffer;
        return Spvc.CompilerMslAddResourceBinding2(compiler, &resource) == Spvc.Result.Success;
    }
}
