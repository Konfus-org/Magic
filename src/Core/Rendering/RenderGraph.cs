using System.Collections.Immutable;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Shaders;
using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// A frame's passes as data: begin a pass in a layer, say what it uses, add its commands, end it, and take the frame
/// with <see cref="Finish"/>. Core holds one, every gem's Render records into it, and Core hands what it finishes to
/// <see cref="IGraphicsApi.Present"/>, which puts in the barriers the uses need. Each method's parameters say which of
/// a <see cref="GpuCommand"/>'s fields its kind reads. Render thread.
/// </summary>
public sealed class RenderGraph
{
    private static readonly RenderLayer[] _layers = Enum.GetValues<RenderLayer>();

    private readonly List<GpuPass> _passes = [];

    private readonly ImmutableArray<GpuUse>.Builder _uses = ImmutableArray.CreateBuilder<GpuUse>();

    private readonly ImmutableArray<GpuCommand>.Builder _commands = ImmutableArray.CreateBuilder<GpuCommand>();

    private GpuPass? _pass;

    /// <summary>
    /// The GPU provider the graph's names resolve through, set by Core once the gems are loaded; none while
    /// no gem provides the GPU.
    /// </summary>
    internal IGraphicsApi? Gpu { get; set; }

    /// <summary>
    /// The texture under the description's name, made by the GPU provider on the first ask and kept while the same
    /// description is asked for, its heap id known at once to write into constants.
    /// </summary>
    public Result<GpuTexture> Texture(in TextureDescription description) =>
        Gpu?.GetTexture(description) ?? Result.Failure($"there is no GPU provider to make the {description.Name}.");

    /// <summary>
    /// Whether the GPU provider has built <paramref name="shader"/>, the first ask starting it; failed while there is no
    /// provider.
    /// </summary>
    public GpuState GetState(in ShaderEntry shader) => Gpu?.GetState(shader) ?? GpuState.Failed;

    /// <summary>
    /// Begins a compute pass running <paramref name="shader"/> with <paramref name="constants"/> pushed to it: say what
    /// it reads and writes on what this returns, then dispatch, which ends it. The GPU provider leaves the pass out of a
    /// frame while the shader is not built.
    /// </summary>
    public ComputePass Compute(string name, in ShaderEntry shader, in GpuConstants constants)
    {
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Compute,
                Shader = shader,
            }
        );
        PushConstants(constants);

        return new ComputePass(this);
    }

    public void BeginComputePass(string name, RenderLayer layer = RenderLayer.Picture) =>
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Compute,
                Layer = layer,
            }
        );

    /// <summary>
    /// Begins a draw pass into <paramref name="targets"/>, each target used as a colour or depth target.
    /// </summary>
    public void BeginDrawPass(string name, in GpuRenderTargets targets, RenderLayer layer = RenderLayer.Picture)
    {
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Draw,
                Targets = targets,
                Layer = layer,
            }
        );
        foreach (GpuTarget color in targets.Colors)
        {
            Use(color.Texture, GpuAccess.ColorTarget, color.Level);
        }

        if (targets.Depth is { } depth)
            Use(depth.Texture, GpuAccess.DepthTarget, depth.Level);
    }

    public void BeginTransferPass(string name, RenderLayer layer = RenderLayer.Picture) =>
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Transfer,
                Layer = layer,
            }
        );

    public void EndPass()
    {
        GpuPass pass = _pass ?? throw new InvalidOperationException("No pass has begun.");
        _passes.Add(pass with { Uses = _uses.DrainToImmutable(), Commands = _commands.DrainToImmutable() });
        _pass = null;
    }

    /// <summary>
    /// The frame's passes, layer by layer and in the order they were recorded within one, and the graph empty for the
    /// next frame. A pass still open is left out.
    /// </summary>
    public GpuCommands Finish()
    {
        DropOpenPass();
        ImmutableArray<GpuPass>.Builder ordered = ImmutableArray.CreateBuilder<GpuPass>(_passes.Count);
        foreach (RenderLayer layer in _layers)
        {
            foreach (GpuPass pass in _passes)
            {
                if (pass.Layer == layer)
                    ordered.Add(pass);
            }
        }

        _passes.Clear();

        return new(ordered.MoveToImmutable());
    }

    /// <summary>
    /// Says the pass touches <paramref name="buffer"/> as <paramref name="access"/>.
    /// </summary>
    public void Use(GpuBuffer buffer, GpuAccess access)
    {
        ThrowIfNotInPass();
        _uses.Add(
            new()
            {
                Id = buffer.Id,
                IsTexture = false,
                Access = access,
            }
        );
    }

    /// <summary>
    /// Says the pass touches <paramref name="texture"/> as <paramref name="access"/>, every level or
    /// <paramref name="level"/> alone.
    /// </summary>
    public void Use(GpuTexture texture, GpuAccess access, int level = -1)
    {
        ThrowIfNotInPass();
        _uses.Add(
            new()
            {
                Id = texture.Id,
                IsTexture = true,
                Access = access,
                Level = level,
            }
        );
    }

    public void BindPipeline(GpuPipeline pipeline) =>
        Add(new() { Kind = GpuCommandKind.BindPipeline, Id = pipeline.Id });

    public void PushConstants(in GpuConstants constants) =>
        Add(new() { Kind = GpuCommandKind.PushConstants, Constants = constants });

    /// <summary>
    /// Builds an acceleration structure: a bottom-level one over its triangles, a top-level one over the first
    /// <paramref name="instanceCount"/> instances of its instance buffer. The build waits for every command before
    /// it and every command after waits for the build.
    /// </summary>
    public void BuildAcceleration(GpuAcceleration acceleration, uint instanceCount = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BuildAcceleration,
                Id = acceleration.Id,
                X = instanceCount,
            }
        );

    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.Dispatch,
                X = groupsX,
                Y = groupsY,
                Z = groupsZ,
            }
        );

    /// <summary>
    /// Three unsigned group counts read from <paramref name="arguments"/> at <paramref name="offset"/>.
    /// </summary>
    public void DispatchIndirect(GpuBuffer arguments, ulong offset) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DispatchIndirect,
                Id = arguments.Id,
                Offset = offset,
            }
        );

    /// <summary>
    /// The whole target when every argument is 0.
    /// </summary>
    public void SetScissor(uint x, uint y, uint width, uint height) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.SetScissor,
                X = x,
                Y = y,
                Z = width,
                W = height,
            }
        );

    /// <summary>
    /// 32-bit indices from <paramref name="offset"/> on.
    /// </summary>
    public void BindIndexBuffer(GpuBuffer indices, ulong offset = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BindIndexBuffer,
                Id = indices.Id,
                Offset = offset,
            }
        );

    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, uint firstInstance = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndexed,
                X = indices,
                Y = instances,
                Z = firstIndex,
                W = firstInstance,
            }
        );

    /// <summary>
    /// As many draws as the unsigned count at <paramref name="countOffset"/> in <paramref name="count"/>, up to
    /// <paramref name="maxDraws"/>, their arguments <paramref name="stride"/> bytes apart from
    /// <paramref name="argumentsOffset"/> in <paramref name="arguments"/>, each four unsigned integers: vertices,
    /// instances, first vertex, first instance.
    /// </summary>
    public void DrawIndirectCount(
        GpuBuffer arguments,
        ulong argumentsOffset,
        GpuBuffer count,
        ulong countOffset,
        uint maxDraws,
        uint stride = 16
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndirectCount,
                Id = arguments.Id,
                Offset = argumentsOffset,
                Id2 = count.Id,
                Offset2 = countOffset,
                X = maxDraws,
                Y = stride,
            }
        );

    public void CopyBuffer(
        GpuBuffer source,
        ulong sourceOffset,
        GpuBuffer destination,
        ulong destinationOffset,
        ulong bytes
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBuffer,
                Id = source.Id,
                Offset = sourceOffset,
                Id2 = destination.Id,
                Offset2 = destinationOffset,
                Size = bytes,
            }
        );

    /// <summary>
    /// A whole level of a layer, tightly packed from <paramref name="sourceOffset"/> on.
    /// </summary>
    public void CopyBufferToTexture(
        GpuBuffer source,
        ulong sourceOffset,
        GpuTexture destination,
        int level = 0,
        int layer = 0
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBufferToTexture,
                Id = source.Id,
                Offset = sourceOffset,
                Id2 = destination.Id,
                X = (uint)level,
                Y = (uint)layer,
            }
        );

    /// <summary>
    /// A whole level of a layer, tightly packed from <paramref name="destinationOffset"/> on.
    /// </summary>
    public void CopyTextureToBuffer(
        GpuTexture source,
        int level,
        int layer,
        GpuBuffer destination,
        ulong destinationOffset
    ) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyTextureToBuffer,
                Id = source.Id,
                X = (uint)level,
                Y = (uint)layer,
                Id2 = destination.Id,
                Offset2 = destinationOffset,
            }
        );

    /// <summary>
    /// Level 0 of one texture stretched over level 0 of another, filtered linearly; the formats may differ.
    /// </summary>
    public void BlitTexture(GpuTexture source, GpuTexture destination) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.BlitTexture,
                Id = source.Id,
                Id2 = destination.Id,
            }
        );

    /// <summary>
    /// Every 4 bytes from <paramref name="offset"/> for <paramref name="bytes"/>, or to the end when 0, set to
    /// <paramref name="value"/>.
    /// </summary>
    public void FillBuffer(GpuBuffer buffer, uint value, ulong offset = 0, ulong bytes = 0) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.FillBuffer,
                Id = buffer.Id,
                Offset = offset,
                Size = bytes,
                X = value,
            }
        );

    /// <summary>
    /// A thread for each of <paramref name="x"/> by <paramref name="y"/> by <paramref name="z"/>, in as many groups of
    /// the pass's shader as cover them: in a pass <see cref="Compute"/> began.
    /// </summary>
    internal void DispatchThreads(uint x, uint y, uint z) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DispatchThreads,
                X = x,
                Y = y,
                Z = z,
            }
        );

    private void Begin(in GpuPass pass)
    {
        DropOpenPass();
        _pass = pass;
    }

    /// <summary>
    /// A pass a gem began and never ended, left out, as the gem threw in its Render or forgot: the other gems' passes
    /// still draw.
    /// </summary>
    private void DropOpenPass()
    {
        if (_pass is not { } open)
            return;

        Log.Error($"The pass {open.Name} was never ended, so it is left out of the frame.");
        _uses.Clear();
        _commands.Clear();
        _pass = null;
    }

    private void Add(in GpuCommand command)
    {
        ThrowIfNotInPass();
        _commands.Add(command);
    }

    private void ThrowIfNotInPass()
    {
        if (_pass is null)
            throw new InvalidOperationException("No pass has begun.");
    }
}
