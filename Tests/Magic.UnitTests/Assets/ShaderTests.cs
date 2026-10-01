using Magic.Contexts.Assets;
using Xunit;

namespace Magic.UnitTests.Assets;

public sealed class ShaderTests
{
    [Theory]
    [InlineData("Shaders/Mesh.vert.hlsl", ShaderStage.Vertex)]
    [InlineData("Shaders/Unlit.frag.hlsl", ShaderStage.Fragment)]
    [InlineData("Shaders/Cull/CullEarly.comp.hlsl", ShaderStage.Compute)]
    [InlineData("Shaders/Surfaces/Pbr.surf.hlsl", ShaderStage.Surface)]
    [InlineData("Shaders/Surfaces/PBR.SURF.HLSL", ShaderStage.Surface)]
    [InlineData("Shaders/Include/Surface.hlsli", ShaderStage.Include)]
    public void The_stage_comes_from_the_file_name(string path, ShaderStage expected)
    {
        Shader shader = new() { Path = path };

        ShaderStage stage = shader.Stage;

        Assert.Equal(expected, stage);
    }

    [Fact]
    public void Includes_lists_the_included_files_as_written()
    {
        Shader shader = new() { Text = "#include \"Include/Frame.hlsli\"\n  # include \"Include/Bindings.hlsli\"\nfloat4 main() { return 0; }\n" };

        string[] includes = shader.Includes;

        Assert.Equal(["Include/Frame.hlsli", "Include/Bindings.hlsli"], includes);
    }

    [Fact]
    public void An_include_inside_a_line_of_code_is_not_listed()
    {
        Shader shader = new() { Text = "// see #include \"Include/Frame.hlsli\"\n" };

        string[] includes = shader.Includes;

        Assert.Empty(includes);
    }
}
