using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using static Magic.DxcShaderCompiler.Spvc;
using static Magic.DxcShaderCompiler.spvc_backend;
using static Magic.DxcShaderCompiler.spvc_capture_mode;
using static Magic.DxcShaderCompiler.spvc_compiler_option;
using static Magic.DxcShaderCompiler.spvc_resource_type;
using static Magic.DxcShaderCompiler.spvc_result;
using static Magic.DxcShaderCompiler.SpvDecoration_;
using static Magic.DxcShaderCompiler.SpvExecutionModel_;

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

    internal static Result<byte[]> Translate(byte[] spirv, string name, GpuStage stage)
    {
        spvc_context_s* context;
        if (spvc_context_create(&context) != SPVC_SUCCESS)
        {
            return Result.Failure<byte[]>($"{name}: SPIRV-Cross could not start.");
        }

        try
        {
            fixed (byte* code = spirv)
            {
                spvc_parsed_ir_s* ir;
                spvc_compiler_s* compiler;
                spvc_compiler_options_s* options;
                if (spvc_context_parse_spirv(context, (uint*)code, (nuint)(spirv.Length / 4), &ir) != SPVC_SUCCESS
                    || spvc_context_create_compiler(
                        context,
                        SPVC_BACKEND_MSL,
                        ir,
                        SPVC_CAPTURE_MODE_TAKE_OWNERSHIP,
                        &compiler) != SPVC_SUCCESS
                    || spvc_compiler_create_compiler_options(compiler, &options) != SPVC_SUCCESS
                    || spvc_compiler_options_set_uint(
                        options,
                        SPVC_COMPILER_OPTION_MSL_VERSION,
                        Version) != SPVC_SUCCESS)
                {
                    return GetFailure(context, name);
                }

                Result bound = Bind(compiler, stage);
                if (!bound.Ok) return Result.Failure<byte[]>($"{name}: {bound.Error}");

                byte* source;
                if (spvc_compiler_install_compiler_options(compiler, options) != SPVC_SUCCESS
                    || spvc_compiler_compile(compiler, &source) != SPVC_SUCCESS)
                {
                    return GetFailure(context, name);
                }

                // The text belongs to the context, which is destroyed below.
                return Result.Success(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(source).ToArray());
            }
        }
        finally
        {
            spvc_context_destroy(context);
        }
    }

    private static Result<byte[]> GetFailure(spvc_context_s* context, string name)
    {
        string error = CString.Read(spvc_context_get_last_error_string(context));
        return Result.Failure<byte[]>($"{name}: SPIR-V could not be translated to Metal: {error}");
    }

    /// <summary>
    /// Gives every resource the shader uses its Metal index from its set and binding, in the layout the GPU gem lays
    /// its resources out in: a graphics stage has its textures and storage textures in set 0 (vertex) or 2 (fragment),
    /// its uniform buffers in set 1 or 3 and its storage buffers with the textures; a compute shader has read-only
    /// textures and buffers in set 0, read-write ones in set 1 and uniform buffers in set 2.
    /// </summary>
    private static Result Bind(spvc_compiler_s* compiler, GpuStage stage)
    {
        bool compute = stage == GpuStage.Compute;
        SpvExecutionModel_ model = (compute, stage) switch
        {
            (true, _) => SpvExecutionModelGLCompute,
            (false, GpuStage.Vertex) => SpvExecutionModelVertex,
            _ => SpvExecutionModelFragment,
        };

        spvc_set_s* active;
        spvc_resources_s* resources;
        if (spvc_compiler_get_active_interface_variables(compiler, &active) != SPVC_SUCCESS
            || spvc_compiler_create_shader_resources_for_active_variables(
                compiler,
                &resources,
                active) != SPVC_SUCCESS)
        {
            return Result.Failure("SPIRV-Cross could not reflect the shader's resources.");
        }

        // Textures: texture-samplers (HLSL's separate samplers stand for them), storage textures, and the separate
        // images past the samplers, which are storage textures HLSL left unpaired.
        List<(uint Set, uint Binding)> textures = [];
        List<(uint Set, uint Binding)> buffers = [];
        spvc_resource_type samplers = SPVC_RESOURCE_TYPE_SAMPLED_IMAGE;
        nuint separateSamplers = 0;
        ListOf(resources, samplers, out nuint sampledImages);
        if (sampledImages == 0)
        {
            samplers = SPVC_RESOURCE_TYPE_SEPARATE_SAMPLERS;
            ListOf(resources, samplers, out separateSamplers);
        }

        uint[] storageSets = compute ? [0u, 1u] : [0u, 2u];
        Result collected = Collect(
            compiler,
            resources,
            samplers,
            0,
            textures,
            compute ? [0u] : [0u, 2u],
            "texture-sampler");
        if (collected.Ok)
        {
            collected = Collect(
                compiler,
                resources,
                SPVC_RESOURCE_TYPE_STORAGE_IMAGE,
                0,
                textures,
                storageSets,
                "storage texture");
        }

        if (collected.Ok)
        {
            collected = Collect(
                compiler,
                resources,
                SPVC_RESOURCE_TYPE_SEPARATE_IMAGE,
                separateSamplers,
                textures,
                storageSets,
                "storage texture");
        }

        if (collected.Ok)
        {
            collected = Collect(
                compiler,
                resources,
                SPVC_RESOURCE_TYPE_UNIFORM_BUFFER,
                0,
                buffers,
                compute ? [2u] : [1u, 3u],
                "uniform buffer");
        }

        if (collected.Ok)
        {
            collected = Collect(
                compiler,
                resources,
                SPVC_RESOURCE_TYPE_STORAGE_BUFFER,
                0,
                buffers,
                storageSets,
                "storage buffer");
        }

        if (!collected.Ok) return collected;

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

    private static spvc_reflected_resource* ListOf(
        spvc_resources_s* resources,
        spvc_resource_type type,
        out nuint count)
    {
        spvc_reflected_resource* list;
        nuint found;
        if (spvc_resources_get_resource_list_for_type(resources, type, &list, &found) != SPVC_SUCCESS)
        {
            count = 0;
            return null;
        }

        count = found;
        return list;
    }

    /// <summary>
    /// Adds the set and binding of each resource of <paramref name="type"/> from the <paramref name="first"/> on,
    /// refusing one outside the <paramref name="sets"/> its kind may be in.
    /// </summary>
    private static Result Collect(
        spvc_compiler_s* compiler,
        spvc_resources_s* resources,
        spvc_resource_type type,
        nuint first,
        List<(uint Set, uint Binding)> into,
        uint[] sets,
        string kind)
    {
        spvc_reflected_resource* list = ListOf(resources, type, out nuint count);
        for (nuint i = first; i < count; i++)
        {
            uint id = list[i].id;
            if (spvc_compiler_has_decoration(compiler, id, SpvDecorationDescriptorSet) == 0
                || spvc_compiler_has_decoration(compiler, id, SpvDecorationBinding) == 0)
            {
                return Result.Failure($"its {kind} {CString.Read(list[i].name)} has no descriptor set and binding.");
            }

            uint set = spvc_compiler_get_decoration(compiler, id, SpvDecorationDescriptorSet);
            if (Array.IndexOf(sets, set) < 0)
            {
                return Result.Failure($"its {kind} {CString.Read(list[i].name)} is in descriptor set {set}, "
                    + $"where Metal's layout has no {kind}.");
            }

            into.Add((set, spvc_compiler_get_decoration(compiler, id, SpvDecorationBinding)));
        }

        return Result.Success();
    }

    private static bool Add(
        spvc_compiler_s* compiler,
        SpvExecutionModel_ model,
        uint set,
        uint binding,
        uint texture,
        uint buffer)
    {
        spvc_msl_resource_binding_2 resource;
        spvc_msl_resource_binding_init_2(&resource);
        resource.stage = model;
        resource.desc_set = set;
        resource.binding = binding;
        resource.count = 1;
        resource.msl_texture = texture;
        resource.msl_sampler = texture;
        resource.msl_buffer = buffer;
        return spvc_compiler_msl_add_resource_binding_2(compiler, &resource) == SPVC_SUCCESS;
    }
}
