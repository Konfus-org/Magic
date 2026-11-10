using System.Collections.Immutable;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Utils;

namespace Magic.Graphics;

/// <summary>
/// A frame's passes as data: a compute or draw pass named by its shaders, told what it reads and ended by what it
/// dispatches or draws, and transfers (uploads, clears, blits, readbacks and builds), which the graph gathers into
/// passes of their own; take the frame with <see cref="Finish"/>. Core holds one, every gem's Render records into it,
/// and Core hands what it finishes to <see cref="IGraphicsApi.Present"/>, which puts in the barriers the uses need.
/// Each method's parameters say which of a <see cref="GpuCommand"/>'s fields its kind reads. Render thread.
/// </summary>
public sealed class RenderGraph
{
    private static readonly RenderStage[] _stages = Enum.GetValues<RenderStage>();

    private readonly List<GpuPass> _passes = [];

    private readonly ImmutableArray<GpuUse>.Builder _uses = ImmutableArray.CreateBuilder<GpuUse>();

    private readonly ImmutableArray<GpuCommand>.Builder _commands = ImmutableArray.CreateBuilder<GpuCommand>();

    private GpuPass? _pass;

    /// <summary>
    /// What the GPU has beyond the baseline; nothing while there is no provider.
    /// </summary>
    public GpuFeatures Features => Gpu?.Features ?? default;

    /// <summary>
    /// The window's image for this frame; <see cref="GpuTexture.None"/> headless, while the window is minimised or
    /// while there is no provider.
    /// </summary>
    public GpuTexture WindowTarget => Gpu?.WindowTarget ?? GpuTexture.None;

    /// <summary>
    /// The GPU provider the graph's names resolve through, set by Core once the gems are loaded; none while
    /// no gem provides the GPU.
    /// </summary>
    internal IGraphicsApi? Gpu { get; set; }

    /// <summary>
    /// What <paramref name="texture"/> was made to, the window's image included.
    /// </summary>
    public TextureDescription GetDescription(GpuTexture texture) =>
        (Gpu ?? throw new InvalidOperationException("There is no GPU provider to describe a texture.")).GetDescription(
            texture
        );

    /// <summary>
    /// The texture under the description's name, its heap id known at once to write into constants: made by the GPU
    /// provider on the first ask, kept while every frame asks for it with the same description, so what it holds
    /// carries from frame to frame, and freed once no frame has asked for it in a few. Ask every frame that reads it.
    /// </summary>
    public Result<GpuTexture> Texture(in TextureDescription description) =>
        Gpu?.GetTexture(description) ?? Result.Failure($"there is no GPU provider to make the {description.Name}.");

    /// <summary>
    /// The buffer under the description's name, kept and freed as <see cref="Texture"/> keeps and frees a texture. Asked
    /// for at another size alone, it keeps what it holds, as much as fits, copied before the frame's passes. Ask for it at
    /// its new size before any transfer into it in that frame, as a transfer recorded into the old buffer is lost.
    /// </summary>
    public Result<GpuBuffer> Buffer(in BufferDescription description) =>
        Gpu?.GetBuffer(description) ?? Result.Failure($"there is no GPU provider to make the {description.Name}.");

    /// <summary>
    /// The bottom-level acceleration structure under the description's name, its device address on the handle for the
    /// instances that refer to it, kept and freed as <see cref="Texture"/> keeps and frees a texture; only on a GPU that
    /// traces rays. Build it with <see cref="BuildAcceleration"/> once its triangles are uploaded.
    /// </summary>
    public Result<GpuAcceleration> Acceleration(in BottomLevelDescription description) =>
        Gpu?.GetAcceleration(description)
        ?? Result.Failure($"there is no GPU provider to make the {description.Name}.");

    /// <summary>
    /// The top-level acceleration structure under the description's name, kept and freed as <see cref="Texture"/>
    /// keeps and frees a texture; only on a GPU that traces rays.
    /// </summary>
    public Result<GpuAcceleration> Acceleration(in TopLevelDescription description) =>
        Gpu?.GetAcceleration(description)
        ?? Result.Failure($"there is no GPU provider to make the {description.Name}.");

    /// <summary>
    /// The sampler <paramref name="description"/> makes, one for every ask with the same description, kept and freed
    /// as <see cref="Texture"/> keeps and frees a texture.
    /// </summary>
    public Result<GpuSampler> Sampler(in SamplerDescription description) =>
        Gpu?.GetSampler(description) ?? Result.Failure("there is no GPU provider to make a sampler.");

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
    public ComputePass Compute(string name, in ShaderEntry shader, in GpuConstants constants, RenderStage stage)
    {
        Begin(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Compute,
                Stage = stage,
                Shader = shader,
            }
        );
        PushConstants(constants);

        return new ComputePass(this);
    }

    /// <summary>
    /// Begins a draw pass in <paramref name="stage"/> with <paramref name="shaders"/> into <paramref name="targets"/>,
    /// with <paramref name="constants"/> pushed to it: say what it reads on what this returns, then draw, which ends it.
    /// The GPU provider leaves the pass out of a frame while the shaders are not built.
    /// </summary>
    public DrawPass Draw(
        string name,
        in DrawShaders shaders,
        in GpuRenderTargets targets,
        in GpuConstants constants,
        RenderStage stage
    )
    {
        BeginDraw(
            new()
            {
                Name = name,
                Kind = GpuPassKind.Draw,
                Stage = stage,
                Targets = targets,
                DrawShaders = shaders,
            }
        );
        PushConstants(constants);

        return new DrawPass(this);
    }

    /// <summary>
    /// The frame's passes, stage by stage and in the order they were recorded within one, and the graph empty for the
    /// next frame. The transfers still open end their pass; a compute or draw pass never ended is left out.
    /// </summary>
    public GpuCommands Finish()
    {
        EndOpenPass();
        ImmutableArray<GpuPass>.Builder ordered = ImmutableArray.CreateBuilder<GpuPass>(_passes.Count);
        foreach (RenderStage stage in _stages)
        {
            foreach (GpuPass pass in _passes)
            {
                if (pass.Stage == stage)
                    ordered.Add(pass);
            }
        }

        _passes.Clear();

        return new(ordered.MoveToImmutable());
    }

    /// <summary>
    /// Copies <paramref name="rows"/> into <paramref name="to"/> from <paramref name="offset"/> bytes on, staged through
    /// the frame's upload ring, in a transfer pass.
    /// </summary>
    public void Upload<T>(GpuBuffer to, ReadOnlySpan<T> rows, ulong offset = 0)
        where T : unmanaged => rows.CopyTo(Upload<T>(to, rows.Length, offset));

    /// <summary>
    /// <paramref name="rows"/> rows of the frame's upload ring to write into, copied into <paramref name="to"/> from
    /// <paramref name="offset"/> bytes on in a transfer pass: write them before the frame is presented. No rows record
    /// nothing.
    /// </summary>
    public Span<T> Upload<T>(GpuBuffer to, int rows, ulong offset = 0)
        where T : unmanaged
    {
        if (rows == 0)
            return [];

        UseInTransfer(GetUse(to, GpuAccess.CopyDestination), RenderStage.Prepare);
        int bytes = rows * Unsafe.SizeOf<T>();
        GpuUpload upload = BeginUpload(bytes);
        CopyBuffer(upload.Buffer, upload.Offset, to, offset, (ulong)bytes);

        return MemoryMarshal.Cast<byte, T>(upload.Bytes[..bytes]);
    }

    /// <summary>
    /// Copies <paramref name="pixels"/>, a whole level of the first layer tightly packed, into <paramref name="to"/>'s
    /// <paramref name="level"/>, staged as <see cref="Upload{T}(GpuBuffer, ReadOnlySpan{T}, ulong)"/> stages, in a
    /// transfer pass. The same level filled twice in a frame gets a pass each.
    /// </summary>
    public void Upload(GpuTexture to, ReadOnlySpan<byte> pixels, int level = 0)
    {
        UseInTransfer(GetUse(to, GpuAccess.CopyDestination, level), RenderStage.Prepare);
        GpuUpload upload = BeginUpload(pixels.Length);
        pixels.CopyTo(upload.Bytes);
        CopyBufferToTexture(upload.Buffer, upload.Offset, to, level);
    }

    /// <summary>
    /// Every byte of <paramref name="buffer"/> set to 0.
    /// </summary>
    public void Clear(GpuBuffer buffer)
    {
        // A clear writes the whole buffer, so it follows any other use of it in a pass of its own.
        if (_pass is { Kind: GpuPassKind.Transfer } && IsHeld(buffer))
            EndPass();

        UseInTransfer(GetUse(buffer, GpuAccess.CopyDestination), RenderStage.Prepare);
        Add(new() { Kind = GpuCommandKind.FillBuffer, Id = buffer.Id });
    }

    /// <summary>
    /// Level 0 of <paramref name="from"/> stretched over level 0 of <paramref name="to"/>, filtered linearly; the formats
    /// may differ, in <paramref name="stage"/>.
    /// </summary>
    public void Blit(GpuTexture from, GpuTexture to, RenderStage stage)
    {
        UseInTransfer(GetUse(from, GpuAccess.CopySource), stage);
        UseInTransfer(GetUse(to, GpuAccess.CopyDestination), stage);
        Add(
            new()
            {
                Kind = GpuCommandKind.BlitTexture,
                Id = from.Id,
                Id2 = to.Id,
            }
        );
    }

    /// <summary>
    /// Level 0 of the first layer of <paramref name="from"/> copied, tightly packed, into <paramref name="into"/>, a
    /// <see cref="BufferUsages.Readback"/> buffer the host may read once the frame has finished on the GPU, in
    /// <paramref name="stage"/>.
    /// </summary>
    public void ReadBack(GpuTexture from, GpuBuffer into, RenderStage stage)
    {
        UseInTransfer(GetUse(from, GpuAccess.CopySource), stage);
        UseInTransfer(GetUse(into, GpuAccess.CopyDestination), stage);
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyTextureToBuffer,
                Id = from.Id,
                Id2 = into.Id,
            }
        );
        UseInTransfer(GetUse(into, GpuAccess.HostRead), stage);
    }

    /// <summary>
    /// Builds an acceleration structure: a bottom-level one over its triangles, a top-level one over the first
    /// <paramref name="instanceCount"/> instances of its instance buffer. The build waits for every command before
    /// it and every command after waits for the build.
    /// </summary>
    public void BuildAcceleration(GpuAcceleration acceleration, uint instanceCount = 0)
    {
        BeginTransfer(RenderStage.Prepare);
        Add(
            new()
            {
                Kind = GpuCommandKind.BuildAcceleration,
                Id = acceleration.Id,
                X = instanceCount,
            }
        );
    }

    internal void EndPass()
    {
        GpuPass pass = _pass ?? throw new InvalidOperationException("No pass has begun.");
        _passes.Add(pass with { Uses = _uses.DrainToImmutable(), Commands = _commands.DrainToImmutable() });
        _pass = null;
    }

    internal void PushConstants(in GpuConstants constants) =>
        Add(new() { Kind = GpuCommandKind.PushConstants, Constants = constants });

    internal void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1) =>
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
    internal void DispatchIndirect(GpuBuffer arguments, ulong offset) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DispatchIndirect,
                Id = arguments.Id,
                Offset = offset,
            }
        );

    /// <summary>
    /// Each of <paramref name="draws"/> clipped to its scissor with its constants pushed, its 32-bit indices read from
    /// <paramref name="indices"/>.
    /// </summary>
    internal void DrawIndexed(GpuBuffer indices, scoped ReadOnlySpan<IndexedDraw> draws)
    {
        Use(indices, GpuAccess.Index);
        BindIndexBuffer(indices);
        foreach (IndexedDraw draw in draws)
        {
            // A draw with no pixels to draw into is left out, as a negative size would wrap round to a huge one.
            if (draw.Scissor.Width <= 0 || draw.Scissor.Height <= 0)
                continue;

            Rectangle scissor = draw.Scissor;
            SetScissor((uint)scissor.X, (uint)scissor.Y, (uint)scissor.Width, (uint)scissor.Height);
            PushConstants(draw.Constants);
            DrawIndexed(draw.IndexCount, draw.FirstIndex);
        }
    }

    /// <summary>
    /// As many draws as the unsigned count at <paramref name="countOffset"/> in <paramref name="count"/>, up to
    /// <paramref name="maxDraws"/>, their arguments <paramref name="stride"/> bytes apart from
    /// <paramref name="argumentsOffset"/> in <paramref name="arguments"/>, each four unsigned integers: vertices,
    /// instances, first vertex, first instance.
    /// </summary>
    internal void DrawIndirectCount(
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

    /// <summary>
    /// Says the open pass touches <paramref name="buffer"/> as <paramref name="access"/>.
    /// </summary>
    internal void Use(GpuBuffer buffer, GpuAccess access) => AddUse(GetUse(buffer, access));

    /// <summary>
    /// Says the open pass touches <paramref name="texture"/> as <paramref name="access"/>, every level or
    /// <paramref name="level"/> alone.
    /// </summary>
    internal void Use(GpuTexture texture, GpuAccess access, int level = -1) => AddUse(GetUse(texture, access, level));

    /// <summary>
    /// Says the pass touches each of <paramref name="resources"/> as <paramref name="access"/>, every level of a texture.
    /// </summary>
    internal void Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        foreach (GpuResource resource in resources)
        {
            if (resource.IsTexture)
                Use(resource.Texture, access);
            else
                Use(resource.Buffer, access);
        }
    }

    private static GpuUse GetUse(GpuBuffer buffer, GpuAccess access) =>
        new()
        {
            Id = buffer.Id,
            IsTexture = false,
            Access = access,
        };

    private static GpuUse GetUse(GpuTexture texture, GpuAccess access, int level = -1) =>
        new()
        {
            Id = texture.Id,
            IsTexture = true,
            Access = access,
            Level = level,
        };

    private void SetScissor(uint x, uint y, uint width, uint height) =>
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
    /// 32-bit indices.
    /// </summary>
    private void BindIndexBuffer(GpuBuffer indices) =>
        Add(new() { Kind = GpuCommandKind.BindIndexBuffer, Id = indices.Id });

    /// <summary>
    /// One instance of <paramref name="indices"/> indices from <paramref name="firstIndex"/> on.
    /// </summary>
    private void DrawIndexed(uint indices, uint firstIndex) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.DrawIndexed,
                X = indices,
                Y = 1,
                Z = firstIndex,
            }
        );

    private void CopyBuffer(
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
    /// A whole level of the first layer, tightly packed from <paramref name="sourceOffset"/> on.
    /// </summary>
    private void CopyBufferToTexture(GpuBuffer source, ulong sourceOffset, GpuTexture destination, int level) =>
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBufferToTexture,
                Id = source.Id,
                Offset = sourceOffset,
                Id2 = destination.Id,
                X = (uint)level,
            }
        );

    private GpuUpload BeginUpload(int bytes) =>
        (Gpu ?? throw new InvalidOperationException("There is no GPU provider to upload through.")).BeginUpload(bytes);

    private void Begin(in GpuPass pass)
    {
        EndOpenPass();
        _pass = pass;
    }

    /// <summary>
    /// The open transfer pass ended, or a compute or draw pass a gem began and never ended left out.
    /// </summary>
    private void EndOpenPass()
    {
        if (_pass is { Kind: GpuPassKind.Transfer })
            EndPass();
        else
            DropOpenPass();
    }

    /// <summary>
    /// A transfer pass of <paramref name="stage"/> open for the next transfer: the one open, or a new one.
    /// </summary>
    private void BeginTransfer(RenderStage stage)
    {
        if (_pass is not { Kind: GpuPassKind.Transfer } open || open.Stage != stage)
            Begin(
                new()
                {
                    Name = "Transfers",
                    Kind = GpuPassKind.Transfer,
                    Stage = stage,
                }
            );
    }

    /// <summary>
    /// Adds <paramref name="use"/> to a transfer pass of <paramref name="stage"/>: the open one, or a new one when the open one uses the same
    /// resource another way, has written the same texture level or has cleared the buffer, as a pass's barriers all
    /// come before its commands. Uploads into one buffer share a pass, as each writes its own range.
    /// </summary>
    private void UseInTransfer(in GpuUse use, RenderStage stage)
    {
        if (_pass is { Kind: GpuPassKind.Transfer } && IsUsedOtherwise(use))
            EndPass();

        BeginTransfer(stage);
        AddUse(use);
    }

    private bool IsUsedOtherwise(in GpuUse use)
    {
        foreach (GpuUse held in _uses)
        {
            bool isSame =
                held.Id == use.Id
                && held.IsTexture == use.IsTexture
                && (held.Level == use.Level || held.Level < 0 || use.Level < 0);
            bool isWrittenAgain = use.Access == GpuAccess.CopyDestination && (use.IsTexture || IsCleared(use.Id));
            if (isSame && (held.Access != use.Access || isWrittenAgain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Begins a draw pass, each of its targets used as a colour or depth target.
    /// </summary>
    private bool IsHeld(GpuBuffer buffer)
    {
        foreach (GpuUse held in _uses)
        {
            if (!held.IsTexture && held.Id == buffer.Id)
                return true;
        }

        return false;
    }

    private bool IsCleared(uint buffer)
    {
        foreach (GpuCommand command in _commands)
        {
            if (command.Kind == GpuCommandKind.FillBuffer && command.Id == buffer)
                return true;
        }

        return false;
    }

    private void BeginDraw(in GpuPass pass)
    {
        Begin(pass);
        foreach (GpuTarget color in pass.Targets.Colors)
        {
            Use(color.Texture, GpuAccess.ColorTarget, color.Level);
        }

        if (pass.Targets.Depth is { } depth)
            Use(depth.Texture, GpuAccess.DepthTarget, depth.Level);
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

    /// <summary>
    /// The use added to the open pass, unless it holds the same one, which would only put in a barrier between the two.
    /// </summary>
    private void AddUse(in GpuUse use)
    {
        ThrowIfNotInPass();
        if (!_uses.Contains(use))
            _uses.Add(use);
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
