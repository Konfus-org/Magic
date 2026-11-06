using Magic.Gpu;
using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// A frame's passes recorded in order, each saying what it reads and writes, with the barriers put in for it: the graph
/// keeps what access every resource was left in, by the pass before or the frame before, and a pass that uses it
/// otherwise gets the transition at its start. Transient textures come from a pool by their description and go back
/// to it as the frame ends, so a frame makes nothing. What the passes record goes through <see cref="Recorder"/>,
/// and <see cref="Finish"/> is the frame's <see cref="GpuCommands"/>.
/// </summary>
public sealed class RenderGraph(IGraphicsApi gpu)
{
    private readonly Dictionary<uint, GpuAccess> _bufferAccess = [];
    private readonly Dictionary<uint, GpuAccess[]> _textureAccess = [];
    private readonly Dictionary<TextureDescription, Stack<GpuTexture>> _pool = [];
    private readonly List<(TextureDescription Description, GpuTexture Texture)> _lent = [];

    /// <summary>
    /// What the passes record their commands with, between <see cref="BeginComputePass"/> and <see cref="EndPass"/>.
    /// </summary>
    public GpuCommandRecorder Recorder { get; } = new();

    public void BeginComputePass(string name) => Recorder.BeginComputePass(name);

    public void BeginTransferPass(string name) => Recorder.BeginTransferPass(name);

    /// <summary>
    /// Begins a draw pass into <paramref name="targets"/>, each target used as a colour or depth target.
    /// </summary>
    public void BeginDrawPass(string name, in GpuRenderTargets targets)
    {
        Recorder.BeginDrawPass(name, targets);
        foreach (GpuColorTarget color in targets.Colors)
        {
            Use(color.Texture, GpuAccess.ColorTarget, color.Level);
        }

        if (targets.Depth is { } depth)
            Use(depth.Texture, GpuAccess.DepthTarget, depth.Level);
    }

    public void EndPass() => Recorder.EndPass();

    /// <summary>
    /// Says the pass touches <paramref name="buffer"/> as <paramref name="access"/>, with the barrier that takes.
    /// </summary>
    public void Use(GpuBuffer buffer, GpuAccess access)
    {
        GpuAccess before = _bufferAccess.GetValueOrDefault(buffer.Id, GpuAccess.None);
        if (IsSameRead(before, access))
            return;

        Recorder.Transition(buffer, before, access);
        _bufferAccess[buffer.Id] = access;
    }

    /// <summary>
    /// Says the pass touches <paramref name="texture"/> as <paramref name="access"/>, every level or
    /// <paramref name="level"/> alone, with the barriers that takes.
    /// </summary>
    public void Use(GpuTexture texture, GpuAccess access, int level = -1)
    {
        if (!_textureAccess.TryGetValue(texture.Id, out GpuAccess[]? levels))
        {
            levels = new GpuAccess[gpu.GetDescription(texture).Levels];
            _textureAccess[texture.Id] = levels;
        }

        int first = level < 0 ? 0 : level;
        int last = level < 0 ? levels.Length - 1 : level;
        for (int i = first; i <= last; i++)
        {
            if (IsSameRead(levels[i], access))
                continue;

            Recorder.Transition(texture, levels[i], access, i);
            levels[i] = access;
        }
    }

    /// <summary>
    /// Tells the graph a texture's access as something else left it: the window's image, none as the frame begins.
    /// </summary>
    public void Import(GpuTexture texture, GpuAccess access)
    {
        if (!_textureAccess.TryGetValue(texture.Id, out GpuAccess[]? levels))
        {
            levels = new GpuAccess[gpu.GetDescription(texture).Levels];
            _textureAccess[texture.Id] = levels;
        }

        Array.Fill(levels, access);
    }

    /// <summary>
    /// Forgets a resource that was destroyed, so a new one with its id starts untouched.
    /// </summary>
    public void Forget(GpuTexture texture) => _textureAccess.Remove(texture.Id);

    public void Forget(GpuBuffer buffer) => _bufferAccess.Remove(buffer.Id);

    /// <summary>
    /// A texture for this frame, made to <paramref name="description"/> or taken back from an earlier frame's, gone
    /// at <see cref="Finish"/>.
    /// </summary>
    public Result<GpuTexture> CreateTransient(in TextureDescription description)
    {
        if (_pool.TryGetValue(description, out Stack<GpuTexture>? spare) && spare.TryPop(out GpuTexture pooled))
        {
            _lent.Add((description, pooled));

            return Result.Success(pooled);
        }

        Result<GpuTexture> made = gpu.CreateTexture(description);
        if (made.Ok)
            _lent.Add((description, made.Value));

        return made;
    }

    /// <summary>
    /// The frame's commands, the transient textures back in the pool.
    /// </summary>
    public GpuCommands Finish()
    {
        foreach ((TextureDescription description, GpuTexture texture) in _lent)
        {
            if (!_pool.TryGetValue(description, out Stack<GpuTexture>? spare))
            {
                spare = new Stack<GpuTexture>();
                _pool[description] = spare;
            }

            spare.Push(texture);
        }

        _lent.Clear();

        return Recorder.Finish();
    }

    /// <summary>
    /// Frees every pooled texture: what a graph does as its owner ends.
    /// </summary>
    public void Clear()
    {
        foreach (Stack<GpuTexture> spare in _pool.Values)
        {
            while (spare.TryPop(out GpuTexture texture))
            {
                gpu.Destroy(texture);
                _textureAccess.Remove(texture.Id);
            }
        }

        _pool.Clear();
    }

    // A write after a write still needs a barrier, so the second sees the first; a read after the same read does not.
    private static bool IsSameRead(GpuAccess before, GpuAccess access) =>
        before == access
        && access
            is GpuAccess.ShaderRead
                or GpuAccess.DepthRead
                or GpuAccess.Indirect
                or GpuAccess.Index
                or GpuAccess.CopySource;
}
