using Magic.Contexts.Rendering;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Magic.Services;

/// <summary>
/// The HLSL twins of C# types, so a struct the GPU reads cannot drift from the shader that reads it: for the types it is
/// handed, those marked <see cref="HlslAttribute"/>, and the <c>const</c> fields marked on their own. Pure: the caller
/// says which types (a renderer, its own) and writes the text where its shaders include it. The text has the defines
/// first, then the constants, then the structs, each group in the order of the C# names, so the same
/// types always give the same text. A struct's members come in the order of their offsets, named in camelCase, typed
/// as the GPU reads them: <c>float</c>, <c>int</c>, <c>uint</c> (an enum as its underlying type), <c>floatN</c> for a
/// <c>Vector2..4</c>, <c>float4x4</c> for a <c>Matrix4x4</c>, <c>uintN</c> for a struct of N <c>uint</c>s, and an
/// array for an inline array. Anything else (a <c>bool</c>, whose size the GPU does not agree on) is refused.
/// </summary>
public static partial class Hlsl
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Constants = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static string Write(IEnumerable<Type> types)
    {
        StringBuilder defines = new(), constants = new(), structs = new();
        foreach (Type type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type.GetCustomAttribute<HlslAttribute>() is { } marked)
            {
                if (type.IsEnum)
                    WriteEnum(constants, type, marked.Name ?? type.Name);
                else if (type.IsValueType)
                    WriteStruct(structs, type, marked.Name ?? type.Name);
                else
                    WriteConstants(constants, type);
            }

            foreach (FieldInfo field in type.GetFields(Constants).Where(field => field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                if (field.GetCustomAttribute<HlslAttribute>() is not { } own)
                    continue;

                string name = own.Name ?? field.Name;
                string value = Literal(field.GetRawConstantValue(), field.FieldType);
                if (own.Define)
                    defines.Append(CultureInfo.InvariantCulture, $"#define {name} {value}\n");
                else
                    constants.Append(CultureInfo.InvariantCulture, $"static const {Scalar(field.FieldType)} {name} = {value};\n");
            }
        }

        return $"// Written from the C# marked [Hlsl] (Hlsl.Write): change the C#, never this.\n{defines}{constants}{structs}";
    }

    private static void WriteEnum(StringBuilder text, Type type, string prefix)
    {
        Type underlying = Enum.GetUnderlyingType(type);
        foreach (string member in Enum.GetNames(type))
        {
            object value = Convert.ChangeType(Enum.Parse(type, member), underlying, CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture, $"static const {Scalar(underlying)} {prefix}{member} = {Literal(value, underlying)};\n");
        }
    }

    private static void WriteConstants(StringBuilder text, Type type)
    {
        foreach (FieldInfo field in type.GetFields(Constants).Where(field => field.IsLiteral && !field.IsDefined(typeof(HlslAttribute))))
            text.Append(CultureInfo.InvariantCulture, $"static const {Scalar(field.FieldType)} {field.Name} = {Literal(field.GetRawConstantValue(), field.FieldType)};\n");
    }

    private static void WriteStruct(StringBuilder text, Type type, string name)
    {
        text.Append(CultureInfo.InvariantCulture, $"struct {name}\n{{\n");
        foreach (FieldInfo field in type.GetFields(Instance).OrderBy(field => Marshal.OffsetOf(type, field.Name).ToInt64()))
            text.Append(CultureInfo.InvariantCulture, $"    {Member(field.FieldType, CamelCase(field.Name))};\n");

        text.Append("};\n");
    }

    /// <summary>
    /// One member's declaration: its HLSL type and name, with the length after it for an inline array.
    /// </summary>
    private static string Member(Type type, string name)
    {
        if (type.GetCustomAttribute<InlineArrayAttribute>() is { } array)
            return $"{Vector(type.GetFields(Instance)[0].FieldType)} {name}[{array.Length}]";

        return $"{Vector(type)} {name}";
    }

    private static string Vector(Type type)
    {
        if (type == typeof(Vector2))
            return "float2";
        if (type == typeof(Vector3))
            return "float3";
        if (type == typeof(Vector4))
            return "float4";
        if (type == typeof(Matrix4x4))
            return "float4x4";
        if (type.IsPrimitive || type.IsEnum)
            return Scalar(type);

        FieldInfo[] fields = type.GetFields(Instance);
        if (fields.Length is >= 2 and <= 4 && fields.All(field => field.FieldType == fields[0].FieldType && field.FieldType.IsPrimitive))
            return $"{Scalar(fields[0].FieldType)}{fields.Length}";

        throw new NotSupportedException($"{type.Name} has no HLSL twin.");
    }

    private static string Scalar(Type type)
    {
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        if (type == typeof(float))
            return "float";
        if (type == typeof(uint) || type == typeof(ushort) || type == typeof(byte))
            return "uint";
        if (type == typeof(int) || type == typeof(short) || type == typeof(sbyte))
            return "int";

        throw new NotSupportedException($"{type.Name} has no HLSL twin.");
    }

    private static string Literal(object? value, Type type)
    {
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        return value switch
        {
            uint or ushort or byte => $"{Convert.ToUInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}u",
            int or short or sbyte => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            float single => single.ToString("0.0###########", CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"{type.Name} has no HLSL twin."),
        };
    }

    /// <summary>
    /// <paramref name="source"/> with each <c>#include "path"</c> line replaced by the text <paramref name="read"/>
    /// gives for the path, spliced in turn, so the compiler needs no folder to find includes in: a gem's shaders
    /// include whatever assets are loaded, whichever gem they came with. Every include goes in where it is written, as
    /// the preprocessor would read it (its guards then keep a second copy out), between <c>#line</c>s that keep the
    /// compiler's errors naming the file and line they are in; a file that includes itself, directly or not, is not
    /// spliced into itself again. An include <paramref name="read"/> has no text for (null) is left
    /// as it is, for the compiler to report. <paramref name="name"/> is what <paramref name="source"/> is called.
    /// </summary>
    public static string Splice(string source, string name, Func<string, string?> read)
    {
        StringBuilder spliced = new(source.Length);
        Splice(source, name, read, [name], spliced);
        return spliced.ToString();
    }

    private static void Splice(string source, string name, Func<string, string?> read, List<string> including, StringBuilder into)
    {
        int number = 0;
        foreach (string line in source.Split('\n'))
        {
            number++;
            if (Include().Match(line) is not { Success: true } include)
            {
                into.Append(line).Append('\n');
                continue;
            }

            string path = include.Groups[1].Value;
            if (including.Contains(path) || read(path) is not { } text)
            {
                into.Append(line).Append('\n');
                continue;
            }

            into.Append(CultureInfo.InvariantCulture, $"#line 1 \"{path}\"\n");
            including.Add(path);
            Splice(text, path, read, including, into);
            including.RemoveAt(including.Count - 1);
            into.Append(CultureInfo.InvariantCulture, $"#line {number + 1} \"{name}\"\n");
        }
    }

    /// <summary>
    /// The files <paramref name="source"/> includes, as written (relative to the shader root), in order: what
    /// <see cref="Splice"/> splices in, so that editing one can reload every shader that includes it.
    /// </summary>
    public static string[] Includes(string source)
    {
        return [.. source.Split('\n').Select(line => Include().Match(line)).Where(include => include.Success).Select(include => include.Groups[1].Value)];
    }

    [GeneratedRegex("""^\s*#\s*include\s+"([^"]+)"\s*(//.*)?$""")]
    private static partial Regex Include();

    private static string CamelCase(string name)
    {
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
