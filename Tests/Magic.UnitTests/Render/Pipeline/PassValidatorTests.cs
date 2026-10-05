using DeferredRendererGem;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;
using Xunit;

namespace Magic.UnitTests.Render.Pipeline;

public sealed class PassValidatorTests
{
    [Fact]
    public void A_fullscreen_pass_with_two_writes_is_refused()
    {
        Pass pass = Fullscreen();
        pass.Writes = [new PassWrite { Name = "Hdr" }, new PassWrite { Name = "Ldr" }];

        Result result = PassValidator.Validate(pass, PipelineStage.Post, ShaderStage.Fragment, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_compute_pass_with_a_vertex_shader_is_refused()
    {
        Pass pass = Compute();

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Vertex, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void More_iterations_than_allowed_are_refused()
    {
        Pass pass = Compute();
        pass.Each.Max = PassValidator.MaxIterations + 1;

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Compute, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_dispatch_that_says_both_per_and_groups_is_refused()
    {
        Pass pass = Compute();
        pass.Dispatch.Groups = [1, 1, 1];

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Compute, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_buffer_sized_per_an_unknown_count_is_refused()
    {
        Pass pass = Compute();
        pass.Creates = [new PassCreate { Name = "Tiles", Kind = ResourceKind.Buffer, Per = "$tiles" }];

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Compute, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Creating_an_engine_name_is_refused()
    {
        Pass pass = Compute();
        pass.Creates = [new PassCreate { Name = "Hdr", Kind = ResourceKind.Texture }];

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Compute, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_frame_wide_pass_cannot_create_a_texture_that_follows_the_target()
    {
        Pass pass = Compute();
        pass.Creates = [new PassCreate { Name = "Mask", Kind = ResourceKind.Texture, Scale = 0.5f }];

        Result result = PassValidator.Validate(pass, PipelineStage.Gi, ShaderStage.Compute, null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_well_formed_compute_pass_is_accepted()
    {
        Pass pass = Compute();
        pass.Creates = [new PassCreate { Name = "Tiles", Kind = ResourceKind.Buffer, Per = "$lightCount", Stride = 16 }];

        Result result = PassValidator.Validate(pass, PipelineStage.Lighting, ShaderStage.Compute, null);

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_shader_that_uses_one_sampled_texture_fewer_than_the_pass_reads_fails_the_check()
    {
        Pass pass = Fullscreen();
        pass.Reads = [new PassRead { Name = "Hdr" }, new PassRead { Name = "Depth" }];
        CompiledShader shader = new() { Stage = GpuStage.Fragment, Samplers = 1 };

        Result result = PassValidator.Check(pass, shader, null, 0, _ => null);

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_shader_whose_bindings_match_the_pass_passes_the_check()
    {
        Pass pass = Compute();
        pass.Reads = [new PassRead { Name = "Depth" }, new PassRead { Name = "Lights" }];
        CompiledShader shader = new() { Stage = GpuStage.Compute, Samplers = 1, StorageBuffers = 1, ReadWriteStorageTextures = 1, ThreadCountX = 8, ThreadCountY = 8, ThreadCountZ = 1 };

        Result result = PassValidator.Check(pass, shader, null, 0, _ => null);

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_read_of_a_buffer_another_pass_made_counts_as_a_buffer()
    {
        Pass pass = Compute();
        pass.Reads = [new PassRead { Name = "Tiles" }];
        CompiledShader shader = new() { Stage = GpuStage.Compute, StorageBuffers = 1, ReadWriteStorageTextures = 1, ThreadCountX = 8, ThreadCountY = 8, ThreadCountZ = 1 };

        Result result = PassValidator.Check(pass, shader, null, 0, name => name == "Tiles" ? ResourceKind.Buffer : null);

        Assert.True(result.Ok);
    }

    [Fact]
    public void A_compute_shader_without_numthreads_fails_the_check()
    {
        Pass pass = Compute();
        CompiledShader shader = new() { Stage = GpuStage.Compute, ReadWriteStorageTextures = 1 };

        Result result = PassValidator.Check(pass, shader, null, 0, _ => null);

        Assert.True(result.Failed);
    }

    private static Pass Compute()
    {
        return new Pass
        {
            Kind = PassKind.Compute,
            Shader = new Handle<Shader>(1),
            Writes = [new PassWrite { Name = "Hdr" }],
            Dispatch = new PassDispatch { Per = "view" },
        };
    }

    private static Pass Fullscreen()
    {
        return new Pass
        {
            Kind = PassKind.Fullscreen,
            Shader = new Handle<Shader>(1),
            Reads = [new PassRead { Name = "Hdr" }],
            Writes = [new PassWrite { Name = "Ldr" }],
        };
    }
}
