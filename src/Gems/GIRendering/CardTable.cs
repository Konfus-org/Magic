using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The card table: six cards per model, set of materials and scale in the world, the sides of the model's box
/// drawn orthographically into the card atlases at a resolution that scale earns. A capture first asked for gets
/// its rows and its places in the atlases and waits in line; the renderer draws the model into them side by side,
/// and from then the row is handed to the instances that show it.
/// </summary>
internal sealed class CardTable(IGraphicsApi gpu, RenderGraph graph) : IDisposable
{
    public const int Capacity = 4096;

    /// <summary>
    /// Metres a card's texel covers at most, and the fewest texels a card has across.
    /// </summary>
    private const float TexelWorld = 0.1f;
    private const int MinTexels = 8;

    private readonly GpuCard[] _rows = new GpuCard[Capacity * GpuCard.PerCapture];
    private readonly Dictionary<CaptureKey, uint> _rowOf = [];
    private readonly bool[] _isReady = new bool[Capacity];
    private readonly Queue<CaptureJob> _pending = new();
    private readonly Dictionary<int, (int Y, int X)> _shelves = [];
    private int _nextShelf;
    private int _count = 1;
    private bool _isDirty = true;
    private bool _isFullLogged;

    public GpuBuffer Buffer { get; private set; }

    public GpuTexture Albedo { get; private set; }

    public GpuTexture Normal { get; private set; }

    public GpuTexture Depth { get; private set; }

    /// <summary>
    /// The axes of a card's view, as the shaders set them up: towards the viewer along the side's direction, the
    /// view looking back along it.
    /// </summary>
    public static void GetBasis(uint axis, out Vector3 direction, out Vector3 right, out Vector3 up)
    {
        float sign = (axis & 1) != 0 ? -1 : 1;
        direction = (axis >> 1) switch
        {
            0 => new Vector3(sign, 0, 0),
            1 => new Vector3(0, sign, 0),
            _ => new Vector3(0, 0, sign),
        };
        Vector3 worldUp = Math.Abs(direction.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 forward = -direction;
        right = Vector3.Normalize(Vector3.Cross(worldUp, forward));
        up = Vector3.Cross(forward, right);
    }

    /// <summary>
    /// The box's half extents in a card's frame, across, up and along its direction, each at least a centimetre.
    /// </summary>
    public static Vector3 GetExtents(in GpuCard card, Vector3 direction, Vector3 right, Vector3 up) =>
        Vector3.Max(
            new Vector3(
                Math.Abs(Vector3.Dot(card.Extents, right)),
                Math.Abs(Vector3.Dot(card.Extents, up)),
                Math.Abs(Vector3.Dot(card.Extents, direction))
            ),
            new Vector3(0.01f)
        );

    public void Dispose()
    {
        foreach (GpuTexture atlas in (ReadOnlySpan<GpuTexture>)[Albedo, Normal, Depth])
        {
            if (!atlas.IsNone)
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
                Bytes = (long)_rows.Length * GpuCard.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Card table",
            }
        );
        if (!buffer.Ok)
            return Result.Failure($"the card table was not made: {buffer.Error}");

        Buffer = buffer.Value;
        Result<GpuTexture> albedo = CreateAtlas("Card albedo", GpuFormat.R8G8B8A8Unorm);
        Result<GpuTexture> normal = CreateAtlas("Card normals", GpuFormat.R16G16Unorm);
        Result<GpuTexture> depth = CreateAtlas("Card depths", GpuFormat.R32Sfloat);
        if (!albedo.Ok || !normal.Ok || !depth.Ok)
            return Result.Failure($"the card atlases were not made: {albedo.Error}{normal.Error}{depth.Error}");

        (Albedo, Normal, Depth) = (albedo.Value, normal.Value, depth.Value);

        return Result.Success();
    }

    /// <summary>
    /// The capture row of the model with these materials at about this scale once its cards are drawn, 0 until
    /// then; the first ask takes the cards' places in the atlases and puts the capture in line. The scale is
    /// bucketed by powers of two, so an instance's cards have the texels its size in the world earns.
    /// </summary>
    public uint Get(ulong model, ResidentModel resident, ReadOnlySpan<uint> materials, float scale)
    {
        int bucket = Math.Clamp((int)MathF.Round(MathF.Log2(Math.Max(scale, 1e-3f))), -4, 8);
        CaptureKey key = new(model, materials, bucket);
        if (_rowOf.TryGetValue(key, out uint row))
            return _isReady[row] ? row : 0;

        if (_count >= Capacity)
        {
            if (!_isFullLogged)
                Log.Error($"Some models have no cards: the card table holds {Capacity} captures.");

            _isFullLogged = true;
            _rowOf[key] = 0;

            return 0;
        }

        float worldScale = MathF.Pow(2, bucket);
        Vector3 center = (resident.BoundsMin + resident.BoundsMax) * 0.5f;
        Vector3 extents = (resident.BoundsMax - resident.BoundsMin) * 0.5f;
        Span<GpuCard> cards = stackalloc GpuCard[GpuCard.PerCapture];
        for (uint axis = 0; axis < GpuCard.PerCapture; axis++)
        {
            GpuCard card = new()
            {
                Center = center,
                Axis = axis,
                Extents = extents,
            };
            GetBasis(axis, out Vector3 direction, out Vector3 right, out Vector3 up);
            Vector3 inCard = GetExtents(card, direction, right, up);
            card.Width = (uint)
                Math.Clamp(MathF.Ceiling(2 * inCard.X * worldScale / TexelWorld), MinTexels, GpuCard.MaxTexels);
            card.Height = (uint)
                Math.Clamp(MathF.Ceiling(2 * inCard.Y * worldScale / TexelWorld), MinTexels, GpuCard.MaxTexels);
            if (!TryAllocate((int)card.Width, (int)card.Height, out int x, out int y))
            {
                if (!_isFullLogged)
                    Log.Error("Some models have no cards: the card atlases are full.");

                _isFullLogged = true;
                _rowOf[key] = 0;

                return 0;
            }

            card.OriginX = (uint)x;
            card.OriginY = (uint)y;
            cards[(int)axis] = card;
        }

        row = (uint)_count++;
        cards.CopyTo(_rows.AsSpan((int)row * GpuCard.PerCapture, GpuCard.PerCapture));
        _rowOf[key] = row;
        _isDirty = true;
        _pending.Enqueue(new CaptureJob(row, resident, materials.ToArray()));

        return 0;
    }

    /// <summary>
    /// A capture's card, by its row and side.
    /// </summary>
    public GpuCard GetCard(uint row, int side) => _rows[((int)row * GpuCard.PerCapture) + side];

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
    /// Says the pass reads the table and the atlases: what every pass that reads a card declares.
    /// </summary>
    public void UseForReading()
    {
        graph.Use(Buffer, GpuAccess.ShaderRead);
        graph.Use(Albedo, GpuAccess.ShaderRead);
        graph.Use(Normal, GpuAccess.ShaderRead);
        graph.Use(Depth, GpuAccess.ShaderRead);
    }

    /// <summary>
    /// Copies the rows into the table, in the frame's upload pass, when any were added.
    /// </summary>
    public void Upload()
    {
        if (!_isDirty)
            return;

        _isDirty = false;
        int bytes = _count * GpuCard.PerCapture * GpuCard.Bytes;
        GpuUpload upload = gpu.BeginUpload(bytes);
        MemoryMarshal.AsBytes(_rows.AsSpan(0, _count * GpuCard.PerCapture)).CopyTo(upload.Bytes);
        graph.Use(Buffer, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Buffer, 0, (ulong)bytes);
    }

    /// <summary>
    /// A place in the atlases for a card: on a shelf of the next power of two above its height, a new shelf when
    /// the open one is full; false when the atlases hold no more.
    /// </summary>
    private bool TryAllocate(int width, int height, out int x, out int y)
    {
        int shelfHeight = (int)BitOperations.RoundUpToPowerOf2((uint)height);
        if (!_shelves.TryGetValue(shelfHeight, out (int Y, int X) shelf) || shelf.X + width > GpuCard.AtlasTexels)
        {
            if (_nextShelf + shelfHeight > GpuCard.AtlasTexels)
            {
                x = 0;
                y = 0;

                return false;
            }

            shelf = (_nextShelf, 0);
            _nextShelf += shelfHeight;
        }

        x = shelf.X;
        y = shelf.Y;
        _shelves[shelfHeight] = (shelf.Y, shelf.X + width);

        return true;
    }

    private Result<GpuTexture> CreateAtlas(string name, GpuFormat format) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = GpuCard.AtlasTexels,
                Height = GpuCard.AtlasTexels,
                Format = format,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = name,
            }
        );

    /// <summary>
    /// A model, the material rows of its slots and its scale's bucket: what one capture is of.
    /// </summary>
    private readonly struct CaptureKey(ulong model, ReadOnlySpan<uint> materials, int bucket) : IEquatable<CaptureKey>
    {
        private readonly ulong _model = model;
        private readonly ulong _materials = Hash(materials);
        private readonly int _bucket = bucket;

        public bool Equals(CaptureKey other) =>
            _model == other._model && _materials == other._materials && _bucket == other._bucket;

        public override bool Equals(object? obj) => obj is CaptureKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_model, _materials, _bucket);

        // FNV-1a over the material rows.
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
/// A capture waiting to be drawn: its row and what to draw.
/// </summary>
internal readonly record struct CaptureJob(uint Row, ResidentModel Resident, uint[] Materials);
