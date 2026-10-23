using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using static Magic.DxcShaderCompiler.Spvc;
using static Magic.DxcShaderCompiler.spvc_backend;
using static Magic.DxcShaderCompiler.spvc_capture_mode;
using static Magic.DxcShaderCompiler.spvc_resource_type;
using static Magic.DxcShaderCompiler.spvc_result;
using static Magic.DxcShaderCompiler.SpvDecoration_;
using static Magic.DxcShaderCompiler.SpvExecutionMode_;

namespace Magic.DxcShaderCompiler;

/// <summary>
/// What a SPIR-V module binds, read by SPIRV-Cross: its constant block's size (its push constant block's) and a compute
/// module's group size. Every resource it declares must be the heap's (Shaders/Include/Heap.hlsli), an array of
/// resources in set 0, as the heap is all a GPU binds.
/// </summary>
internal static unsafe class SpirvBindings
{
    /// <summary>
    /// The lists SPIRV-Cross sorts a module's resources into. A module's stage inputs and outputs are not resources it
    /// binds, and are not read; its push constants are its constant block.
    /// </summary>
    private static readonly spvc_resource_type[] _resources =
    [
        SPVC_RESOURCE_TYPE_UNIFORM_BUFFER,
        SPVC_RESOURCE_TYPE_STORAGE_BUFFER,
        SPVC_RESOURCE_TYPE_STORAGE_IMAGE,
        SPVC_RESOURCE_TYPE_SEPARATE_IMAGE,
        SPVC_RESOURCE_TYPE_SEPARATE_SAMPLERS,
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
    /// The shader <paramref name="spirv"/> is, compiled for <paramref name="stage"/>; why not, when it declares a
    /// resource outside the heap.
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

            foreach (spvc_resource_type list in _resources)
            {
                spvc_reflected_resource* resource = GetList(resources, list, out nuint count);
                for (nuint i = 0; i < count; i++)
                {
                    if (!IsInHeap(compiler, resource[i]))
                    {
                        return Result.Failure<GpuShader>(
                            $"{GetName(compiler, resource[i].id)} is declared outside the heap: read it through the "
                            + "heap (Shaders/Include/Heap.hlsli).");
                    }
                }
            }

            return Result.Success(new GpuShader
            {
                Code = spirv,
                EntryPoint = "main",
                Stage = stage,
                ConstantBytes = GetConstantBytes(compiler, resources),
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
    /// The size of the module's push constant block, or 0 for none.
    /// </summary>
    private static uint GetConstantBytes(spvc_compiler_s* compiler, spvc_resources_s* resources)
    {
        spvc_reflected_resource* block = GetList(resources, SPVC_RESOURCE_TYPE_PUSH_CONSTANT, out nuint count);

        if (count == 0) return 0;

        nuint size;
        spvc_type_s* type = spvc_compiler_get_type_handle(compiler, block[0].base_type_id);

        return spvc_compiler_get_declared_struct_size(compiler, type, &size) == SPVC_SUCCESS ? (uint)size : 0;
    }

    /// <summary>
    /// Whether a resource is one of the heap's arrays, in set 0.
    /// </summary>
    private static bool IsInHeap(spvc_compiler_s* compiler, in spvc_reflected_resource resource)
        => spvc_type_get_num_array_dimensions(spvc_compiler_get_type_handle(compiler, resource.type_id)) > 0
            && spvc_compiler_get_decoration(compiler, resource.id, SpvDecorationDescriptorSet) == 0;

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
    /// A compute module's group size along one axis (its LocalSize), and 1 for a module that has none.
    /// </summary>
    private static uint GetGroupSize(spvc_compiler_s* compiler, uint axis)
        => Math.Max(spvc_compiler_get_execution_mode_argument_by_index(compiler, SpvExecutionModeLocalSize, axis), 1u);
}
