using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ModelLodGenerator;

/// <summary>
/// Makes a <see cref="Model"/>'s levels of detail as it loads (<see cref="ILoadPostProcessor{T}"/>): its clusters at
/// every level of detail (<see cref="ClusterBuilder"/>), then a card for each mesh (<see cref="Card"/>, drawn by
/// <see cref="CardBaker"/>), drawn from a distance that grows with the model's size. They are kept in the cache
/// (<see cref="Caches"/>, which it writes them for as <see cref="ICache{T}"/> of <see cref="ModelLods"/>) under the
/// model's id, made from its version and where its cards start, so a model is made once, and again only when its file
/// or sidecar, or the setting, changes. A card's atlases are handed out with the assets (<see cref="Assets.Add{T}"/>),
/// so whatever draws loads them as it loads any texture.
/// </summary>
internal sealed class ModelLodGeneratorGem(Assets assets, Caches caches, ModelLodGeneratorSettings settings) : IGem, ILoadPostProcessor<Model>, ICache<ModelLods>
{
    /// <summary>
    /// Raised whenever the clusters or the cards would come out otherwise or how they are written changes, so what was
    /// made before is made again.
    /// </summary>
    private const int CacheVersion = 7;

    /// <summary>
    /// Half the vertical field of view the cards' distance is worked out for, in radians: half a camera's default 60
    /// degrees.
    /// </summary>
    private const float HalfFieldOfView = MathF.PI / 6;

    /// <summary>
    /// A model less tall than this share of its largest side lies flat, as a ground plane does, so a card, which stands
    /// up, would not stand in for it: it keeps its last mesh however far.
    /// </summary>
    private const float FlatShare = 0.05f;

    private readonly Assets _assets = assets;
    private readonly ModelLodGeneratorSettings _settings = settings;
    private readonly Caches _caches = caches;

    public int Version => CacheVersion;

    /// <summary>
    /// Whether it has cards, and their distance and meshes, each mesh its material slot, then its vertices and its
    /// indices as they are in memory, then its card's atlases, each its handle's id and its texels; then whether it has
    /// clusters, and their grid's step, vertices, indices and clusters as they are in memory.
    /// </summary>
    public void Write(ModelLods value, Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(value.Cards is not null);
        if (value.Cards is { } cards)
        {
            writer.Write(cards.Distance);
            writer.Write(cards.Meshes.Count);
            foreach (Mesh mesh in cards.Meshes)
            {
                writer.Write(mesh.MaterialSlot);
                writer.Write(mesh.Vertices.Length);
                writer.Write(mesh.Indices.Length);
                writer.Write(MemoryMarshal.AsBytes(mesh.Vertices.Span));
                writer.Write(MemoryMarshal.AsBytes(mesh.Indices.Span));
                if (mesh.Card is { } card)
                {
                    WriteAtlas(card.Surface);
                    WriteAtlas(card.Normals);
                }
            }
        }

        writer.Write(value.Clusters is not null);
        if (value.Clusters is { } clusters)
        {
            writer.Write(clusters.PositionStep);
            writer.Write(clusters.Vertices.Length);
            writer.Write(clusters.Indices.Length);
            writer.Write(clusters.Clusters.Length);
            writer.Write(MemoryMarshal.AsBytes(clusters.Vertices.Span));
            writer.Write(clusters.Indices.Span);
            writer.Write(MemoryMarshal.AsBytes(clusters.Clusters.Span));
        }

        void WriteAtlas(Texture atlas)
        {
            writer.Write(atlas.Handle.Id);
            writer.Write(atlas.Pixels.Length);
            writer.Write(atlas.Pixels.Span);
        }
    }

    public Result<ModelLods> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        ModelCards? cards = null;
        if (reader.ReadBoolean())
        {
            float distance = reader.ReadSingle();
            Mesh[] meshes = new Mesh[reader.ReadInt32()];
            for (int i = 0; i < meshes.Length; i++)
            {
                int slot = reader.ReadInt32();
                Vertex[] vertices = new Vertex[reader.ReadInt32()];
                uint[] indices = new uint[reader.ReadInt32()];
                bytes.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
                bytes.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
                Card card = new() { Surface = ReadAtlas(), Normals = ReadAtlas() };
                meshes[i] = new Mesh { Vertices = vertices, Indices = indices, MaterialSlot = slot, Card = card };
            }

            cards = new ModelCards { Distance = distance, Meshes = meshes };
        }

        ClusterHierarchy? hierarchy = null;
        if (reader.ReadBoolean())
        {
            float step = reader.ReadSingle();
            Vertex[] vertices = new Vertex[reader.ReadInt32()];
            byte[] indices = new byte[reader.ReadInt32()];
            Cluster[] clusters = new Cluster[reader.ReadInt32()];
            bytes.ReadExactly(MemoryMarshal.AsBytes(vertices.AsSpan()));
            bytes.ReadExactly(indices);
            bytes.ReadExactly(MemoryMarshal.AsBytes(clusters.AsSpan()));
            hierarchy = new ClusterHierarchy { Vertices = vertices, Indices = indices, Clusters = clusters, PositionStep = step };
        }

        return Result.Success(new ModelLods(cards, hierarchy));

        Texture ReadAtlas()
        {
            ulong id = reader.ReadUInt64();
            byte[] pixels = new byte[reader.ReadInt32()];
            bytes.ReadExactly(pixels);
            return AtlasOf(id, pixels);
        }
    }

    public async Task<Result<Model>> ProcessAsync(Model asset, ulong version, CancellationToken cancel)
    {
        ulong key = asset.Handle.Id;
        float cardHeight = _settings.CardHeight;
        ulong madeFrom = MadeFrom(version, cardHeight);
        if (await _caches.ReadAsync<ModelLods>(key, madeFrom, cancel).ConfigureAwait(false) is { Ok: true } cached)
        {
            Debugging.Verbose($"Read the clusters and cards of model {key} from the cache.");
            HandOut(cached.Value.Cards);
            return Result.Success(WithLods(asset, cached.Value));
        }

        ModelLods made = new(CardsOf(asset, cardHeight), ClusterBuilder.Build(asset, cancel));
        Debugging.Verbose($"Made {made.Clusters?.Clusters.Length ?? 0} clusters and {made.Cards?.Meshes.Count ?? 0} cards of model {key}.");
        await _caches.WriteAsync(key, madeFrom, made, cancel).ConfigureAwait(false);

        HandOut(made.Cards);
        return Result.Success(WithLods(asset, made));
    }

    /// <summary>
    /// <paramref name="model"/> with its clusters and its cards.
    /// </summary>
    private static Model WithLods(Model model, ModelLods lods)
    {
        return new Model { Handle = model.Handle, Meshes = model.Meshes, Clusters = lods.Clusters, Cards = lods.Cards };
    }

    /// <summary>
    /// What a model's clusters and cards are made from: its <paramref name="version"/>, and the
    /// <paramref name="cardHeight"/> its cards start at.
    /// </summary>
    private static ulong MadeFrom(ulong version, float cardHeight)
    {
        XxHash3 hash = new();
        hash.Append(BitConverter.GetBytes(version));
        hash.Append(BitConverter.GetBytes(cardHeight));
        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// <paramref name="model"/>'s cards, one for each of its meshes, drawn from where it is <paramref name="cardHeight"/>
    /// of the view tall; none for a model that lies flat.
    /// </summary>
    private static ModelCards? CardsOf(Model model, float cardHeight)
    {
        float radius = RadiusOf(model);
        if (radius <= 0 || HeightOf(model) < radius * 2 * FlatShare)
        {
            return null;
        }

        Mesh[] cards = new Mesh[model.Meshes.Count];
        for (int i = 0; i < cards.Length; i++)
        {
            (Vector3 centre, float half, byte[] surface, byte[] normals) = CardBaker.Bake(model.Meshes[i]);
            Card card = new()
            {
                Surface = AtlasOf(HandleOf(model.Handle, i, 1), surface),
                Normals = AtlasOf(HandleOf(model.Handle, i, 2), normals),
            };
            cards[i] = CardMesh(centre, half, model.Meshes[i].MaterialSlot, card);
        }

        return new ModelCards { Distance = radius / (cardHeight * MathF.Tan(HalfFieldOfView)), Meshes = cards };
    }

    /// <summary>
    /// One card as a mesh: four vertices at <paramref name="centre"/>, each with its corner in its uv, the card's half
    /// width and half height in its normal, no turn, and a tangent whose w is 0, which marks a card.
    /// </summary>
    private static Mesh CardMesh(Vector3 centre, float half, int slot, Card card)
    {
        Vector3 sides = new(half, half, 0);
        Vector4 marker = new(1, 0, 0, 0);
        Vertex[] vertices =
        [
            new() { Position = centre, Normal = sides, Tangent = marker, Uv = new Vector2(-1, -1) },
            new() { Position = centre, Normal = sides, Tangent = marker, Uv = new Vector2(1, -1) },
            new() { Position = centre, Normal = sides, Tangent = marker, Uv = new Vector2(1, 1) },
            new() { Position = centre, Normal = sides, Tangent = marker, Uv = new Vector2(-1, 1) },
        ];
        return new Mesh { Vertices = vertices, Indices = new uint[] { 0, 3, 2, 0, 2, 1 }, MaterialSlot = slot, Card = card };
    }

    /// <summary>
    /// A card's atlas as a texture: its texels, linear, read between texels and never past its edge.
    /// </summary>
    private static Texture AtlasOf(ulong id, byte[] pixels)
    {
        return new Texture
        {
            Handle = new Handle<Texture>(id),
            Width = Card.Size,
            Height = Card.Size,
            Format = GpuFormat.Rgba8Unorm,
            Pixels = pixels,
            Levels = [new TextureLevel(Card.Size, Card.Size, 0, pixels.Length)],
            Filter = GpuFilter.Linear,
            Address = GpuAddress.Clamp,
        };
    }

    /// <summary>
    /// The handle of the <paramref name="atlas"/>th atlas of <paramref name="model"/>'s <paramref name="mesh"/>th card:
    /// the same each time it is made, so whatever draws takes a new one in place of the old.
    /// </summary>
    private static ulong HandleOf(Handle<Model> model, int mesh, int atlas)
    {
        XxHash3 hash = new();
        hash.Append(BitConverter.GetBytes(model.Id));
        hash.Append(BitConverter.GetBytes(mesh));
        hash.Append(BitConverter.GetBytes(atlas));
        ulong value = hash.GetCurrentHashAsUInt64();
        return value == 0 ? 1 : value;
    }

    /// <summary>
    /// Half the largest side of the box around <paramref name="model"/>'s vertices: how big it is, which its height on
    /// screen is measured by; 0 when it has none.
    /// </summary>
    private static float RadiusOf(Model model)
    {
        (Vector3 min, Vector3 max) = BoundsOf(model);
        Vector3 size = max - min;
        return min.X > max.X ? 0 : MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2;
    }

    /// <summary>
    /// How tall <paramref name="model"/> stands, along +Y.
    /// </summary>
    private static float HeightOf(Model model)
    {
        (Vector3 min, Vector3 max) = BoundsOf(model);
        return min.Y > max.Y ? 0 : max.Y - min.Y;
    }

    private static (Vector3 Min, Vector3 Max) BoundsOf(Model model)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Mesh mesh in model.Meshes)
        {
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
        }

        return (min, max);
    }

    /// <summary>
    /// Hands the atlases of <paramref name="cards"/> out with the assets, under their handles, so whatever draws loads
    /// them by those, for as long as the model holds them.
    /// </summary>
    private void HandOut(ModelCards? cards)
    {
        foreach (Mesh mesh in cards?.Meshes ?? [])
        {
            if (mesh.Card is { } card)
            {
                _assets.Add(card.Surface);
                _assets.Add(card.Normals);
            }
        }
    }
}

/// <summary>
/// A model's cards and its clusters, as they are kept in the cache.
/// </summary>
internal sealed record ModelLods(ModelCards? Cards, ClusterHierarchy? Clusters);
