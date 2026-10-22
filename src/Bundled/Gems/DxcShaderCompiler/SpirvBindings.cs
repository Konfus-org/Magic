using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using static Magic.DxcShaderCompiler.Spvc;
using static Magic.DxcShaderCompiler.spvc_backend;
using static Magic.DxcShaderCompiler.spvc_basetype;
using static Magic.DxcShaderCompiler.spvc_capture_mode;
using static Magic.DxcShaderCompiler.spvc_resource_type;
using static Magic.DxcShaderCompiler.spvc_result;
using static Magic.DxcShaderCompiler.SpvDecoration_;
using static Magic.DxcShaderCompiler.SpvExecutionMode_;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// What a SPIR-V module binds, read by SPIRV-Cross: each resource variable the module declares, by its name, what it
/// is (an image sampled or not, or of integers, which no GPU samples, a sampler, a uniform block or a buffer) and where
/// it is (its DescriptorSet and Binding decorations), in the binding model of
/// Shaders/Include/Bindings.hlsli. A set is a stage's read, write or uniform space, and a binding is a register of it.
/// A sampler is the sampled texture's at its binding, and is not listed. A compute module's group size comes with them.
/// </summary>
internal static unsafe class SpirvBindings
{
    /// <summary>
    /// The lists SPIRV-Cross sorts a module's resources into that the binding model binds, and what each holds.
    /// </summary>
    private static readonly (spvc_resource_type List, Resource Resource)[] _bound =
    [
        (SPVC_RESOURCE_TYPE_UNIFORM_BUFFER, Resource.Block),
        (SPVC_RESOURCE_TYPE_STORAGE_BUFFER, Resource.Buffer),
        (SPVC_RESOURCE_TYPE_STORAGE_IMAGE, Resource.StorageImage),
        (SPVC_RESOURCE_TYPE_SEPARATE_IMAGE, Resource.Texture),
        (SPVC_RESOURCE_TYPE_SEPARATE_SAMPLERS, Resource.Sampler),
    ];

    /// <summary>
    /// The lists of resources the binding model has no place for: an image and sampler combined, which HLSL never
    /// makes, and what only other languages or ray tracing declare. A module's stage inputs and outputs and its push
    /// constants are not resources it binds, and are not read.
    /// </summary>
    private static readonly spvc_resource_type[] _unbound =
    [
        SPVC_RESOURCE_TYPE_SAMPLED_IMAGE,
        SPVC_RESOURCE_TYPE_SUBPASS_INPUT,
        SPVC_RESOURCE_TYPE_ATOMIC_COUNTER,
        SPVC_RESOURCE_TYPE_ACCELERATION_STRUCTURE,
        SPVC_RESOURCE_TYPE_RAY_QUERY,
        SPVC_RESOURCE_TYPE_SHADER_RECORD_BUFFER,
        SPVC_RESOURCE_TYPE_GL_PLAIN_UNIFORM,
        SPVC_RESOURCE_TYPE_TENSOR,
    ];

    /// <summary>
    /// The shader <paramref name="spirv"/> is, compiled for <paramref name="stage"/>, with what it binds; why not, when
    /// it binds something the binding model has no place for.
    /// </summary>
    internal static Result<GpuShader> Read(ReadOnlyMemory<byte> spirv, GpuStage stage)
    {
        spvc_context_s* context;

        if (spvc_context_create(&context) != SPVC_SUCCESS)
            return Result.Failure<GpuShader>("SPIRV-Cross could not start.");

        try
        {
            spvc_compiler_s* compiler;
            spvc_resources_s* resources;
            fixed (byte* code = spirv.Span)
            {
                spvc_parsed_ir_s* ir;

                if (spvc_context_parse_spirv(context, (uint*)code, (nuint)(spirv.Length / 4), &ir) != SPVC_SUCCESS
                    || spvc_context_create_compiler(
                        context,
                        SPVC_BACKEND_NONE,
                        ir,
                        SPVC_CAPTURE_MODE_TAKE_OWNERSHIP,
                        &compiler) != SPVC_SUCCESS
                    || spvc_compiler_create_shader_resources(compiler, &resources) != SPVC_SUCCESS)
                {
                    return Result.Failure<GpuShader>(CString.Read(spvc_context_get_last_error_string(context)));
                }
            }

            foreach (spvc_resource_type list in _unbound)
            {
                spvc_reflected_resource* unbound = GetList(resources, list, out nuint count);

                if (count > 0)
                {
                    return Result.Failure<GpuShader>(
                        $"{GetName(compiler, unbound[0].id)} is of a type the binding model does not bind.");
                }
            }

            List<(uint Id, GpuShaderBinding Binding)> found = [];
            Result read = ReadBindings(compiler, resources, stage, found);

            if (!read.Ok) return Result.Failure<GpuShader>(read.Error);

            // In the order the module declares them, as each list is.
            found.Sort((first, second) => first.Id.CompareTo(second.Id));

            return Result.Success(new GpuShader
            {
                Code = spirv,
                EntryPoint = "main",
                Stage = stage,
                Bindings = found.ConvertAll(entry => entry.Binding),
                ThreadCountX = GetGroupSize(compiler, 0),
                ThreadCountY = GetGroupSize(compiler, 1),
                ThreadCountZ = GetGroupSize(compiler, 2),
            });
        }
        finally
        {
            spvc_context_destroy(context);
        }
    }

    /// <summary>
    /// Adds every resource of the lists the binding model binds to <paramref name="found"/>, with the variable it is;
    /// a sampler is checked against the texture whose it is instead.
    /// </summary>
    private static Result ReadBindings(
        spvc_compiler_s* compiler,
        spvc_resources_s* resources,
        GpuStage stage,
        List<(uint Id, GpuShaderBinding Binding)> found)
    {
        HashSet<uint> samplers = [];
        foreach ((spvc_resource_type list, Resource listed) in _bound)
        {
            spvc_reflected_resource* resource = GetList(resources, list, out nuint count);
            for (nuint i = 0; i < count; i++)
            {
                uint id = resource[i].id;
                string name = GetName(compiler, id);

                if (name.Length == 0) return Result.Failure("a resource has no name.");

                spvc_type_s* type = spvc_compiler_get_type_handle(compiler, resource[i].type_id);

                if (spvc_type_get_num_array_dimensions(type) > 0)
                    return Result.Failure($"{name} is an array of resources, which the binding model does not bind.");

                uint binding = spvc_compiler_get_decoration(compiler, id, SpvDecorationBinding);
                if (listed == Resource.Sampler)
                {
                    samplers.Add(binding);

                    continue;
                }

                // An image of integers cannot be sampled, so the binding model reads it as a storage texture.
                Resource what = listed == Resource.Texture && IsOfIntegers(compiler, resource[i].base_type_id)
                    ? Resource.StorageImage
                    : listed;
                uint set = spvc_compiler_get_decoration(compiler, id, SpvDecorationDescriptorSet);
                Result<GpuBindingKind> kind = TryGetKind(stage, set, what, name);

                if (!kind.Ok) return Result.Failure(kind.Error);

                found.Add((id, new GpuShaderBinding(name, kind.Value, binding)));
            }
        }

        foreach (uint binding in samplers)
        {
            if (!found.Exists(texture => texture.Binding.Kind == GpuBindingKind.Sampled
                && texture.Binding.Register == binding))
            {
                return Result.Failure(
                    $"a sampler at {binding} has no sampled texture at {binding}, whose it would be.");
            }
        }

        return Result.Success();
    }

    private static spvc_reflected_resource* GetList(
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
    /// The name the module gives a variable (its OpName), or empty.
    /// </summary>
    private static string GetName(spvc_compiler_s* compiler, uint id)
        => CString.Read(spvc_compiler_get_name(compiler, id));

    /// <summary>
    /// Whether an image's texels are integers, read from the type it samples.
    /// </summary>
    private static bool IsOfIntegers(spvc_compiler_s* compiler, uint image)
    {
        spvc_type_s* sampled = spvc_compiler_get_type_handle(
            compiler,
            spvc_type_get_image_sampled_type(spvc_compiler_get_type_handle(compiler, image)));

        return spvc_type_get_basetype(sampled) is SPVC_BASETYPE_INT8 or SPVC_BASETYPE_UINT8 or SPVC_BASETYPE_INT16
            or SPVC_BASETYPE_UINT16 or SPVC_BASETYPE_INT32 or SPVC_BASETYPE_UINT32 or SPVC_BASETYPE_INT64
            or SPVC_BASETYPE_UINT64;
    }

    /// <summary>
    /// A compute module's group size along one axis (its LocalSize), and 1 for a module that has none.
    /// </summary>
    private static uint GetGroupSize(spvc_compiler_s* compiler, uint axis)
        => Math.Max(spvc_compiler_get_execution_mode_argument_by_index(compiler, SpvExecutionModeLocalSize, axis), 1u);

    /// <summary>
    /// What a resource in a set is to a stage, by the space the set is (Shaders/Include/Bindings.hlsli): a read space
    /// holds sampled textures, storage textures and storage buffers; compute's write space what it writes; the uniform
    /// space its one constant block.
    /// </summary>
    private static Result<GpuBindingKind> TryGetKind(GpuStage stage, uint set, Resource resource, string name)
    {
        // The sets the binding header gives the stage's read, write and uniform spaces; a vertex or fragment stage has
        // no write space.
        (uint read, uint write, uint uniform) = stage switch
        {
            GpuStage.Vertex => (0u, uint.MaxValue, 1u),
            GpuStage.Fragment => (2u, uint.MaxValue, 3u),
            GpuStage.Compute => (0u, 1u, 2u),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "There is no such shader stage."),
        };

        if (set == uniform && resource == Resource.Block) return Result.Success(GpuBindingKind.Uniform);

        if (set == read)
        {
            return resource switch
            {
                Resource.Texture => Result.Success(GpuBindingKind.Sampled),
                Resource.StorageImage => Result.Success(GpuBindingKind.StorageTexture),
                Resource.Buffer => Result.Success(GpuBindingKind.StorageBuffer),
                Resource.Sampler or Resource.Block => Result.Failure<GpuBindingKind>(
                    $"{name} is read as what the binding model does not read."),
                _ => Result.Failure<GpuBindingKind>($"{name} is read as what the binding model does not read."),
            };
        }

        if (set == write)
        {
            return resource switch
            {
                Resource.StorageImage => Result.Success(GpuBindingKind.ReadWriteStorageTexture),
                Resource.Buffer => Result.Success(GpuBindingKind.ReadWriteStorageBuffer),
                Resource.Texture or Resource.Sampler or Resource.Block => Result.Failure<GpuBindingKind>(
                    $"{name} is written as what the binding model does not write."),
                _ => Result.Failure<GpuBindingKind>($"{name} is written as what the binding model does not write."),
            };
        }

        return Result.Failure<GpuBindingKind>($"{name} is in space {set}, which the stage has no use for.");
    }

    private enum Resource : byte
    {
        Texture,
        StorageImage,
        Sampler,
        Buffer,
        Block,
    }
}
