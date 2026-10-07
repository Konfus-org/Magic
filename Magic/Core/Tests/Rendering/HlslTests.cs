using Magic.Contexts.Rendering;
using Magic.Services;
using Xunit;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RenderingTests;

public sealed class HlslTests
{
    [Fact]
    public void A_struct_is_written_with_its_fields_in_order()
    {
        string header = Hlsl.Write([typeof(Row)]);

        Assert.Contains("struct Row\n{\n    float4 sphere;\n    uint slot;\n    float fade;\n    int offset;\n    uint pad;\n};", header);
    }

    [Fact]
    public void A_renamed_struct_is_written_under_its_name()
    {
        string header = Hlsl.Write([typeof(Renamed)]);

        Assert.Contains("struct GpuRenamed\n", header);
    }

    [Fact]
    public void Vectors_matrices_and_inline_arrays_are_their_hlsl_types()
    {
        string header = Hlsl.Write([typeof(Shapes)]);

        Assert.Contains("    float4x4 world;\n    uint4 flags;\n    float4 planes[3];\n", header);
    }

    [Fact]
    public void An_enum_is_a_constant_per_member()
    {
        string header = Hlsl.Write([typeof(Mode)]);

        Assert.Contains("static const uint ModeBright = 2u;", header);
    }

    [Fact]
    public void A_class_has_its_constants_written()
    {
        string header = Hlsl.Write([typeof(Sizes)]);

        Assert.Contains("static const int TileSize = 16;", header);
    }

    [Fact]
    public void A_marked_constant_can_be_a_define()
    {
        string header = Hlsl.Write([typeof(Grouped)]);

        Assert.Contains("#define GROUP_SIZE 64", header);
    }

    [Fact]
    public void Includes_lists_the_included_files_as_written()
    {
        string source = "#include \"Include/Frame.hlsli\"\n  # include \"Include/Bindings.hlsli\"\nfloat4 main() { return 0; }\n";

        string[] includes = Hlsl.Includes(source);

        Assert.Equal(["Include/Frame.hlsli", "Include/Bindings.hlsli"], includes);
    }

    [Fact]
    public void An_include_with_a_comment_after_it_is_listed()
    {
        string source = "#include \"Include/Frame.hlsli\" // the frame block\r\n";

        string[] includes = Hlsl.Includes(source);

        Assert.Equal(["Include/Frame.hlsli"], includes);
    }

    [Fact]
    public void An_include_inside_a_line_of_code_is_not_listed()
    {
        string source = "// see #include \"Include/Frame.hlsli\"\n";

        string[] includes = Hlsl.Includes(source);

        Assert.Empty(includes);
    }

    [Fact]
    public void A_bool_member_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => Hlsl.Write([typeof(WithBool)]));
    }

    [Hlsl]
    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    {
        public Vector4 Sphere;
        public uint Slot;
        public float Fade;
        public int Offset;
        public uint Pad;
    }

    [Hlsl("GpuRenamed")]
    [StructLayout(LayoutKind.Sequential)]
    private struct Renamed
    {
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Four
    {
        public uint X, Y, Z, W;
    }

    [InlineArray(3)]
    private struct Planes
    {
        private Vector4 _element;
    }

    [Hlsl]
    [StructLayout(LayoutKind.Sequential)]
    private struct Shapes
    {
        public Matrix4x4 World;
        public Four Flags;
        public Planes Planes;
    }

    [Hlsl]
    private enum Mode : byte
    {
        Dim = 1,
        Bright = 2,
    }

    [Hlsl]
    private static class Sizes
    {
        public const int TileSize = 16;
    }

    private static class Grouped
    {
        [Hlsl("GROUP_SIZE", Define = true)]
        public const int Size = 64;
    }

    [Hlsl]
    [StructLayout(LayoutKind.Sequential)]
    private struct WithBool
    {
        public bool Flag;
    }
}
