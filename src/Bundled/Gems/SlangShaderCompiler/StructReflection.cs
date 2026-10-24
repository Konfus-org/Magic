using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using static Magic.SlangShaderCompiler.Slang;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// What a struct a shader declares is, read from its module's reflection (Core's <see cref="ShaderStruct"/>): which of
/// Shaders/Core/Surface.slang's <c>ISurface</c> and Sky.slang's <c>ISky</c> it implements, and its fields, each where
/// it sits as a buffer lays the struct out, its size, what it is as a material's parameter, and its initializer's
/// packed bytes, its default.
/// </summary>
internal static unsafe class StructReflection
{
    /// <summary>
    /// The struct <paramref name="name"/> the program of <paramref name="layout"/> declares; why not, when it declares
    /// none.
    /// </summary>
    internal static Result<ShaderStruct> Read(nint layout, string name)
    {
        nint type;
        fixed (byte* named = CString.ToBytes(name))
        {
            type = spReflection_FindTypeByName(layout, named);
        }

        if (type == 0 || spReflectionType_GetKind(type) != TypeKind.Struct)
            return Result.Failure<ShaderStruct>($"it declares no struct {name}.");

        nint typeLayout = spReflection_GetTypeLayout(layout, type, StructuredBufferLayout);
        List<ShaderField> fields = [];
        uint count = spReflectionTypeLayout_GetFieldCount(typeLayout);
        for (uint i = 0; i < count; i++)
        {
            nint field = spReflectionTypeLayout_GetFieldByIndex(typeLayout, i);
            nint variable = spReflectionVariableLayout_GetVariable(field);
            fields.Add(new ShaderField(
                CString.Read(spReflectionVariable_GetName(variable)),
                (uint)spReflectionVariableLayout_GetOffset(field, ParameterCategory.Uniform),
                (uint)spReflectionTypeLayout_GetSize(
                    spReflectionVariableLayout_GetTypeLayout(field),
                    ParameterCategory.Uniform),
                GetParamType(spReflectionVariable_GetType(variable)),
                GetDefault(variable)));
        }

        uint size = (uint)spReflectionTypeLayout_GetSize(typeLayout, ParameterCategory.Uniform);

        return Result.Success(new ShaderStruct(GetContract(layout, type), size, fields));
    }

    private static ShaderContract GetContract(nint layout, nint type)
    {
        if (Implements(layout, type, CString.Constant("ISurface"u8))) return ShaderContract.Surface;

        return Implements(layout, type, CString.Constant("ISky"u8)) ? ShaderContract.Sky : ShaderContract.None;
    }

    private static bool Implements(nint layout, nint type, byte* contractName)
    {
        nint contract = spReflection_FindTypeByName(layout, contractName);

        return contract != 0 && spReflection_isSubType(layout, type, contract) != 0;
    }

    /// <summary>
    /// What a field's type is as a material's parameter, or null for one no parameter can be.
    /// </summary>
    private static ShaderParamType? GetParamType(nint type) => spReflectionType_GetKind(type) switch
    {
        TypeKind.Scalar => GetVectorType(spReflectionType_GetScalarType(type), 1),
        TypeKind.Vector => GetVectorType(
            spReflectionType_GetScalarType(type),
            (int)spReflectionType_GetElementCount(type)),
        TypeKind.Struct => CString.Read(spReflectionType_GetName(type)) switch
        {
            "Color" => ShaderParamType.Color,
            "TextureRef" => ShaderParamType.Texture,
            _ => null,
        },
        TypeKind.Array => null,
        _ => null,
    };

    private static ShaderParamType? GetVectorType(ScalarType scalar, int count) => (scalar, count) switch
    {
        (ScalarType.Float32, 1) => ShaderParamType.Float1,
        (ScalarType.Float32, 2) => ShaderParamType.Float2,
        (ScalarType.Float32, 3) => ShaderParamType.Float3,
        (ScalarType.Float32, 4) => ShaderParamType.Float4,
        (ScalarType.Int32, 1) => ShaderParamType.Int1,
        (ScalarType.Int32, 2) => ShaderParamType.Int2,
        (ScalarType.Int32, 3) => ShaderParamType.Int3,
        (ScalarType.Int32, 4) => ShaderParamType.Int4,
        (ScalarType.UInt32, 1) => ShaderParamType.Uint1,
        (ScalarType.UInt32, 2) => ShaderParamType.Uint2,
        (ScalarType.UInt32, 3) => ShaderParamType.Uint3,
        (ScalarType.UInt32, 4) => ShaderParamType.Uint4,
        (ScalarType.Bool, 1) => ShaderParamType.Bool,
        _ => null,
    };

    /// <summary>
    /// A field's initializer as Slang packs it, or none when it has none.
    /// </summary>
    private static byte[] GetDefault(nint variable)
    {
        nint blob;

        if (GetDefaultValueBlob(variable, &blob) < 0 || blob == 0) return [];

        byte[] bytes = GetBytes(blob).ToArray();
        Release(blob);

        return bytes;
    }
}
