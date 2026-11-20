using System.Collections.Immutable;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;

namespace Magic.Graphics;

/// <summary>
/// A frame's passes as data: compute and draw passes, and transfers that the graph gathers into passes of their own.
/// </summary>
/// <remarks>
/// <para>
/// Core holds one graph. Each gem's step records into it, and Core gives the result of <see cref="Finish"/> to
/// <see cref="IGraphicsApi.Present"/>. Transfers are uploads, clears, blits, readbacks and builds.
/// </para>
/// <para>
/// Each method's parameters show which fields of a <see cref="GpuCommand"/> its kind reads. A gem that cannot record a
/// part of its frame tells <see cref="RenderFrame.Fail"/> and goes on. Use it only on the render thread.
/// </para>
/// </remarks>
public sealed class RenderGraph
{
    private readonly List<GpuPass> _passes = [];

    private readonly ImmutableArray<GpuUse>.Builder _uses = ImmutableArray.CreateBuilder<GpuUse>();

    private readonly ImmutableArray<GpuCommand>.Builder _commands = ImmutableArray.CreateBuilder<GpuCommand>();

    /// <summary>
    /// The passes of the views before this one, in the order they run.
    /// </summary>
    private readonly ImmutableArray<GpuPass>.Builder _ordered = ImmutableArray.CreateBuilder<GpuPass>();

    private GpuPass? _pass;

    /// <summary>
    /// The GPU provider whose upload ring the graph stages into. Core sets it after the gems load; null while no gem
    /// provides the GPU.
    /// </summary>
    internal IGraphicsApi? Gpu { get; set; }

    /// <summary>
    /// The gems' steps, which keep what failed in the frame. Core sets it after the gems load.
    /// </summary>
    internal Rendering? Rendering { get; set; }

    /// <summary>
    /// Begins a compute pass that runs <paramref name="shader"/> with <paramref name="constants"/>. A dispatch ends it.
    /// </summary>
    /// <remarks>
    /// Declare what it reads and writes on the returned pass, then dispatch. The frame leaves the pass out while the
    /// shader is not built.
    /// </remarks>
    public ComputePass Compute(string name, in ShaderEntry shader, in GpuConstants constants, RenderStage stage)
    {
        Begin(
            new()
            {
                Name = name,
                Stage = stage,
                Shader = shader,
            }
        );
        PushConstants(constants);

        return new ComputePass(this);
    }

    /// <summary>
    /// Begins a compute pass that runs <paramref name="shader"/> with its bindings, which it reads and writes as their
    /// kinds say. A dispatch ends it.
    /// </summary>
    public ComputePass Compute(string name, in BoundShader shader, RenderStage stage)
    {
        Begin(
            new()
            {
                Name = name,
                Stage = stage,
                BoundShader = shader,
            }
        );
        foreach (GpuBinding binding in shader.Bindings)
        {
            if (binding.Kind == GpuBindingKind.Texture)
                Use(new GpuTexture(binding.Id), GpuAccess.ShaderRead);
            else if (binding.Kind == GpuBindingKind.StorageTexture)
                Use(new GpuTexture(binding.Id), GpuAccess.ShaderWrite);
        }

        return new ComputePass(this);
    }

    /// <summary>
    /// Begins a draw pass with <paramref name="shaders"/> into <paramref name="targets"/>. A draw ends it.
    /// </summary>
    /// <remarks>
    /// Declare what it reads on the returned pass, then draw. The frame leaves the pass out while the shaders are not
    /// built.
    /// </remarks>
    public DrawPass Draw(
        string name,
        in DrawShaders shaders,
        in GpuRenderTargets targets,
        in GpuConstants constants,
        RenderStage stage
    )
    {
        Begin(
            new()
            {
                Name = name,
                Stage = stage,
                Targets = targets,
                DrawShaders = shaders,
            }
        );
        foreach (GpuTarget color in targets.Colors)
        {
            Use(color.Texture, GpuAccess.ColorTarget, color.Level);
        }

        if (targets.Depth is { } depth)
            Use(depth.Texture, GpuAccess.DepthTarget, depth.Level);

        PushConstants(constants);

        return new DrawPass(this);
    }

    /// <summary>
    /// Copies <paramref name="rows"/> into <paramref name="to"/> at <paramref name="offset"/> bytes, through the
    /// frame's upload ring.
    /// </summary>
    /// <returns>
    /// False when the ring has no room. Then nothing is copied, and the caller keeps the rows as not sent.
    /// </returns>
    public bool Upload<T>(GpuBuffer to, ReadOnlySpan<T> rows, ulong offset = 0)
        where T : unmanaged
    {
        Span<T> into = Upload<T>(to, rows.Length, offset);
        if (into.Length < rows.Length)
            return false;

        rows.CopyTo(into);

        return true;
    }

    /// <summary>
    /// Gets <paramref name="rows"/> rows of the upload ring to write, copied into <paramref name="to"/> at
    /// <paramref name="offset"/> bytes.
    /// </summary>
    /// <remarks>
    /// Write them before the frame is presented. Zero rows record nothing. When the ring has no room, it gives an empty
    /// span and tells <see cref="RenderFrame.Fail"/>, and the caller keeps the rows as not sent.
    /// </remarks>
    public Span<T> Upload<T>(GpuBuffer to, int rows, ulong offset = 0)
        where T : unmanaged
    {
        if (rows == 0)
            return [];

        int bytes = rows * Unsafe.SizeOf<T>();
        GpuUpload upload = BeginUpload(bytes);
        if (upload.Buffer.IsNone)
            return [];

        UseInTransfer(GetUse(to, GpuAccess.CopyDestination), RenderStage.Prepare);
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBuffer,
                Id = upload.Buffer.Id,
                Offset = upload.Offset,
                Id2 = to.Id,
                Offset2 = offset,
                Size = (ulong)bytes,
            }
        );

        return MemoryMarshal.Cast<byte, T>(upload.Bytes[..bytes]);
    }

    /// <summary>
    /// Copies <paramref name="pixels"/>, a whole level of the first layer tightly packed, into <paramref name="to"/>'s
    /// <paramref name="level"/>.
    /// </summary>
    /// <remarks>
    /// It stages as <see cref="Upload{T}(GpuBuffer, ReadOnlySpan{T}, ulong)"/> does. The same level filled two times in
    /// a frame gets a pass each.
    /// </remarks>
    /// <returns>
    /// False when the ring has no room. Then nothing is copied, and the caller keeps the pixels as not sent.
    /// </returns>
    public bool Upload(GpuTexture to, ReadOnlySpan<byte> pixels, int level = 0)
    {
        GpuUpload upload = BeginUpload(pixels.Length);
        if (upload.Buffer.IsNone)
            return false;

        UseInTransfer(GetUse(to, GpuAccess.CopyDestination, level), RenderStage.Prepare);
        pixels.CopyTo(upload.Bytes);
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyBufferToTexture,
                Id = upload.Buffer.Id,
                Offset = upload.Offset,
                Id2 = to.Id,
                X = (uint)level,
            }
        );
        return true;
    }

    /// <summary>
    /// Stretches level 0 of <paramref name="from"/> over level 0 of <paramref name="to"/> with
    /// <paramref name="filter"/>. The formats can differ.
    /// </summary>
    public void Blit(GpuTexture from, GpuTexture to, RenderStage stage, TextureFilter filter) =>
        Blit(from, to, default, stage, filter);

    /// <summary>
    /// Level 0 of <paramref name="from"/> stretched over <paramref name="part"/> of level 0 of <paramref name="to"/>,
    /// in pixels from its top left, or over all of it when the part is empty, read with <paramref name="filter"/>; the
    /// formats may differ, in <paramref name="stage"/>.
    /// </summary>
    public void Blit(GpuTexture from, GpuTexture to, Rectangle part, RenderStage stage, TextureFilter filter)
    {
        UseInTransfer(GetUse(from, GpuAccess.CopySource), stage);
        UseInTransfer(GetUse(to, GpuAccess.CopyDestination), stage);
        Add(
            new()
            {
                Kind = GpuCommandKind.BlitTexture,
                Id = from.Id,
                Id2 = to.Id,
                X = (uint)filter,
                Offset2 = ((ulong)(uint)part.X << 32) | (uint)part.Y,
                Size = ((ulong)(uint)part.Width << 32) | (uint)part.Height,
            }
        );
    }

    /// <summary>
    /// Copies level 0 of <paramref name="from"/> into level 0 of <paramref name="to"/>, texel for texel, with the bytes
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// The two textures have the same size, and formats with texels of the same size. No colour is converted, so a
    /// picture encoded for the screen goes into an sRGB texture as it is.
    /// </remarks>
    public void Copy(GpuTexture from, GpuTexture to, RenderStage stage)
    {
        UseInTransfer(GetUse(from, GpuAccess.CopySource), stage);
        UseInTransfer(GetUse(to, GpuAccess.CopyDestination), stage);
        Add(
            new()
            {
                Kind = GpuCommandKind.CopyTexture,
                Id = from.Id,
                Id2 = to.Id,
            }
        );
    }

    /// <summary>
    /// Copies level 0 of the first layer of <paramref name="from"/>, tightly packed, into the
    /// <see cref="BufferUsages.Readback"/> buffer <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// The host can read the buffer after the frame finishes on the GPU.
    /// </remarks>
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
    /// Sets each byte of <paramref name="buffer"/> to 0, in a transfer pass of its own.
    /// </summary>
    public void Clear(GpuBuffer buffer)
    {
        Begin(new() { Name = "Transfers", Stage = RenderStage.Prepare });
        AddUse(GetUse(buffer, GpuAccess.CopyDestination));
        Add(new() { Kind = GpuCommandKind.FillBuffer, Id = buffer.Id });
        EndPass();
    }

    /// <summary>
    /// Builds an acceleration structure: a bottom-level one over its triangles, a top-level one over the first
    /// <paramref name="instanceCount"/> instances of its instance buffer.
    /// </summary>
    /// <remarks>
    /// The build waits for each command before it, and each command after it waits for the build.
    /// </remarks>
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

    /// <summary>
    /// Takes the frame's passes, view by view and stage by stage in the order recorded, and empties the graph for the
    /// next frame.
    /// </summary>
    /// <remarks>
    /// An open transfer pass ends. A compute or draw pass that was never ended is left out.
    /// </remarks>
    internal GpuCommands Finish()
    {
        OrderPasses();

        // A copy, then Clear, so the builder keeps its capacity for the next frame; a drain would start it empty.
        ImmutableArray<GpuPass> passes = _ordered.ToImmutable();
        _ordered.Clear();

        return new(passes);
    }

    /// <summary>
    /// Puts the passes recorded since the last call in the order they run, after those of every call before: stage by
    /// stage, and in the order they were recorded within one. Core calls it between two views, so a view's passes all
    /// run before the next view's.
    /// </summary>
    internal void OrderPasses()
    {
        EndOpenPass();

        // Each turn takes the lowest stage not yet taken, then its passes in the order they were recorded.
        int end = _ordered.Count + _passes.Count;
        int taken = -1;
        while (_ordered.Count < end)
        {
            int stage = byte.MaxValue;
            foreach (GpuPass pass in _passes)
            {
                if (pass.Stage.Order > taken && pass.Stage.Order < stage)
                    stage = pass.Stage.Order;
            }

            foreach (GpuPass pass in _passes)
            {
                if (pass.Stage.Order == stage)
                    _ordered.Add(pass);
            }

            taken = stage;
        }

        _passes.Clear();
    }

    internal void EndPass()
    {
        GpuPass pass = _pass ?? throw new InvalidOperationException("No pass has begun.");
        _passes.Add(pass with { Uses = _uses.ToImmutable(), Commands = _commands.ToImmutable() });
        _uses.Clear();
        _commands.Clear();
        _pass = null;
    }

    internal void Add(in GpuCommand command)
    {
        ThrowIfNotInPass();
        _commands.Add(command);
    }

    internal void PushConstants(in GpuConstants constants) =>
        Add(new() { Kind = GpuCommandKind.PushConstants, Constants = constants });

    /// <summary>
    /// Declares that the open pass touches <paramref name="resource"/> as <paramref name="access"/>: all levels of a
    /// texture, or <paramref name="level"/> only.
    /// </summary>
    internal void Use(GpuResource resource, GpuAccess access, int level = -1) =>
        AddUse(GetUse(resource, access, level));

    /// <summary>
    /// Declares that the open pass touches each of <paramref name="resources"/> as <paramref name="access"/>, all
    /// levels of a texture.
    /// </summary>
    internal void Use(scoped ReadOnlySpan<GpuResource> resources, GpuAccess access)
    {
        foreach (GpuResource resource in resources)
        {
            Use(resource, access);
        }
    }

    /// <summary>
    /// Silently leaves out the open compute or draw pass of a gem that threw, as its failure is already told.
    /// </summary>
    internal void LeaveOutOpenPass()
    {
        if (_pass is not { Kind: not GpuPassKind.Transfer })
            return;

        _uses.Clear();
        _commands.Clear();
        _pass = null;
    }

    private static GpuUse GetUse(GpuResource resource, GpuAccess access, int level = -1) =>
        new()
        {
            Id = resource.Id,
            IsTexture = resource.IsTexture,
            Access = access,
            Level = level,
        };

    /// <summary>
    /// Gets room for <paramref name="bytes"/> in the frame's upload ring. Without room or a provider, it gives none and
    /// tells the upload as failed.
    /// </summary>
    private GpuUpload BeginUpload(int bytes)
    {
        GpuUpload upload = Gpu is { } gpu ? gpu.BeginUpload(bytes) : default;
        if (upload.Buffer.IsNone)
            Rendering?.Fail(
                RenderFailure.Resource,
                "Uploads are dropped",
                "the GPU provider has no room to stage them, or there is no provider."
            );

        return upload;
    }

    private void Begin(in GpuPass pass)
    {
        EndOpenPass();
        _pass = pass;
    }

    /// <summary>
    /// Ends the open transfer pass, or leaves out a compute or draw pass that a gem began and never ended.
    /// </summary>
    private void EndOpenPass()
    {
        if (_pass is not { } open)
            return;

        if (open.Kind == GpuPassKind.Transfer)
        {
            EndPass();
            return;
        }

        // The gem forgot to end the pass: the other gems' passes still draw.
        Log.Error($"The pass {open.Name} was never ended, so it is left out of the frame.");
        LeaveOutOpenPass();
    }

    /// <summary>
    /// Makes sure that a transfer pass of <paramref name="stage"/> is open: the one open, or a new one.
    /// </summary>
    private void BeginTransfer(RenderStage stage)
    {
        if (_pass is not { Kind: GpuPassKind.Transfer } open || open.Stage != stage)
            Begin(new() { Name = "Transfers", Stage = stage });
    }

    /// <summary>
    /// Adds <paramref name="use"/> to a transfer pass of <paramref name="stage"/>: the open one, or a new one.
    /// </summary>
    /// <remarks>
    /// A pass's barriers all come before its commands. So a new pass starts when the open one uses the same resource in
    /// another way or wrote the same texture level. Uploads into one buffer share a pass, as each writes its own range.
    /// </remarks>
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
            bool isWrittenAgain = use.Access == GpuAccess.CopyDestination && use.IsTexture;
            if (isSame && (held.Access != use.Access || isWrittenAgain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adds the use to the open pass, unless its resource is <c>None</c> or the pass already holds the same use.
    /// </summary>
    private void AddUse(in GpuUse use)
    {
        ThrowIfNotInPass();
        if (use.Id != 0 && !_uses.Contains(use))
            _uses.Add(use);
    }

    private void ThrowIfNotInPass()
    {
        if (_pass is null)
            throw new InvalidOperationException("No pass has begun.");
    }
}
