using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Utils;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Magic.UnitTests.Fakes;

/// <summary>
/// An <see cref="IRendering"/> that makes handles, compiles every shader to one byte, and records what it is handed. A
/// compile reflects the binding counts the way the renderer's validator counts them, from the HLSL text alone: every
/// <c>READ(n)</c> declaration is a sampled texture (when a <c>SAMPLER(n)</c> pairs with it), a storage texture or a
/// storage buffer by its type; every <c>WRITE(n)</c> a read-write texture or buffer; <c>[numthreads]</c> gives the
/// thread counts, with a <c>#define</c> of the same text standing in for a number. Includes are not read: a shader
/// declares its bindings in its own file. The one preprocessor rule honoured is <c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>
/// on a name the text itself defines, so a variant declares only its own bindings.
/// </summary>
internal sealed partial class FakeRendering : IRendering
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

    /// <summary>
    /// Set by a test to stand for the device made again.
    /// </summary>
    public uint Generation { get; set; }
    public GpuFormat DepthFormat => GpuFormat.D32Float;

    public GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes) => new(_next++);
    public GpuTexture CreateTexture(in TextureDesc desc) => new(_next++);
    public GpuSampler CreateSampler(in SamplerDesc desc) => new(_next++);
    public GpuPipeline CreatePipeline(PipelineDesc desc) => new(_next++);
    public GpuPipeline CreateComputePipeline(CompiledShader shader) => new(_next++);

    public Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string includeDirectory)
    {
        Compiled.Add(hlsl);
        return Result<CompiledShader>.Success(Reflect(hlsl, stage));
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

    /// <summary>
    /// The binding counts of <paramref name="hlsl"/> as the renderer's validator expects them, from the text alone.
    /// </summary>
    public static CompiledShader Reflect(string hlsl, GpuStage stage)
    {
        string live = LiveText(hlsl, out Dictionary<string, string> defines);
        CompiledShader shader = new() { Stage = stage, Code = [1], UniformBuffers = live.Contains("cbuffer", StringComparison.Ordinal) || live.Contains("#include", StringComparison.Ordinal) ? 1u : 0u };

        HashSet<string> samplers = [];
        foreach (Match sampler in SamplerPattern().Matches(live))
            samplers.Add(sampler.Groups["index"].Value);

        foreach (Match read in ReadPattern().Matches(live))
        {
            string type = read.Groups["type"].Value;
            if (type.EndsWith("Buffer", StringComparison.Ordinal))
                shader.StorageBuffers++;
            else if (samplers.Contains(read.Groups["index"].Value))
                shader.Samplers++;
            else
                shader.StorageTextures++;
        }

        foreach (Match write in WritePattern().Matches(live))
        {
            if (write.Groups["type"].Value.EndsWith("Buffer", StringComparison.Ordinal))
                shader.ReadWriteStorageBuffers++;
            else
                shader.ReadWriteStorageTextures++;
        }

        Match threads = ThreadsPattern().Match(live);
        if (threads.Success)
        {
            shader.ThreadCountX = Count(threads.Groups["x"].Value, defines);
            shader.ThreadCountY = Count(threads.Groups["y"].Value, defines);
            shader.ThreadCountZ = Count(threads.Groups["z"].Value, defines);
        }

        return shader;
    }

    /// <summary>
    /// The text with the lines under a false <c>#if</c>, <c>#ifdef</c> or <c>#ifndef</c> of a name the text defines (or
    /// does not) taken out, the defines read in order as a preprocessor would (one under a false branch counts for
    /// nothing). Any other condition is taken as true.
    /// </summary>
    private static string LiveText(string hlsl, out Dictionary<string, string> defines)
    {
        defines = [];
        List<string> kept = [];
        Stack<bool> live = new();
        foreach (string line in hlsl.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                live.Push(Condition(trimmed, defines) && live.All(outer => outer));
                continue;
            }

            if (trimmed.StartsWith("#else", StringComparison.Ordinal))
            {
                bool inner = live.Pop();
                live.Push(!inner && live.All(outer => outer));
                continue;
            }

            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (live.Count > 0)
                    live.Pop();
                continue;
            }

            if (live.Count != 0 && !live.Peek())
                continue;

            Match define = DefinePattern().Match(trimmed);
            if (define.Success)
                defines[define.Groups["name"].Value] = define.Groups["value"].Value.Trim();
            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static bool Condition(string directive, Dictionary<string, string> defines)
    {
        Match match = ConditionPattern().Match(directive);
        if (!match.Success)
            return true;

        string name = match.Groups["name"].Value;
        return match.Groups["kind"].Value switch
        {
            "ifdef" => defines.ContainsKey(name),
            "ifndef" => !defines.ContainsKey(name),
            _ => match.Groups["defined"].Success ? defines.ContainsKey(name) : defines.TryGetValue(name, out string? value) && value != "0",
        };
    }

    private static uint Count(string token, Dictionary<string, string> defines)
    {
        if (uint.TryParse(token.Trim(), out uint count))
            return count;

        return defines.TryGetValue(token.Trim(), out string? value) && uint.TryParse(value, out uint defined) ? defined : 1;
    }

    [GeneratedRegex(@"^\s*(?<type>Texture2D|Texture2DArray|Texture3D|TextureCube|StructuredBuffer|ByteAddressBuffer|Buffer)(<[^>]*>)?\s+\w+(\[\w*\])?\s*:\s*READ\(\s*(?<index>\w+)\s*\)", RegexOptions.Multiline)]
    private static partial Regex ReadPattern();

    [GeneratedRegex(@"^\s*Sampler(Comparison)?State\s+\w+\s*:\s*SAMPLER\(\s*(?<index>\w+)\s*\)", RegexOptions.Multiline)]
    private static partial Regex SamplerPattern();

    [GeneratedRegex(@"^\s*(?<type>RWTexture2D|RWTexture2DArray|RWTexture3D|RWStructuredBuffer|RWByteAddressBuffer|RWBuffer)(<[^>]*>)?\s+\w+\s*:\s*WRITE\(", RegexOptions.Multiline)]
    private static partial Regex WritePattern();

    [GeneratedRegex(@"\[\s*numthreads\s*\(\s*(?<x>[^,]+),\s*(?<y>[^,]+),\s*(?<z>[^)]+)\)\s*\]")]
    private static partial Regex ThreadsPattern();

    [GeneratedRegex(@"^\s*#define\s+(?<name>\w+)(?<value>[^\n]*)$")]
    private static partial Regex DefinePattern();

    [GeneratedRegex(@"^#(?<kind>ifdef|ifndef|if)\s+(?:(?<defined>defined)\s*\(?\s*)?(?<name>\w+)")]
    private static partial Regex ConditionPattern();
}
