using Magic.Contexts.Assets;
using Xunit;

namespace RenderingTests;

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
}
