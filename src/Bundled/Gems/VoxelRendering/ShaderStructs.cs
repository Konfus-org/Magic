using System.Reflection;
using System.Runtime.CompilerServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// Checks, as the renderer starts, each C# struct of its own and of Core's marked <see cref="ShaderStructAttribute"/>
/// against the shader's struct it names, as the compiler reads it: their fields by name, case aside, in order and at
/// the same offsets, and their sizes. One that differs is an error naming both, as a pass would read it wrong.
/// </summary>
internal static class ShaderStructs
{
    private static readonly MethodInfo? _sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf));

    internal static async Task CheckAsync(IShaderCompiler compiler, Assets assets, CancellationToken cancel)
    {
        List<Task> checks = [];
        foreach (Assembly assembly in (ReadOnlySpan<Assembly>)
            [typeof(ShaderStructs).Assembly, typeof(ShaderStructAttribute).Assembly])
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<ShaderStructAttribute>() is { } twin)
                    checks.Add(CheckAsync(compiler, assets, type, twin, cancel));
            }
        }

        await Task.WhenAll(checks).ConfigureAwait(false);
    }

    private static async Task CheckAsync(
        IShaderCompiler compiler,
        Assets assets,
        Type type,
        ShaderStructAttribute twin,
        CancellationToken cancel)
    {
        ShaderType shaderType = new(assets.Find<Shader>(twin.Path), twin.Name);
        Result<ShaderStruct> read = await compiler.ReflectStructAsync(shaderType, cancel).ConfigureAwait(false);
        string? why = read.Ok ? Compare(type, read.Value) : read.Error;
        if (why is not null) Debugging.Error($"{type.Name} is not {twin.Path}'s {twin.Name}: {why}");
    }

    /// <summary>
    /// Where <paramref name="type"/> differs from <paramref name="twin"/>; null where it does not. Each field is laid
    /// out right after the one before, as the shader's are, so the fields' sizes in order give their offsets, and a
    /// size above their sum is padding the shader's has not.
    /// </summary>
    private static string? Compare(Type type, ShaderStruct twin)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (fields.Length != twin.Fields.Count)
            return $"it has {fields.Length} fields, and the shader's {twin.Fields.Count}.";

        // In the order they are declared, which is the order a sequential struct lays them out in.
        Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        uint offset = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            string name = NameOf(fields[i]);
            ShaderField field = twin.Fields[i];

            if (!string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase) || offset != field.Offset)
                return $"its {name} at byte {offset} is the shader's {field.Name} at byte {field.Offset}.";

            offset += SizeOf(fields[i].FieldType);
        }

        uint size = SizeOf(type);

        return size == twin.Size && size == offset ? null : $"it takes {size} bytes, and the shader's {twin.Size}.";
    }

    private static uint SizeOf(Type type)
        => _sizeOf?.MakeGenericMethod(type).Invoke(null, null) is int size ? (uint)size : 0;

    /// <summary>
    /// A field's name in code: a record's property's, rather than its backing field's.
    /// </summary>
    private static string NameOf(FieldInfo field)
        => field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>', StringComparison.Ordinal)] : field.Name;
}
