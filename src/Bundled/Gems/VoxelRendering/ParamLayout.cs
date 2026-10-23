using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;

namespace Magic.VoxelRendering;

/// <summary>
/// The type of a <see cref="ParamField"/>.
/// </summary>
internal enum ParamType : byte
{
    Float,
    Float2,
    Float3,
    Float4,
    Int,
    Int2,
    Int3,
    Int4,
    Uint,
    Uint2,
    Uint3,
    Uint4,
    Bool,

    /// <summary>
    /// Four floats, written r, g, b, a.
    /// </summary>
    Color,

    /// <summary>
    /// A texture's heap id, or <c>TextureNone</c>.
    /// </summary>
    TextureRef,
}

/// <summary>
/// A surface's <c>MaterialParams</c> as the renderer reads it from the HLSL, the compiler reporting no names: its
/// members, where each sits in the packed record, the HLSL that unpacks the record, and how values are packed into it.
/// The CPU is the one place the layout exists: scalars 4-byte aligned, 2-vectors 8, 3- and 4-vectors 16, in declaration
/// order, the total rounded up to 16 bytes and at most <see cref="RecordBytes"/>. A member may carry a default and a
/// role (<c>: GiColor</c>), which HLSL takes neither of: both are blanked out of the text the compiler sees.
/// </summary>
internal sealed partial class ParamLayout
{
    /// <summary>
    /// The most a material's params pack to: the eight rows of its row in the materials' table.
    /// </summary>
    internal const int RecordBytes = 128;

    private const string StructName = "MaterialParams";

    private static readonly Dictionary<string, ParamType> _types = new(StringComparer.Ordinal)
    {
        ["float"] = ParamType.Float,
        ["float2"] = ParamType.Float2,
        ["float3"] = ParamType.Float3,
        ["float4"] = ParamType.Float4,
        ["int"] = ParamType.Int,
        ["int2"] = ParamType.Int2,
        ["int3"] = ParamType.Int3,
        ["int4"] = ParamType.Int4,
        ["uint"] = ParamType.Uint,
        ["uint2"] = ParamType.Uint2,
        ["uint3"] = ParamType.Uint3,
        ["uint4"] = ParamType.Uint4,
        ["bool"] = ParamType.Bool,
        ["Color"] = ParamType.Color,
        ["TextureRef"] = ParamType.TextureRef,
    };

    /// <summary>
    /// The text the struct was read from, and where its body is in it, its braces excluded.
    /// </summary>
    private readonly string _hlsl;
    private readonly int _bodyStart;
    private readonly int _bodyEnd;

    private ParamLayout(string hlsl, IReadOnlyList<ParamField> fields, int size, int bodyStart, int bodyEnd)
    {
        _hlsl = hlsl;
        Fields = fields;
        Size = size;
        _bodyStart = bodyStart;
        _bodyEnd = bodyEnd;
    }

    internal IReadOnlyList<ParamField> Fields { get; }

    /// <summary>
    /// Bytes of the packed record, a multiple of 16.
    /// </summary>
    internal int Size { get; }

    /// <summary>
    /// <c>struct MaterialParams { ... };</c> in <paramref name="hlsl"/>, laid out; none when the text has no such struct.
    /// Why not, for a member of a type a param cannot be, or more than <see cref="RecordBytes"/> of them.
    /// </summary>
    internal static Result<ParamLayout?> Parse(string hlsl)
    {
        Match match = StructPattern().Match(hlsl);
        while (match.Success && !string.Equals(match.Groups["name"].Value, StructName, StringComparison.Ordinal))
        {
            match = match.NextMatch();
        }

        if (!match.Success) return Result.Success<ParamLayout?>(null);

        Group body = match.Groups["body"];
        List<ParamField> fields = [];
        int offset = 0;
        foreach (string raw in StripComments(body.Value).Split(';'))
        {
            string declaration = raw.Trim();

            if (declaration.Length == 0) continue;

            Match member = MemberPattern().Match(declaration);

            if (!member.Success)
            {
                return Result.Failure<ParamLayout?>(
                    $"{StructName} has a member that is not 'type name [: Role] [= default]': '{declaration}'.");
            }

            string name = member.Groups["name"].Value;
            string typeName = member.Groups["type"].Value;

            if (!_types.TryGetValue(typeName, out ParamType type))
            {
                return Result.Failure<ParamLayout?>($"{StructName}.{name} is a {typeName}; a param is float, int, "
                    + "uint or bool, a 2 to 4 vector of one, a Color or a TextureRef.");
            }

            if (fields.Exists(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
                return Result.Failure<ParamLayout?>($"{StructName} declares {name} twice.");

            ParamField field = new(name, type, 0, new MaterialParam());
            int align = field.Components switch { 1 => 4, 2 => 8, _ => 16 };
            offset = RoundUp(offset, align);
            string? defaultText = member.Groups["default"].Success ? member.Groups["default"].Value.Trim() : null;
            fields.Add(field with { Offset = offset, Default = GetDefault(field, defaultText) });
            offset += field.Size;
        }

        int size = RoundUp(offset, 16);

        if (size > RecordBytes)
            return Result.Failure<ParamLayout?>($"{StructName} packs to {size} bytes, past the {RecordBytes} it may.");

        return Result.Success<ParamLayout?>(new ParamLayout(hlsl, fields, size, body.Index, body.Index + body.Length));
    }

    /// <summary>
    /// The text the struct was read from as HLSL takes it: the struct's defaults and roles blanked out, keeping every
    /// line where it was so the compiler's line numbers still hold, then, on the struct's last line, the loader that
    /// unpacks the struct from <paramref name="rows"/>, an expression of <c>uint4</c> rows, as
    /// <c>MaterialParams signature { prologue ... }</c>.
    /// </summary>
    internal string Prepare(string signature, string prologue, string rows)
    {
        string body = _hlsl[_bodyStart.._bodyEnd];
        char[] blanked = body.ToCharArray();
        foreach (Match tail in TailPattern().Matches(StripComments(body)))
        {
            for (int i = tail.Index; i < tail.Index + tail.Length; i++)
            {
                if (blanked[i] is not '\n' and not '\r') blanked[i] = ' ';
            }
        }

        string loader = CreateLoader(signature, prologue, rows);
        int structEnd = _hlsl.IndexOf(';', _bodyEnd) + 1;

        return new StringBuilder(_hlsl.Length + loader.Length + 1)
            .Append(_hlsl, 0, _bodyStart)
            .Append(blanked)
            .Append(_hlsl, _bodyEnd, structEnd - _bodyEnd)
            .Append(' ')
            .Append(loader.Replace('\n', ' '))
            .Append(_hlsl, structEnd, _hlsl.Length - structEnd)
            .ToString();
    }

    /// <summary>
    /// The names in <paramref name="values"/> the struct does not declare, for the caller to warn of.
    /// </summary>
    internal List<string> FindUnknown(IEnumerable<string> values)
    {
        List<string> unknown = [];
        foreach (string name in values)
        {
            if (FindField(name) is null) unknown.Add(name);
        }

        return unknown;
    }

    /// <summary>
    /// Packs each member into <paramref name="record"/> from <paramref name="values"/> by name, else its default, else
    /// zero; a texture as <paramref name="texture"/> resolves it.
    /// </summary>
    internal void Write(
        IReadOnlyDictionary<string, MaterialParam> values,
        Func<MaterialParam, uint> texture,
        Span<byte> record)
    {
        record.Clear();
        foreach (ParamField field in Fields)
        {
            MaterialParam value = values.TryGetValue(field.Name, out MaterialParam? given) ? given : field.Default;
            Span<byte> destination = record.Slice(field.Offset, field.Size);
            if (field.Type == ParamType.TextureRef)
                BinaryPrimitives.WriteUInt32LittleEndian(destination, texture(value));
            else if (field.Type == ParamType.Bool)
                BinaryPrimitives.WriteUInt32LittleEndian(destination, value.X != 0 ? 1u : 0u);
            else WriteComponents(field, value, destination);
        }
    }

    private static void WriteComponents(ParamField field, MaterialParam value, Span<byte> destination)
    {
        for (int component = 0; component < field.Components; component++)
        {
            float number = GetComponent(field, value, component);
            Span<byte> word = destination[(component * 4)..];
            if (field.IsFloat) BinaryPrimitives.WriteSingleLittleEndian(word, number);
            else if (field.IsInt) BinaryPrimitives.WriteInt32LittleEndian(word, (int)number);
            else BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)MathF.Max(0, number));
        }
    }

    /// <summary>
    /// A component of a member's value: r to a for a Color, x to w for the rest.
    /// </summary>
    private static float GetComponent(ParamField field, MaterialParam value, int component)
    {
        return field.Type == ParamType.Color
            ? component switch { 0 => value.R, 1 => value.G, 2 => value.B, _ => value.A }
            : component switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };
    }

    /// <summary>
    /// <paramref name="value"/> rounded up to a whole number of <paramref name="multiple"/>.
    /// </summary>
    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// The declared default as a param: the numbers of its initialiser in order (into r to a for a Color, and one
    /// number into every component, as <c>float4(0.5)</c> does), true as 1, anything else zero.
    /// </summary>
    private static MaterialParam GetDefault(ParamField field, string? text)
    {
        if (text is null || field.Type is ParamType.TextureRef) return new MaterialParam();

        if (field.Type == ParamType.Bool)
            return new MaterialParam { X = string.Equals(text, "true", StringComparison.Ordinal) ? 1 : 0 };

        // A whole number in hex, as HLSL writes a mask or a none (0xFFFFu), which the names below would take apart.
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(
                text.AsSpan(2).TrimEnd("uU"),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint hex))
        {
            return new MaterialParam { X = hex };
        }

        // The type's own digit, "float4(", is not a value, so names go first.
        MatchCollection numbers = NumberPattern().Matches(NamePattern().Replace(text, " "));
        float[] values = new float[4];
        for (int i = 0; i < 4; i++)
        {
            int index = numbers.Count == 1 ? 0 : i;
            if (index < numbers.Count)
            {
                values[i] = float.Parse(
                    numbers[index].Value.TrimEnd('f', 'F', 'u', 'U'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture);
            }
        }

        return field.Type == ParamType.Color
            ? new MaterialParam { R = values[0], G = values[1], B = values[2], A = values[3] }
            : new MaterialParam { X = values[0], Y = values[1], Z = values[2], W = values[3] };
    }

    private static string StripComments(string text)
    {
        return CommentPattern().Replace(
            text,
            comment => new string(Array.ConvertAll(comment.Value.ToCharArray(), c => c is '\n' or '\r' ? c : ' ')));
    }

    [GeneratedRegex(
        @"\bstruct\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex StructPattern();

    [GeneratedRegex(
        @"^(?<type>\w+)\s+(?<name>\w+)\s*(?::\s*(?<role>\w+))?\s*(?:=\s*(?<default>.+))?$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex MemberPattern();

    /// <summary>
    /// A member's <c>: Role</c> or <c>= default</c>, up to its <c>;</c>.
    /// </summary>
    [GeneratedRegex(
        @":\s*\w+|=\s*[^;]*",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TailPattern();

    [GeneratedRegex(
        @"//[^\n]*|/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(
        @"-?\d+(\.\d+)?([eE][-+]?\d+)?[fFuU]?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(
        @"[A-Za-z_]\w*",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();

    /// <summary>
    /// HLSL that unpacks the struct from <paramref name="rows"/>, an expression of <c>uint4</c> rows:
    /// <c>MaterialParams LoadMaterialParams(uint material) { ... }</c>. One line, so it shifts no line after it.
    /// </summary>
    private string CreateLoader(string signature, string prologue, string rows)
    {
        StringBuilder text = new();
        text.Append(StructName).Append(' ').Append(signature).Append(" { ").Append(prologue).Append(' ')
            .Append(StructName)
            .Append(" loaded; ");
        foreach (ParamField field in Fields)
        {
            string raw = $"{rows}[{field.Offset / 16}].{"xyzw".Substring(field.Offset % 16 / 4, field.Components)}";
            string value = field switch
            {
                { Type: ParamType.Bool } => $"{raw} != 0u",
                { IsFloat: true } => $"asfloat({raw})",
                { IsInt: true } => $"asint({raw})",
                _ => raw,
            };
            text.Append("loaded.").Append(field.Name).Append(" = ").Append(value).Append("; ");
        }

        return text.Append("return loaded; }").ToString();
    }

    /// <summary>
    /// The member called <paramref name="name"/>, or null.
    /// </summary>
    private ParamField? FindField(string name)
    {
        foreach (ParamField field in Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal)) return field;
        }

        return null;
    }
}

/// <summary>
/// One member of a <c>MaterialParams</c>: where it sits in the packed record, and its declared default.
/// </summary>
internal sealed record ParamField(string Name, ParamType Type, int Offset, MaterialParam Default)
{
    internal int Components => Type switch
    {
        ParamType.Float or ParamType.Int or ParamType.Uint or ParamType.Bool or ParamType.TextureRef => 1,
        ParamType.Float2 or ParamType.Int2 or ParamType.Uint2 => 2,
        ParamType.Float3 or ParamType.Int3 or ParamType.Uint3 => 3,
        ParamType.Float4 or ParamType.Int4 or ParamType.Uint4 or ParamType.Color => 4,
        _ => 1,
    };

    internal int Size => Components * 4;

    internal bool IsFloat
        => Type is ParamType.Float or ParamType.Float2 or ParamType.Float3 or ParamType.Float4 or ParamType.Color;

    internal bool IsInt => Type is ParamType.Int or ParamType.Int2 or ParamType.Int3 or ParamType.Int4;
}
