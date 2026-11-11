using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// A <see cref="Renderer"/>'s materials by the slot a model's mesh names (<see cref="Mesh.MaterialSlot"/>), each a
/// <see cref="MaterialInstance"/>: a list no one changes, held once for the process and shared by every renderer that
/// holds the same, so the component stays a few bytes however many slots it fills. Equal lists are equal sets. A slot
/// changes by making the set that has it: <c>renderer.Materials = renderer.Materials.With(0, slot)</c>. Sets read from a
/// file are kept for the process; sets made in code are freed once no renderer has held them for a while, so a set kept
/// anywhere but a renderer may come back empty. In a file, an array of instances by slot, as
/// <see cref="MaterialSlots"/>.
/// </summary>
[JsonConverter(typeof(MaterialSetJsonConverter))]
public readonly struct MaterialSet : IEquatable<MaterialSet>
{
    public const int Capacity = MaterialSlots.Capacity;

    /// <summary>
    /// How long at least passes between two frees of the sets no renderer holds.
    /// </summary>
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// The sets by id, which any thread reads; 0, the empty set, is none of them.
    /// </summary>
    private static readonly ConcurrentDictionary<int, Stored> _byId = [];

    /// <summary>
    /// The ids of the sets read from files, by their slots: kept for the process.
    /// </summary>
    private static readonly ConcurrentDictionary<Slots, int> _read = [];

    /// <summary>
    /// The ids of the sets made in code, by their slots: freed by <see cref="Sweep(IReadOnlySet{int})"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<Slots, int> _made = [];

    /// <summary>
    /// The ids live renderers hold, gathered for a sweep. Game thread.
    /// </summary>
    private static readonly HashSet<int> _held = [];

    private static int _lastId;

    /// <summary>
    /// How many sweeps have run. Game thread.
    /// </summary>
    private static int _sweeps;

    private static long _sweptAt = Stopwatch.GetTimestamp();

    private static int _framesSinceSweep;

    private MaterialSet(int id) => Id = id;

    public static MaterialSet Empty => default;

    /// <summary>
    /// Every slot, up to the last filled one.
    /// </summary>
    public ImmutableArray<MaterialInstance> All => _byId.TryGetValue(Id, out Stored? stored) ? stored.Slots.All : [];

    public int Count => All.Length;

    /// <summary>
    /// How many sets the process holds.
    /// </summary>
    internal static int StoredCount => _byId.Count;

    /// <summary>
    /// The set's number in the process, which a file never holds.
    /// </summary>
    internal int Id { get; }

    /// <summary>
    /// The slot's instance, an empty one past the last filled slot.
    /// </summary>
    public MaterialInstance this[int slot]
    {
        get
        {
            ImmutableArray<MaterialInstance> all = All;

            return (uint)slot < (uint)all.Length ? all[slot] : default;
        }
    }

    public static bool operator ==(MaterialSet left, MaterialSet right) => left.Equals(right);

    public static bool operator !=(MaterialSet left, MaterialSet right) => !left.Equals(right);

    /// <summary>
    /// The set of these instances, by slot; at most <see cref="Capacity"/>, empty slots after the last filled one left
    /// out.
    /// </summary>
    public static MaterialSet Of(params ReadOnlySpan<MaterialInstance> instances)
    {
        Slots slots = ToSlots(instances);
        if (slots.All.IsEmpty)
            return Empty;

        // A made set equal to one read from a file is that one.
        if (_read.TryGetValue(slots, out int readId))
            return new MaterialSet(readId);

        int id = _made.GetOrAdd(slots, AddStored);
        if (_byId.TryGetValue(id, out Stored? stored))
            stored.HeldAt = _sweeps;

        return new MaterialSet(id);
    }

    /// <summary>
    /// The set with <paramref name="slot"/> holding <paramref name="instance"/>, the others as they are.
    /// </summary>
    public MaterialSet With(int slot, MaterialInstance instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Capacity);

        ImmutableArray<MaterialInstance> all = All;
        Span<MaterialInstance> slots = stackalloc MaterialInstance[Math.Max(all.Length, slot + 1)];
        all.AsSpan().CopyTo(slots);
        slots[slot] = instance;

        return Of(slots);
    }

    /// <summary>
    /// Whether both hold the same material, setting the same params, in every slot.
    /// </summary>
    public bool Equals(MaterialSet other) => Id == other.Id || All.AsSpan().SequenceEqual(other.All.AsSpan());

    public override bool Equals(object? obj) => obj is MaterialSet other && Equals(other);

    public override int GetHashCode() => _byId.TryGetValue(Id, out Stored? stored) ? stored.Slots.GetHashCode() : 0;

    /// <summary>
    /// The set as it was read from a file, kept for the process.
    /// </summary>
    internal static MaterialSet Read(ReadOnlySpan<MaterialInstance> instances)
    {
        Slots slots = ToSlots(instances);

        return slots.All.IsEmpty ? Empty : new MaterialSet(_read.GetOrAdd(slots, AddStored));
    }

    /// <summary>
    /// Frees the sets made in code that no live renderer has held since the sweep before, a few times a second and at
    /// least 3 frames apart, so no frame still being drawn holds one. Game thread, once a frame, between the gems'
    /// hooks.
    /// </summary>
    internal static void SweepIfDue(IEcs ecs)
    {
        _framesSinceSweep++;
        if (_framesSinceSweep < 3 || Stopwatch.GetElapsedTime(_sweptAt) < _sweepInterval || _made.IsEmpty)
            return;

        // Renderers placed together mostly hold the same set, so each run of one id is looked up once.
        _held.Clear();
        int last = 0;
        foreach (EcsChunk<Renderer> chunk in ecs.Query<Renderer>())
        {
            foreach (Renderer renderer in chunk.First)
            {
                int id = renderer.Materials.Id;
                if (id == last)
                    continue;

                _held.Add(id);
                last = id;
            }
        }

        Sweep(_held);
    }

    /// <summary>
    /// Frees the sets made in code that are not in <paramref name="held"/> now, were not at the sweep before, and were
    /// not asked for since.
    /// </summary>
    internal static void Sweep(IReadOnlySet<int> held)
    {
        _framesSinceSweep = 0;
        _sweptAt = Stopwatch.GetTimestamp();
        _sweeps++;
        foreach (KeyValuePair<Slots, int> made in _made)
        {
            if (!_byId.TryGetValue(made.Value, out Stored? stored))
                continue;

            if (held.Contains(made.Value))
                stored.HeldAt = _sweeps;
            else if (_sweeps - stored.HeldAt >= 2)
            {
                _made.TryRemove(made);
                _byId.TryRemove(made.Value, out _);
            }
        }
    }

    /// <summary>
    /// The instances up to the last filled one, at most <see cref="Capacity"/>.
    /// </summary>
    private static Slots ToSlots(ReadOnlySpan<MaterialInstance> instances)
    {
        int count = instances.Length;
        while (count > 0 && instances[count - 1].Handle == Handle<Material>.None)
        {
            count--;
        }

        if (count > Capacity)
            throw new ArgumentException(
                $"A material set holds at most {Capacity} slots, not {count}.",
                nameof(instances)
            );

        return new([.. instances[..count]]);
    }

    private static int AddStored(Slots slots)
    {
        int id = Interlocked.Increment(ref _lastId);
        _byId[id] = new Stored(slots) { HeldAt = _sweeps };

        return id;
    }

    /// <summary>
    /// A set's slots, compared by what they hold.
    /// </summary>
    private readonly struct Slots(ImmutableArray<MaterialInstance> all) : IEquatable<Slots>
    {
        private readonly int _hash = GetHash(all);

        public ImmutableArray<MaterialInstance> All { get; } = all;

        public bool Equals(Slots other) => _hash == other._hash && All.AsSpan().SequenceEqual(other.All.AsSpan());

        public override bool Equals(object? obj) => obj is Slots other && Equals(other);

        public override int GetHashCode() => _hash;

        private static int GetHash(ImmutableArray<MaterialInstance> all)
        {
            HashCode hash = default;
            foreach (MaterialInstance instance in all)
            {
                hash.Add(instance);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A held set, and the last sweep a renderer held it at or, when it was asked for since, the sweep before that ask.
    /// Game thread, but for the slots.
    /// </summary>
    private sealed class Stored(Slots slots)
    {
        public Slots Slots { get; } = slots;

        public int HeldAt { get; set; }
    }
}
