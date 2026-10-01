using Magic.Contexts.Rendering;
using Magic.Systems.Rendering;
using Xunit;

namespace Magic.UnitTests.Systems;

public sealed class SurfaceComposerTests
{
    private const string Pbr = """
        struct MaterialParams
        {
            float4 color : GiColor = float4(0.8, 0.8, 0.8, 1.0);
        };

        Surface EvaluateSurface(SurfaceInputs input, MaterialParams m) { Surface s = DefaultSurface(input); return s; }
        """;

    [Fact]
    public void A_surface_without_evaluate_surface_is_refused()
    {
        bool ok = SurfaceComposer.Read(1, "x", "struct MaterialParams { float a; };").Ok;

        Assert.False(ok);
    }

    [Fact]
    public void A_surface_without_material_params_is_refused()
    {
        bool ok = SurfaceComposer.Read(1, "x", "Surface EvaluateSurface(SurfaceInputs input, MaterialParams m) { }").Ok;

        Assert.False(ok);
    }

    [Theory]
    [InlineData((int)SurfaceVariant.Masked, "#define SURFACE_MASKED 1\n")]
    [InlineData((int)SurfaceVariant.DoubleSided, "#define SURFACE_DOUBLE_SIDED 1\n")]
    [InlineData((int)SurfaceVariant.FailureForced, "#define FAILURE_FORCE 1\n")]
    public void Composing_defines_each_flag_of_the_variant(int variant, string define)
    {
        string composed = SurfaceComposer.Compose(Surface(), (SurfaceVariant)variant, "t", "x");

        Assert.Contains(define, composed);
    }

    [Fact]
    public void Composing_defines_the_variant_before_anything_else()
    {
        string composed = SurfaceComposer.Compose(Surface(), SurfaceVariant.Masked, "t", "x");

        Assert.StartsWith("#define", composed);
    }

    [Theory]
    [InlineData("#line 1 \"Shaders/Surfaces/Pbr.surf.hlsl\"\n")]
    [InlineData("#line 1 \"Shaders/Templates/Forward.frag.hlsl\"\nfloat4 main()")]
    public void Composing_maps_each_part_back_to_its_file_with_a_line_directive(string directive)
    {
        string composed = SurfaceComposer.Compose(
            Surface(),
            SurfaceVariant.Masked,
            "Shaders/Templates/Forward.frag.hlsl",
            "float4 main() : SV_Target0 { return 0; }");

        Assert.Contains(directive, composed);
    }

    [Fact]
    public void Composing_includes_the_loader()
    {
        string composed = SurfaceComposer.Compose(Surface(), SurfaceVariant.Masked, "t", "x");

        Assert.Contains("MaterialParams LoadMaterialParams(uint slot)", composed);
    }

    [Fact]
    public void Composing_strips_the_declarations()
    {
        string composed = SurfaceComposer.Compose(Surface(), SurfaceVariant.Masked, "t", "x");

        Assert.DoesNotContain("GiColor", composed);
    }

    private static SurfaceSource Surface()
    {
        return SurfaceComposer.Read(10001, "Shaders/Surfaces/Pbr.surf.hlsl", Pbr).Payload;
    }
}
