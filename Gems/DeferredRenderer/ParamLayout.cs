using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Utils;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeferredRendererGem;

internal enum ParamType : byte
{
    Float, Float2, Float3, Float4,
    Int, Int2, Int3, Int4,
    Uint, Uint2, Uint3, Uint4,
    Bool,
    /// <summary>
    /// Four floats like <see cref="Float4"/>, but authored as r, g, b, a.
    /// </summary>
    Color,
    TextureRef
}

/// <summary>
/// One member of a shader's parameter struct, where it sits in the packed record and what it defaults to.
/// </summary>
internal sealed record ParamField(string Name, ParamType Type, string? Semantic, string? Default, int Offset)
{
    public int Components => Type switch
    {
        ParamType.Float2 or ParamType.Int2 or ParamType.Uint2 => 2,
        ParamType.Float3 or ParamType.Int3 or ParamType.Uint3 => 3,
        ParamType.Float4 or ParamType.Int4 or ParamType.Uint4 or ParamType.Color => 4,
        _ => 1,
    };

    public int Size => Components * 4;

    /// <summary>
    /// <see cref="Default"/> read once into a <see cref="Param"/> by <see cref="ParamLayout.Parse"/>, so packing never parses.
    /// </summary>
    public Param DefaultParam { get; init; }

    public bool IsFloat => Type is ParamType.Float or ParamType.Float2 or ParamType.Float3 or ParamType.Float4 or ParamType.Color;

    public bool IsInt => Type is ParamType.Int or ParamType.Int2 or ParamType.Int3 or ParamType.Int4;
}

/// <summary>
/// A shader's parameter struct (<c>MaterialParams</c> or <c>PassParams</c>) as the renderer reads it out of
/// the HLSL text, since shadercross reflection gives counts and no names: its members, a packed layout for
/// them, the HLSL that unpacks a record back into the struct, and the packing of a set of <see cref="Param"/>
/// values into bytes. Packing rules, chosen so the CPU is the only place the layout exists: scalars 4-byte
/// aligned, 2-vectors 8, 3- and 4-vectors 16, declaration order, total rounded up to 16, at most
/// <see cref="RecordBytes"/>.
/// </summary>
internal sealed partial class ParamLayout
{
    public const int RecordBytes = 128;
    public const int MaxFields = 32;

    private static readonly Dictionary<string, ParamType> Types = new(StringComparer.Ordinal)
    {
        ["float"] = ParamType.Float, ["float2"] = ParamType.Float2, ["float3"] = ParamType.Float3, ["float4"] = ParamType.Float4,
        ["int"] = ParamType.Int, ["int2"] = ParamType.Int2, ["int3"] = ParamType.Int3, ["int4"] = ParamType.Int4,
        ["uint"] = ParamType.Uint, ["uint2"] = ParamType.Uint2, ["uint3"] = ParamType.Uint3, ["uint4"] = ParamType.Uint4,
        ["bool"] = ParamType.Bool,
        ["Color"] = ParamType.Color,
        ["TextureRef"] = ParamType.TextureRef,
    };

    private ParamLayout(string structName, ParamField[] fields, int size, (int Start, int End) span)
    {
        StructName = structName;
        Fields = fields;
        Size = size;
        StructSpan = span;
    }

    public string StructName { get; }

    public ParamField[] Fields { get; }

    /// <summary>
    /// Bytes of the packed record, a multiple of 16.
    /// </summary>
    public int Size { get; }

    /// <summary>
    /// Where the struct body sits in the text that was parsed (comments included), for stripping.
    /// </summary>
    public (int Start, int End) StructSpan { get; }

    /// <summary>
    /// Finds <c>struct name { ... };</c> in <paramref name="hlsl"/> and lays its members out.
    /// </summary>
    public static Result<ParamLayout> Parse(string hlsl, string structName, bool allowTextures)
    {
        Match match = StructPattern().Match(hlsl);
        while (match.Success && match.Groups["name"].Value != structName)
            match = match.NextMatch();

        if (!match.Success)
            return Result<ParamLayout>.Failure($"no 'struct {structName} {{ ... }}' declaration.");

        Group body = match.Groups["body"];
        string clean = StripComments(body.Value);

        List<ParamField> fields = [];
        int offset = 0;
        foreach (string raw in clean.Split(';'))
        {
            string declaration = raw.Trim();
            if (declaration.Length == 0)
                continue;

            Match member = MemberPattern().Match(declaration);
            if (!member.Success)
                return Result<ParamLayout>.Failure($"cannot read member '{declaration}' of {structName}.");

            string name = member.Groups["name"].Value;
            string typeName = member.Groups["type"].Value;
            if (!Types.TryGetValue(typeName, out ParamType type))
                return Result<ParamLayout>.Failure($"{structName}.{name} has type {typeName}; allowed: float/int/uint/bool, their 2..4 vectors, Color, TextureRef.");
            if (type == ParamType.TextureRef && !allowTextures)
                return Result<ParamLayout>.Failure($"{structName}.{name}: a TextureRef is not allowed here.");
            if (fields.Exists(field => field.Name == name))
                return Result<ParamLayout>.Failure($"{structName} declares {name} twice.");
            if (fields.Count == MaxFields)
                return Result<ParamLayout>.Failure($"{structName} has more than {MaxFields} members.");

            string? semantic = member.Groups["semantic"].Success ? member.Groups["semantic"].Value : null;
            string? defaultText = member.Groups["default"].Success ? member.Groups["default"].Value.Trim() : null;
            ParamField field = new(name, type, semantic, defaultText, 0);
            int align = field.Components switch { 1 => 4, 2 => 8, _ => 16 };
            offset = (offset + align - 1) / align * align;
            fields.Add(field with { Offset = offset, DefaultParam = DefaultValue(field) });
            offset += field.Size;
        }

        int size = (offset + 15) / 16 * 16;
        if (size > RecordBytes)
            return Result<ParamLayout>.Failure($"{structName} packs to {size} bytes; the record holds {RecordBytes}.");

        return Result<ParamLayout>.Success(new ParamLayout(structName, [.. fields], size, (body.Index, body.Index + body.Length)));
    }

    /// <summary>
    /// The struct body with defaults and semantics blanked out, same length, so line numbers survive. The
    /// tails are found in the body with its comments blanked (same length, so the offsets line up), so a
    /// <c>=</c> or <c>:</c> inside a comment never blanks the member after it.
    /// </summary>
    public static string BlankDefaults(string hlsl, (int Start, int End) span)
    {
        string body = hlsl[span.Start..span.End];
        char[] stripped = body.ToCharArray();
        foreach (Match match in DefaultOrSemanticPattern().Matches(StripComments(body)))
        {
            for (int i = match.Index; i < match.Index + match.Length; i++)
            {
                if (stripped[i] != '\n' && stripped[i] != '\r')
                    stripped[i] = ' ';
            }
        }

        return string.Concat(hlsl.AsSpan(0, span.Start), stripped, hlsl.AsSpan(span.End));
    }

    /// <summary>
    /// HLSL that loads a record from <c>Materials[slot]</c> into the struct: the one place the packing is spelled out for the GPU.
    /// </summary>
    public string EmitLoader(string functionName, string bufferName)
    {
        return Emit(functionName, "(uint slot)", $"    GpuMaterial record = {bufferName}[slot];\n", "record.words");
    }

    /// <summary>
    /// The same for parameters living in a <c>uint4[]</c> of a constant block (a data pass).
    /// </summary>
    public string EmitArrayLoader(string functionName, string arrayName)
    {
        return Emit(functionName, "()", "", arrayName);
    }

    /// <summary>
    /// The names in <paramref name="values"/> the struct does not declare, for the caller to warn about once, where the values are loaded.
    /// </summary>
    public List<string> UnknownKeys(IReadOnlyDictionary<string, Param> values)
    {
        List<string> unknown = [];
        foreach (string key in values.Keys)
        {
            if (!Declares(key))
                unknown.Add(key);
        }

        return unknown;
    }

    /// <summary>
    /// Packs values into <paramref name="record"/> (<see cref="RecordBytes"/> long): each field from
    /// <paramref name="values"/> by name, else its declared default, else zero. Textures go through
    /// <paramref name="texture"/>. Allocates nothing; unknown names are <see cref="UnknownKeys"/>' business.
    /// </summary>
    public void Write(IReadOnlyDictionary<string, Param> values, Func<Handle<Texture>, uint> texture, Span<byte> record)
    {
        record.Clear();

        foreach (ParamField field in Fields)
        {
            if (!values.TryGetValue(field.Name, out Param param))
                param = field.DefaultParam;

            Span<byte> dst = record.Slice(field.Offset, field.Size);
            switch (field.Type)
            {
                case ParamType.TextureRef:
                    BitConverter.TryWriteBytes(dst, param.Texture.IsValid ? texture(param.Texture) : uint.MaxValue);
                    break;
                case ParamType.Bool:
                    BitConverter.TryWriteBytes(dst, param.X != 0f ? 1u : 0u);
                    break;
                default:
                    for (int component = 0; component < field.Components; component++)
                    {
                        float value = field.Type == ParamType.Color
                            ? component switch { 0 => param.R, 1 => param.G, 2 => param.B, _ => param.A }
                            : component switch { 0 => param.X, 1 => param.Y, 2 => param.Z, _ => param.W };
                        if (field.IsFloat)
                            BitConverter.TryWriteBytes(dst[(component * 4)..], value);
                        else if (field.IsInt)
                            BitConverter.TryWriteBytes(dst[(component * 4)..], (int)value);
                        else
                            BitConverter.TryWriteBytes(dst[(component * 4)..], (uint)Math.Max(0f, value));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The declared default as a <see cref="Param"/>: the numbers in the initialiser in order (into r..a for a
    /// <see cref="ParamType.Color"/>), <c>true</c> as 1, anything else 0/none.
    /// </summary>
    private static Param DefaultValue(ParamField field)
    {
        if (field.Default is null)
            return default;
        if (field.Type == ParamType.Bool)
            return Param.Of(field.Default.Trim() == "true");
        if (field.Type == ParamType.TextureRef)
            return default;

        // "float4(0.8, 0.8, 0.8, 1.0)": the type name's digit is not a value, so identifiers go first.
        MatchCollection numbers = NumberPattern().Matches(IdentifierPattern().Replace(field.Default, " "));
        float[] values = new float[4];
        if (numbers.Count == 1)
        {
            Array.Fill(values, ParseFloat(numbers[0].Value)); // float4(0.5) broadcasts
        }
        else
        {
            for (int i = 0; i < Math.Min(4, numbers.Count); i++)
                values[i] = ParseFloat(numbers[i].Value);
        }

        if (field.Type == ParamType.Color)
            return new Param { R = values[0], G = values[1], B = values[2], A = values[3] };
        return new Param { X = values[0], Y = values[1], Z = values[2], W = values[3] };
    }

    private string Emit(string functionName, string parameters, string prologue, string rows)
    {
        StringBuilder sb = new();
        sb.Append(StructName).Append(' ').Append(functionName).Append(parameters).Append("\n{\n");
        sb.Append(prologue);
        sb.Append("    ").Append(StructName).Append(" loaded;\n");

        foreach (ParamField field in Fields)
        {
            int row = field.Offset / 16;
            int first = (field.Offset % 16) / 4;
            string swizzle = "xyzw".Substring(first, field.Components);
            string raw = $"{rows}[{row}].{swizzle}";
            string value = field.Type switch
            {
                ParamType.Bool => $"{raw} != 0u",
                _ when field.IsFloat => $"asfloat({raw})",
                _ when field.IsInt => $"asint({raw})",
                _ => raw,
            };
            sb.Append("    loaded.").Append(field.Name).Append(" = ").Append(value).Append(";\n");
        }

        sb.Append("    return loaded;\n}\n");
        return sb.ToString();
    }

    private bool Declares(string name)
    {
        foreach (ParamField field in Fields)
        {
            if (field.Name == name)
                return true;
        }

        return false;
    }

    private static float ParseFloat(string number)
    {
        return float.TryParse(number.TrimEnd('f', 'F', 'u', 'U'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
    }

    private static string Blank(string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\n' && chars[i] != '\r')
                chars[i] = ' ';
        }

        return new string(chars);
    }

    private static string StripComments(string text)
    {
        return CommentPattern().Replace(text, comment => Blank(comment.Value));
    }

    [GeneratedRegex(@"\bstruct\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex StructPattern();

    [GeneratedRegex(@"^(?<type>\w+)\s+(?<name>\w+)\s*(?::\s*(?<semantic>\w+))?\s*(?:=\s*(?<default>.+))?$", RegexOptions.Singleline)]
    private static partial Regex MemberPattern();

    // A ": Semantic" or "= default" tail of a member, up to the ';'. Never crosses a ';'.
    [GeneratedRegex(@"(:\s*\w+)|(=\s*[^;]*)")]
    private static partial Regex DefaultOrSemanticPattern();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"-?\d+(\.\d+)?([eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierPattern();
}

/// <summary>
/// What a surface shader is compiled as: its variants make distinct pipeline classes.
/// </summary>
[Flags]
internal enum SurfaceVariant : byte
{
    None = 0,
    Masked = 1,
    DoubleSided = 2,
    /// <summary>
    /// Compiled with FAILURE_FORCE 1: draws the shader-failure glow and never reads the record. Only the failure surface uses it.
    /// </summary>
    FailureForced = 4,
    /// <summary>
    /// Blended over the lit scene by the transparency stage's forward draw, not drawn into the gbuffer.
    /// </summary>
    Transparent = 8,

    /// <summary>
    /// A transparent surface that reads the scene behind it (<c>SceneBehind</c>, Include/Surface.hlsli): drawn after the
    /// other transparents, in layers, so what is behind it includes them and the glass behind it.
    /// </summary>
    Refractive = 16,

    /// <summary>
    /// An impostor's card (the last LOD of a model): compiled with SURFACE_IMPOSTOR 1, the surface's inputs read from the
    /// baked atlases (Include/Impostor.hlsli), drawn two-sided.
    /// </summary>
    Impostor = 32
}

/// <summary>
/// A surface shader as the renderer uses it: its text with defaults and roles removed, and its parameter layout.
/// </summary>
internal sealed record SurfaceSource(ulong Id, string Path, string Stripped, ParamLayout Layout);
