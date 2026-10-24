using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using static Magic.SlangShaderCompiler.Slang;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// What a program Slang linked takes, read from its reflection: its entry point's stage, its constant block's size (its
/// push constant block's) and a compute stage's group size. Every resource it declares must be the heap's
/// (Shaders/Core/Heap.slang), an array of resources in set 0, as the heap is all a GPU binds, and its entry point
/// takes no resource of its own.
/// </summary>
internal static unsafe class ShaderReflection
{
    /// <summary>
    /// The shader <paramref name="code"/> is, its program's reflection <paramref name="layout"/>; why not, when it
    /// declares a resource outside the heap or its stage is none a GPU runs.
    /// </summary>
    internal static Result<GpuShader> Read(nint layout, ReadOnlyMemory<byte> code)
    {
        nint entryPoint = spReflection_getEntryPointByIndex(layout, 0);
        GpuStage? stage = spReflectionEntryPoint_getStage(entryPoint) switch
        {
            Stage.Vertex => GpuStage.Vertex,
            Stage.Fragment => GpuStage.Fragment,
            Stage.Compute => GpuStage.Compute,
            Stage.None => null,
            _ => null,
        };

        if (stage is not { } known)
            return Result.Failure<GpuShader>("its entry point is no vertex, fragment or compute stage.");

        nint parameters = spReflectionEntryPoint_getVarLayout(entryPoint);

        if (HasCategory(parameters, ParameterCategory.DescriptorTableSlot))
        {
            return Result.Failure<GpuShader>(
                "its entry point takes a resource: read it through the heap (Shaders/Core/Heap.slang).");
        }

        uint constantBytes = GetConstantBytes(parameters);
        uint count = spReflection_GetParameterCount(layout);
        for (uint i = 0; i < count; i++)
        {
            nint parameter = spReflection_GetParameterByIndex(layout, i);
            if (HasCategory(parameter, ParameterCategory.PushConstantBuffer))
            {
                constantBytes = GetConstantBytes(parameter);

                continue;
            }

            if (!IsInHeap(parameter))
            {
                return Result.Failure<GpuShader>(
                    $"{GetName(parameter)} is declared outside the heap: read it through the heap "
                    + "(Shaders/Core/Heap.slang).");
            }
        }

        nuint* groupSize = stackalloc nuint[3];
        spReflectionEntryPoint_getComputeThreadGroupSize(entryPoint, 3, groupSize);

        return Result.Success(new GpuShader
        {
            Code = code,
            EntryPoint = "main",
            Stage = known,
            ConstantBytes = constantBytes,
            ThreadCountX = Math.Max((uint)groupSize[0], 1u),
            ThreadCountY = Math.Max((uint)groupSize[1], 1u),
            ThreadCountZ = Math.Max((uint)groupSize[2], 1u),
        });
    }

    private static bool HasCategory(nint parameter, ParameterCategory category)
    {
        nint type = spReflectionVariableLayout_GetTypeLayout(parameter);
        uint count = spReflectionTypeLayout_GetCategoryCount(type);
        for (uint i = 0; i < count; i++)
        {
            if (spReflectionTypeLayout_GetCategoryByIndex(type, i) == category) return true;
        }

        return false;
    }

    /// <summary>
    /// The size of the push constant block <paramref name="parameter"/> is, what it holds; 0 when it is none.
    /// </summary>
    private static uint GetConstantBytes(nint parameter)
    {
        if (!HasCategory(parameter, ParameterCategory.PushConstantBuffer)) return 0;

        nint block = spReflectionVariableLayout_GetTypeLayout(parameter);

        return (uint)spReflectionTypeLayout_GetSize(
            spReflectionTypeLayout_GetElementTypeLayout(block),
            ParameterCategory.Uniform);
    }

    /// <summary>
    /// Whether a parameter is one of the heap's arrays, in set 0.
    /// </summary>
    private static bool IsInHeap(nint parameter)
        => spReflectionTypeLayout_getKind(spReflectionVariableLayout_GetTypeLayout(parameter)) == TypeKind.Array
            && spReflectionParameter_GetBindingSpace(parameter) == 0;

    private static string GetName(nint parameter)
        => CString.Read(spReflectionVariable_GetName(spReflectionVariableLayout_GetVariable(parameter)));
}
