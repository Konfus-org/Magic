using Magic.Contexts.Rendering;
using Xunit;

namespace Magic.UnitTests.Fakes;

public sealed class FakeRenderingTests
{
    private const string Source = """
        #include "Include/Frame.hlsli"
        #define GROUP_SIZE 8
        Texture2D Albedo : READ(0);
        SamplerState AlbedoSampler : SAMPLER(0);
        Texture2D<float> Mask : READ(1);
        StructuredBuffer<uint> Lights : READ(2);
        RWTexture2D<float4> Hdr : WRITE(0);
        RWStructuredBuffer<uint> Counts : WRITE(1);
        [numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
        void main(uint3 id : SV_DispatchThreadID) { }
        """;

    [Fact]
    public void A_texture_with_a_sampler_is_a_sampled_texture()
    {
        CompiledShader shader = FakeRendering.Reflect(Source, GpuStage.Compute);

        Assert.Equal(1u, shader.Samplers);
    }

    [Fact]
    public void A_texture_without_a_sampler_is_a_storage_texture()
    {
        CompiledShader shader = FakeRendering.Reflect(Source, GpuStage.Compute);

        Assert.Equal(1u, shader.StorageTextures);
    }

    [Fact]
    public void Buffers_and_writes_are_counted_by_class()
    {
        CompiledShader shader = FakeRendering.Reflect(Source, GpuStage.Compute);

        Assert.Equal((1u, 1u, 1u), (shader.StorageBuffers, shader.ReadWriteStorageTextures, shader.ReadWriteStorageBuffers));
    }

    [Fact]
    public void Thread_counts_come_from_numthreads_through_a_define()
    {
        CompiledShader shader = FakeRendering.Reflect(Source, GpuStage.Compute);

        Assert.Equal((8u, 8u, 1u), (shader.ThreadCountX, shader.ThreadCountY, shader.ThreadCountZ));
    }

    [Fact]
    public void A_binding_under_a_false_if_is_not_counted()
    {
        const string source = """
            #define OCCLUSION 0
            StructuredBuffer<uint> Always : READ(0);
            #if OCCLUSION
            StructuredBuffer<uint> Never : READ(1);
            #endif
            """;

        CompiledShader shader = FakeRendering.Reflect(source, GpuStage.Compute);

        Assert.Equal(1u, shader.StorageBuffers);
    }

    [Fact]
    public void A_binding_under_an_ifdef_of_a_defined_name_is_counted()
    {
        const string source = """
            #define OCCLUSION 1
            StructuredBuffer<uint> Always : READ(0);
            #ifdef OCCLUSION
            StructuredBuffer<uint> Also : READ(1);
            #endif
            """;

        CompiledShader shader = FakeRendering.Reflect(source, GpuStage.Compute);

        Assert.Equal(2u, shader.StorageBuffers);
    }
}
