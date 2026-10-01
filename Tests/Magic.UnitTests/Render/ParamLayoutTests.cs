using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Render;

public sealed class ParamLayoutTests
{
    private const string Pbr = """
        // the default surface
        struct MaterialParams
        {
            float4     color       : GiColor    = float4(0.8, 0.8, 0.8, 1.0);
            float      roughness                = 0.6;
            float      metallic; // no default
            float3     emissive    : GiEmissive = float3(0.0, 0.0, 0.0);
            TextureRef colorMap    : GiColorMap;
            bool       flip                     = true;
            int2       tiles                    = int2(2, 3);
        };

        Surface EvaluateSurface(SurfaceInputs input, MaterialParams m) { Surface s = DefaultSurface(input); return s; }
        """;

    [Fact]
    public void Members_keep_their_declaration_order()
    {
        ParamLayout layout = Parse();

        Assert.Equal(["color", "roughness", "metallic", "emissive", "colorMap", "flip", "tiles"], layout.Fields.Select(field => field.Name));
    }

    [Fact]
    public void Members_pack_without_crossing_a_16_byte_row()
    {
        ParamLayout layout = Parse();

        Assert.Equal([0, 16, 20, 32, 44, 48, 56], layout.Fields.Select(field => field.Offset));
    }

    [Fact]
    public void The_size_is_rounded_up_to_whole_rows()
    {
        ParamLayout layout = Parse();

        Assert.Equal(64, layout.Size);
    }

    [Fact]
    public void A_member_keeps_its_semantic()
    {
        ParamLayout layout = Parse();

        Assert.Equal("GiColor", layout.Fields[0].Semantic);
    }

    [Fact]
    public void A_member_keeps_its_default()
    {
        ParamLayout layout = Parse();

        Assert.Equal("float4(0.8, 0.8, 0.8, 1.0)", layout.Fields[0].Default);
    }

    [Fact]
    public void A_texture_ref_member_is_a_texture()
    {
        ParamLayout layout = Parse();

        Assert.Equal(ParamType.TextureRef, layout.Fields[4].Type);
    }

    [Theory]
    [InlineData("struct MaterialParams { float4x4 m; };", "MaterialParams", true)]       // a matrix does not fit a row
    [InlineData("struct MaterialParams { float a; float a; };", "MaterialParams", true)] // duplicate member
    [InlineData("struct PassParams { TextureRef t; };", "PassParams", false)]            // textures not allowed
    [InlineData("float x;", "MaterialParams", true)]                                      // no struct
    [InlineData("", "MaterialParams", true)]
    public void What_the_record_cannot_hold_is_refused(string hlsl, string name, bool allowTextures)
    {
        bool ok = ParamLayout.Parse(hlsl, name, allowTextures).Ok;

        Assert.False(ok);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)] // one row past the record
    public void A_struct_fits_up_to_eight_rows(int rows, bool expected)
    {
        string hlsl = "struct MaterialParams { " + string.Concat(Enumerable.Range(0, rows).Select(i => $"float4 v{i}; ")) + "};";

        bool ok = ParamLayout.Parse(hlsl, "MaterialParams", true).Ok;

        Assert.Equal(expected, ok);
    }

    [Fact]
    public void Stripping_keeps_every_character_position()
    {
        ParamLayout layout = Parse();

        string stripped = ParamLayout.BlankDefaults(Pbr, layout.StructSpan);

        Assert.Equal(Pbr.Length, stripped.Length);
    }

    [Fact]
    public void Stripping_keeps_every_line()
    {
        ParamLayout layout = Parse();

        string stripped = ParamLayout.BlankDefaults(Pbr, layout.StructSpan);

        Assert.Equal(Pbr.Count(character => character == '\n'), stripped.Count(character => character == '\n'));
    }

    [Theory]
    [InlineData("GiColor")] // a semantic
    [InlineData("= 0.6")]   // a default
    public void Stripping_blanks_semantics_and_defaults(string blanked)
    {
        ParamLayout layout = Parse();

        string stripped = ParamLayout.BlankDefaults(Pbr, layout.StructSpan);

        Assert.DoesNotContain(blanked, stripped);
    }

    [Theory]
    [InlineData("float      roughness")]
    [InlineData("float      metallic; // no default")]
    [InlineData("Surface EvaluateSurface(SurfaceInputs input, MaterialParams m)")]
    public void Stripping_keeps_declarations_comments_and_code_outside_the_struct(string kept)
    {
        ParamLayout layout = Parse();

        string stripped = ParamLayout.BlankDefaults(Pbr, layout.StructSpan);

        Assert.Contains(kept, stripped);
    }

    [Fact]
    public void Stripping_leaves_an_initialiser_inside_a_comment()
    {
        const string hlsl = "struct MaterialParams\n{\n    float scale; // scale = 2\n};\n";
        ParamLayout layout = ParamLayout.Parse(hlsl, "MaterialParams", true).Payload;

        string stripped = ParamLayout.BlankDefaults(hlsl, layout.StructSpan);

        Assert.Equal(hlsl, stripped);
    }

    [Fact]
    public void The_loader_is_named_as_asked()
    {
        ParamLayout layout = Parse();

        string loader = layout.EmitLoader("LoadMaterialParams", "Materials");

        Assert.Contains("MaterialParams LoadMaterialParams(uint slot)", loader);
    }

    [Theory]
    [InlineData("loaded.color = asfloat(record.words[0].xyzw);")]
    [InlineData("loaded.roughness = asfloat(record.words[1].x);")]
    [InlineData("loaded.metallic = asfloat(record.words[1].y);")]
    [InlineData("loaded.emissive = asfloat(record.words[2].xyz);")]
    [InlineData("loaded.colorMap = record.words[2].w;")]
    [InlineData("loaded.flip = record.words[3].x != 0u;")]
    [InlineData("loaded.tiles = asint(record.words[3].zw);")]
    public void The_loader_reads_each_member_from_its_row_and_swizzle(string line)
    {
        ParamLayout layout = Parse();

        string loader = layout.EmitLoader("LoadMaterialParams", "Materials");

        Assert.Contains(line, loader);
    }

    [Fact]
    public void Packing_writes_a_given_value_at_its_offset()
    {
        byte[] record = Write(new() { ["roughness"] = Param.Of(0.25f) });

        Assert.Equal(0.25f, BitConverter.ToSingle(record, 16));
    }

    [Fact]
    public void Packing_falls_back_to_the_declared_default()
    {
        byte[] record = Write([]);

        Assert.Equal(0.6f, BitConverter.ToSingle(record, 16));
    }

    [Fact]
    public void Packing_writes_an_integer_default_as_an_integer()
    {
        byte[] record = Write([]);

        Assert.Equal(3, BitConverter.ToInt32(record, 60));
    }

    [Fact]
    public void Packing_writes_zero_for_a_member_with_no_default()
    {
        byte[] record = Write([]);

        Assert.Equal(0f, BitConverter.ToSingle(record, 20));
    }

    [Fact]
    public void Packing_resolves_a_texture_through_the_callback()
    {
        byte[] record = Write(new() { ["colorMap"] = Param.Of(new Handle<Texture>(36)) });

        Assert.Equal(0x00020005u, BitConverter.ToUInt32(record, 44));
    }

    [Fact]
    public void Packing_writes_false_as_zero()
    {
        byte[] record = Write(new() { ["flip"] = Param.Of(false) });

        Assert.Equal(0u, BitConverter.ToUInt32(record, 48));
    }

    [Fact]
    public void Keys_the_layout_does_not_declare_are_reported()
    {
        ParamLayout layout = Parse();
        Dictionary<string, Param> values = new() { ["roughness"] = Param.Of(0.25f), ["bogus"] = Param.Of(1f) };

        IEnumerable<string> unknown = layout.UnknownKeys(values);

        Assert.Equal(["bogus"], unknown);
    }

    [Fact]
    public void A_vector_default_parses_as_a_param()
    {
        ParamLayout layout = Parse();

        Param color = layout.Fields[0].DefaultParam;

        Assert.Equal(new Vector4(0.8f, 0.8f, 0.8f, 1f), color.Vector);
    }

    [Fact]
    public void A_true_default_parses_as_one()
    {
        ParamLayout layout = Parse();

        Param flip = layout.Fields[5].DefaultParam;

        Assert.Equal(1f, flip.X);
    }

    private static ParamLayout Parse()
    {
        return ParamLayout.Parse(Pbr, "MaterialParams", allowTextures: true).Payload;
    }

    /// <summary>Packs <paramref name="values"/> into a record; texture 36 resolves to 0x00020005.</summary>
    private static byte[] Write(Dictionary<string, Param> values)
    {
        byte[] record = new byte[ParamLayout.RecordBytes];

        Parse().Write(values, texture => texture.Id == 36 ? 0x00020005u : uint.MaxValue, record);

        return record;
    }
}
