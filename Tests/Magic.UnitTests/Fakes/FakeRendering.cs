using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Utils;
using System.Collections.Concurrent;

namespace Magic.UnitTests.Fakes;

/// <summary>
/// An <see cref="IRendering"/> that makes handles, compiles every shader to one byte, and records what it is handed.
/// </summary>
internal sealed class FakeRendering : IRendering
{
    private uint _next = 1;

    public List<RenderCommandType[]> Submitted { get; } = [];
    public List<byte[]> Uploaded { get; } = [];

    /// <summary>
    /// The HLSL of every compile, in no order: compiles run on workers.
    /// </summary>
    public ConcurrentBag<string> Compiled { get; } = [];

    public int Released { get; private set; }

    /// <summary>
    /// What every window last showed, as <see cref="Read(GpuTexture)"/> answers it; null when nothing was.
    /// </summary>
    public CapturedFrame? Shown { get; set; }

    public bool Debug { get; set; }
    public string Device => "Fake GPU";
    public string ShaderFormat => "FAKE";
    public GpuFormat DepthFormat => GpuFormat.D32Float;

    public GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes) => new(_next++);
    public GpuTexture CreateTexture(in TextureDesc desc) => new(_next++);
    public GpuSampler CreateSampler(in SamplerDesc desc) => new(_next++);
    public GpuPipeline CreatePipeline(PipelineDesc desc) => new(_next++);
    public GpuPipeline CreateComputePipeline(CompiledShader shader) => new(_next++);

    public Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string includeDirectory)
    {
        Compiled.Add(hlsl);
        return Result<CompiledShader>.Success(new CompiledShader { Stage = stage, Code = [1] });
    }

    public void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data) => Uploaded.Add(data.ToArray());
    public void Upload(in TextureRegion region, ReadOnlySpan<byte> data) { }
    public void Copy(in TextureRegion source, in TextureRegion destination) { }
    public void Copy(GpuBuffer source, uint sourceOffset, GpuBuffer destination, uint destinationOffset, uint bytes) { }
    public void Release(GpuBuffer buffer) => Released++;
    public void Release(GpuTexture texture) => Released++;
    public void Release(GpuSampler sampler) => Released++;
    public void Release(GpuPipeline pipeline) => Released++;
    public GpuFormat WindowFormat(uint window) => GpuFormat.Bgra8Unorm;

    public float Submit(RenderCommands commands)
    {
        RenderCommandType[] types = new RenderCommandType[commands.Count];
        for (int i = 0; i < types.Length; i++)
            types[i] = commands.Commands[i].Type;

        Submitted.Add(types);
        commands.Clear();
        return 0f;
    }

    public Result<byte[]> Read(GpuBuffer buffer, uint bytes) => Result<byte[]>.Failure("fake");

    public Result<CapturedFrame> Read(GpuTexture texture)
    {
        return texture.IsWindow && Shown is { } shown ? Result<CapturedFrame>.Success(shown) : Result<CapturedFrame>.Failure("fake");
    }
}
