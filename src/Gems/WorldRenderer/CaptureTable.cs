using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The capture table: a row per model and set of materials drawn as an imposter, with the atlases it was captured
/// into. A capture first asked for gets its row and atlases and waits in line; the renderer draws the model into
/// them from every direction, and from then the row is handed to the instances that show it.
/// </summary>
internal sealed class CaptureTable(IGraphicsApi gpu) : IDisposable
{
    public const int Capacity = 4096;

    private readonly GpuCapture[] _rows = new GpuCapture[Capacity];
    private readonly Dictionary<CaptureKey, uint> _rowOf = [];
    private readonly bool[] _isReady = new bool[Capacity];
    private readonly Queue<CaptureJob> _pending = new();
    private readonly List<GpuTexture> _atlases = [];
    private int _count = 1;
    private bool _isDirty = true;
    private bool _isFullLogged;

    public GpuBuffer Buffer { get; private set; }

    public void Dispose()
    {
        foreach (GpuTexture atlas in _atlases)
        {
            gpu.Destroy(atlas);
        }

        if (!Buffer.IsNone)
            gpu.Destroy(Buffer);
    }

    public Result Create()
    {
        Result<GpuBuffer> buffer = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)Capacity * GpuCapture.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Capture table",
            }
        );
        if (!buffer.Ok)
            return Result.Failure($"the capture table was not made: {buffer.Error}");

        Buffer = buffer.Value;

        return Result.Success();
    }

    /// <summary>
    /// The row of the model's capture with these materials once it is drawn, 0 until then; the first ask makes the
    /// atlases and puts the capture in line.
    /// </summary>
    public uint Get(ulong model, ResidentModel resident, ReadOnlySpan<uint> materials)
    {
        CaptureKey key = new(model, materials);
        if (_rowOf.TryGetValue(key, out uint row))
            return _isReady[row] ? row : 0;

        if (_count >= Capacity)
        {
            if (!_isFullLogged)
                Log.Error($"Some models draw no imposter: the capture table holds {Capacity}.");

            _isFullLogged = true;
            _rowOf[key] = 0;

            return 0;
        }

        Result<GpuTexture> albedo = MakeAtlas("Capture albedo", GpuFormat.R8G8B8A8Unorm);
        Result<GpuTexture> normal = MakeAtlas("Capture normal", GpuFormat.R16G16Unorm);
        Result<GpuTexture> depth = MakeAtlas("Capture depth", GpuFormat.R32Sfloat);
        if (!albedo.Ok || !normal.Ok || !depth.Ok)
        {
            Log.Error(
                $"A model draws no imposter: its atlases were not made: {albedo.Error}{normal.Error}{depth.Error}"
            );
            _rowOf[key] = 0;

            return 0;
        }

        row = (uint)_count++;
        _rows[row] = new GpuCapture
        {
            Albedo = albedo.Value.Id,
            Normal = normal.Value.Id,
            Depth = depth.Value.Id,
        };
        _rowOf[key] = row;
        _isDirty = true;
        _atlases.Add(albedo.Value);
        _atlases.Add(normal.Value);
        _atlases.Add(depth.Value);
        _pending.Enqueue(new CaptureJob(row, resident, materials.ToArray(), albedo.Value, normal.Value, depth.Value));

        return 0;
    }

    /// <summary>
    /// The next capture waiting to be drawn, if any; it counts as drawn once taken.
    /// </summary>
    public bool TryTakeJob(out CaptureJob job)
    {
        if (!_pending.TryDequeue(out job))
            return false;

        _isReady[job.Row] = true;

        return true;
    }

    /// <summary>
    /// Says the pass reads every atlas: what the draw and the resolve declare.
    /// </summary>
    public void UseAll(RenderGraph graph)
    {
        foreach (GpuTexture atlas in _atlases)
        {
            graph.Use(atlas, GpuAccess.ShaderRead);
        }
    }

    /// <summary>
    /// Copies the rows into the table, in the frame's upload pass, when any were added.
    /// </summary>
    public void Upload(RenderGraph graph)
    {
        if (!_isDirty)
            return;

        _isDirty = false;
        int bytes = _count * GpuCapture.Bytes;
        GpuUpload upload = gpu.BeginUpload(bytes);
        MemoryMarshal.AsBytes(_rows.AsSpan(0, _count)).CopyTo(upload.Bytes);
        graph.Use(Buffer, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Buffer, 0, (ulong)bytes);
    }

    private Result<GpuTexture> MakeAtlas(string name, GpuFormat format) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = GpuCapture.AtlasSize,
                Height = GpuCapture.AtlasSize,
                Format = format,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = name,
            }
        );

    /// <summary>
    /// A model and the material rows of its slots: what one capture is of.
    /// </summary>
    private readonly struct CaptureKey(ulong model, ReadOnlySpan<uint> materials) : IEquatable<CaptureKey>
    {
        private readonly ulong _model = model;
        private readonly ulong _materials = Hash(materials);

        public bool Equals(CaptureKey other) => _model == other._model && _materials == other._materials;

        public override bool Equals(object? obj) => obj is CaptureKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_model, _materials);

        private static ulong Hash(ReadOnlySpan<uint> materials)
        {
            ulong hash = 14695981039346656037;
            foreach (uint material in materials)
            {
                hash = unchecked((hash ^ material) * 1099511628211);
            }

            return hash;
        }
    }
}

/// <summary>
/// A capture waiting to be drawn: its row, what to draw and where into.
/// </summary>
internal readonly record struct CaptureJob(
    uint Row,
    ResidentModel Resident,
    uint[] Materials,
    GpuTexture Albedo,
    GpuTexture Normal,
    GpuTexture Depth
);
