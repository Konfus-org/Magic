using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Extensions;

namespace Magic.ClusterRendering;

/// <summary>
/// A shader's param struct, a surface's <c>MaterialParams</c> or a pass's <c>PassParams</c>, as the renderer reads it
/// from the HLSL, the compiler reporting counts and no names: its members, where each sits in the packed record, the
/// HLSL that unpacks the record, and how values are packed into it. The CPU is the one place the layout exists: scalars
/// 4-byte aligned, 2-vectors 8, 3- and 4-vectors 16 and curves 16, four rows each, in declaration order, the total
/// rounded up to 16 bytes and at most <see cref="RecordBytes"/> for a material, <see cref="PassBytes"/> for a pass.
/// </summary>
internal sealed partial class ParamLayout
{
    /// <summary>
    /// The most a material's params pack to: the eight rows of its row in the materials buffer.
    /// </summary>
    internal const int RecordBytes = 128;

    /// <summary>
    /// The most a pass's params pack to: seven rows of the constant block, the last being the renderer's.
    /// </summary>
    internal const int PassBytes = 112;

    /// <summary>
    /// The places along a curve param its value is packed at, evenly from its first key to its last, after where those
    /// are: the 16 floats of Shaders/Include/Curve.hlsli's <c>Curve</c>.
    /// </summary>
    private const int CurveSamples = 14;

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
        ["Curve"] = ParamType.Curve,
    };

    /// <summary>
    /// The text the struct was read from, and where its body is in it, its braces excluded.
    /// </summary>
    private readonly string _hlsl;
    private readonly int _bodyStart;
    private readonly int _bodyEnd;

    private ParamLayout(string hlsl, string structName, IReadOnlyList<ParamField> fields, int size, int bodyStart, int bodyEnd)
    {
        _hlsl = hlsl;
        StructName = structName;
        Fields = fields;
        Size = size;
        _bodyStart = bodyStart;
        _bodyEnd = bodyEnd;
    }

    private string StructName { get; }

    internal IReadOnlyList<ParamField> Fields { get; }

    /// <summary>
    /// Bytes of the packed record, a multiple of 16.
    /// </summary>
    internal int Size { get; }

    /// <summary>
    /// <c>struct structName { ... };</c> in <paramref name="hlsl"/>, laid out; none when the text has no such struct, so a
    /// pass with no params needs none. Why not, for a member of a type a param cannot be, or more than
    /// <paramref name="maxBytes"/> of them.
    /// </summary>
    internal static Result<ParamLayout?> Parse(string hlsl, string structName, bool allowTextures, bool allowCurves, int maxBytes)
    {
        Match match = StructPattern().Match(hlsl);
        while (match.Success && !string.Equals(match.Groups["name"].Value, structName, StringComparison.Ordinal))
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
                return Result.Failure<ParamLayout?>($"{structName} has a member that is not 'type name [: Role] [= default]': '{declaration}'.");
            }

            string name = member.Groups["name"].Value;
            string typeName = member.Groups["type"].Value;
            if (!_types.TryGetValue(typeName, out ParamType type))
            {
                return Result.Failure<ParamLayout?>($"{structName}.{name} is a {typeName}; a param is float, int, uint or bool, a 2 to 4 vector of one, a Color, a TextureRef or a Curve.");
            }

            if (type == ParamType.TextureRef && !allowTextures)
            {
                return Result.Failure<ParamLayout?>($"{structName}.{name} is a TextureRef, which a pass's params cannot be.");
            }

            if (type == ParamType.Curve && !allowCurves)
            {
                return Result.Failure<ParamLayout?>($"{structName}.{name} is a Curve, which a material's params cannot be.");
            }

            if (fields.Exists(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
            {
                return Result.Failure<ParamLayout?>($"{structName} declares {name} twice.");
            }

            ParamField field = new(name, type, 0, new MaterialParam());
            int align = field.Components switch { 1 => 4, 2 => 8, _ => 16 };
            offset = (offset + align - 1) / align * align;
            fields.Add(field with { Offset = offset, Default = GetDefault(field, member.Groups["default"].Success ? member.Groups["default"].Value.Trim() : null), Role = member.Groups["role"].Value });
            offset += field.Size;
        }

        int size = (offset + 15) / 16 * 16;
        if (size > maxBytes)
        {
            return Result.Failure<ParamLayout?>($"{structName} packs to {size} bytes, past the {maxBytes} it may.");
        }

        return Result.Success<ParamLayout?>(new ParamLayout(hlsl, structName, fields, size, body.Index, body.Index + body.Length));
    }

    /// <summary>
    /// The text the struct was read from as HLSL takes it: the struct's defaults and roles blanked out, keeping every
    /// line where it was so the compiler's line numbers still hold, then, on the struct's last line, the loader that
    /// unpacks the struct from <paramref name="rows"/>, an expression of <c>uint4</c> rows, as
    /// <c>StructName signature { prologue ... }</c>.
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

        string loader = MakeLoader(signature, prologue, rows);
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
    /// What a material of <paramref name="values"/> gives global illumination: the colour of its <c>: GiColor</c> member,
    /// white without one, and half of it when a <c>: GiColorMap</c> member names a texture, whose own colour is taken as
    /// mid grey, less as much of it as its <c>: GiMetallic</c> member says it is metal, as a metal scatters no light the
    /// way a matte surface does, only mirrors it; and the light of its <c>: GiEmissive</c> member, none without one.
    /// </summary>
    internal (Vector3 Color, Vector3 Emission) GiOf(IReadOnlyDictionary<string, MaterialParam> values)
    {
        Vector3 color = Vector3.One;
        Vector3 emission = Vector3.Zero;
        float metallic = 0;
        bool isMapped = false;
        foreach (ParamField field in Fields)
        {
            MaterialParam value = values.TryGetValue(field.Name, out MaterialParam? given) ? given : field.Default;
            Vector3 rgb = field.Type == ParamType.Color ? new Vector3(value.R, value.G, value.B) : new Vector3(value.X, value.Y, value.Z);
            switch (field.Role)
            {
                case "GiColor":
                    color = rgb;
                    break;
                case "GiEmissive":
                    emission = rgb;
                    break;
                case "GiColorMap":
                    isMapped = value.Texture != Handle<Texture>.None;
                    break;
                case "GiMetallic":
                    metallic = Math.Clamp(value.X, 0, 1);
                    break;
                default:
                    break;
            }
        }

        return ((isMapped ? color * 0.5f : color) * (1 - metallic), emission);
    }

    /// <summary>
    /// Packs each member into <paramref name="record"/> from the first of <paramref name="layers"/> that has it by name,
    /// else its default, else zero; a texture as <paramref name="texture"/> resolves it, and a curve as
    /// <paramref name="curve"/> does, all zeros for none.
    /// </summary>
    internal void Write(ReadOnlySpan<IReadOnlyDictionary<string, MaterialParam>?> layers, Func<MaterialParam, uint> texture, Func<MaterialParam, Curve?> curve, Span<byte> record)
    {
        record.Clear();
        foreach (ParamField field in Fields)
        {
            MaterialParam value = field.Default;
            foreach (IReadOnlyDictionary<string, MaterialParam>? layer in layers)
            {
                if (layer is not null && layer.TryGetValue(field.Name, out MaterialParam? given))
                {
                    value = given;
                    break;
                }
            }

            Span<byte> destination = record.Slice(field.Offset, field.Size);
            if (field.Type == ParamType.TextureRef)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination, texture(value));
            }
            else if (field.Type == ParamType.Bool)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination, value.X != 0 ? 1u : 0u);
            }
            else if (field.Type == ParamType.Curve)
            {
                WriteCurve(curve(value), destination);
            }
            else
            {
                WriteComponents(field, value, destination);
            }
        }
    }

    /// <summary>
    /// HLSL that unpacks the struct from <paramref name="rows"/>, an expression of <c>uint4</c> rows:
    /// <c>MaterialParams LoadMaterialParams(uint material) { ... }</c>. One line, so it shifts no line after it.
    /// </summary>
    private string MakeLoader(string signature, string prologue, string rows)
    {
        StringBuilder text = new();
        text.Append(StructName).Append(' ').Append(signature).Append(" { ").Append(prologue).Append(' ').Append(StructName).Append(" loaded; ");
        foreach (ParamField field in Fields)
        {
            if (field.Type == ParamType.Curve)
            {
                for (int row = 0; row < field.Size / 16; row++)
                {
                    text.Append(CultureInfo.InvariantCulture, $"loaded.{field.Name}.rows[{row}] = asfloat({rows}[{(field.Offset / 16) + row}]); ");
                }

                continue;
            }

            string raw = $"{rows}[{field.Offset / 16}].{"xyzw".Substring(field.Offset % 16 / 4, field.Components)}";
            string value = field.Type == ParamType.Bool ? $"{raw} != 0u" : field.IsFloat ? $"asfloat({raw})" : field.IsInt ? $"asint({raw})" : raw;
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

    /// <summary>
    /// A curve as Shaders/Include/Curve.hlsli reads it: where its first and last keys are, then its value at
    /// <see cref="CurveSamples"/> places evenly from the one to the other.
    /// </summary>
    private static void WriteCurve(Curve? curve, Span<byte> destination)
    {
        if (curve is not { Keys.Count: > 0 }) return;

        float start = curve.Keys[0].At;
        float end = curve.Keys[^1].At;
        BinaryPrimitives.WriteSingleLittleEndian(destination, start);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], end);
        for (int i = 0; i < CurveSamples; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination[(8 + (i * 4))..], curve.At(start + ((end - start) * i / (CurveSamples - 1))));
        }
    }

    private static void WriteComponents(ParamField field, MaterialParam value, Span<byte> destination)
    {
        for (int component = 0; component < field.Components; component++)
        {
            float number = field.Type == ParamType.Color
                ? component switch { 0 => value.R, 1 => value.G, 2 => value.B, _ => value.A }
                : component switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };
            Span<byte> word = destination[(component * 4)..];
            if (field.IsFloat)
            {
                BinaryPrimitives.WriteSingleLittleEndian(word, number);
            }
            else if (field.IsInt)
            {
                BinaryPrimitives.WriteInt32LittleEndian(word, (int)number);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)MathF.Max(0, number));
            }
        }
    }

    /// <summary>
    /// The declared default as a param: the numbers of its initialiser in order (into r to a for a Color, and one
    /// number into every component, as <c>float4(0.5)</c> does), true as 1, anything else zero.
    /// </summary>
    private static MaterialParam GetDefault(ParamField field, string? text)
    {
        if (text is null || field.Type is ParamType.TextureRef or ParamType.Curve) return new MaterialParam();

        if (field.Type == ParamType.Bool)
        {
            return new MaterialParam { X = string.Equals(text, "true", StringComparison.Ordinal) ? 1 : 0 };
        }

        // A whole number in hex, as HLSL writes a mask or a none (0xFFFFu), which the names below would take apart.
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2).TrimEnd("uU"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
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
                values[i] = float.Parse(numbers[index].Value.TrimEnd('f', 'F', 'u', 'U'), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        return field.Type == ParamType.Color
            ? new MaterialParam { R = values[0], G = values[1], B = values[2], A = values[3] }
            : new MaterialParam { X = values[0], Y = values[1], Z = values[2], W = values[3] };
    }

    private static string StripComments(string text)
    {
        return CommentPattern().Replace(text, comment => new string(Array.ConvertAll(comment.Value.ToCharArray(), c => c is '\n' or '\r' ? c : ' ')));
    }

    [GeneratedRegex(@"\bstruct\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StructPattern();

    [GeneratedRegex(@"^(?<type>\w+)\s+(?<name>\w+)\s*(?::\s*(?<role>\w+))?\s*(?:=\s*(?<default>.+))?$", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MemberPattern();

    /// <summary>
    /// A member's <c>: Role</c> or <c>= default</c>, up to its <c>;</c>.
    /// </summary>
    [GeneratedRegex(@":\s*\w+|=\s*[^;]*", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TailPattern();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"-?\d+(\.\d+)?([eE][-+]?\d+)?[fFuU]?", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[A-Za-z_]\w*", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();
}

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
    /// A texture the renderer resolved into its pools, or <c>TextureNone</c>.
    /// </summary>
    TextureRef,

    /// <summary>
    /// A curve, as sixteen floats: where its first and last keys are, then its value at places evenly between.
    /// </summary>
    Curve,
}

/// <summary>
/// One member of a param struct: where it sits in the packed record, its declared default, and its role for global
/// illumination, <c>GiColor</c>, <c>GiColorMap</c>, <c>GiMetallic</c> or <c>GiEmissive</c>, or none; both are stripped
/// from the HLSL.
/// </summary>
internal sealed record ParamField(string Name, ParamType Type, int Offset, MaterialParam Default)
{
    internal string Role { get; init; } = string.Empty;

    internal int Components => Type switch
    {
        ParamType.Float or ParamType.Int or ParamType.Uint or ParamType.Bool or ParamType.TextureRef => 1,
        ParamType.Float2 or ParamType.Int2 or ParamType.Uint2 => 2,
        ParamType.Float3 or ParamType.Int3 or ParamType.Uint3 => 3,
        ParamType.Float4 or ParamType.Int4 or ParamType.Uint4 or ParamType.Color => 4,
        ParamType.Curve => 16,
        _ => 1,
    };

    internal int Size => Components * 4;

    internal bool IsFloat => Type is ParamType.Float or ParamType.Float2 or ParamType.Float3 or ParamType.Float4 or ParamType.Color;

    internal bool IsInt => Type is ParamType.Int or ParamType.Int2 or ParamType.Int3 or ParamType.Int4;
}
