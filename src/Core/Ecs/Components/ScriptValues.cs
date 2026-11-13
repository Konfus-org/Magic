using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Magic.Ecs.Components;

/// <summary>
/// The values that a prefab gives one script of an entity, or the state that a save kept of a copy of it, as JSON by
/// name, such as <c>{ "depth": 0.5 }</c>.
/// </summary>
/// <remarks>
/// <para>
/// The process holds each set, and no one changes it, so a <see cref="ScriptSlot"/> stays plain unmanaged data: it
/// holds only the set's number. Any thread reads a set, as the importers run on workers.
/// </para>
/// <para>
/// A file's set lives while the process runs, as the file can be read into a new container at any time, and the sets
/// of equal JSON are one set, so reading the same file again adds none. A set that a save kept lives only while a slot
/// holds it: a save makes one set for each copy that runs, and frees the sets that no slot holds any more.
/// </para>
/// <para>
/// A value is JSON, so a set fits a script in any language, and the gem of the script's language gives it to the
/// script in the way of that language.
/// </para>
/// </remarks>
public readonly struct ScriptValues : IEquatable<ScriptValues>
{
    /// <summary>
    /// The sets by their number, which any thread reads; 0, no values, is none of them.
    /// </summary>
    private static readonly ConcurrentDictionary<int, ImmutableArray<KeyValuePair<string, JsonElement>>> _byId = [];

    /// <summary>
    /// The number of each set that a file gave, by the JSON of its values, so one set serves every file of equal
    /// values.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _ofFiles = [];

    /// <summary>
    /// The number of each set that a save kept, as the keys: the sets that <see cref="FreeUnheld"/> frees.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> _ofSaves = [];

    private static int _lastId;

    /// <summary>
    /// The set's number in the process, which a file never holds.
    /// </summary>
    private readonly int _id;

    private ScriptValues(int id) => _id = id;

    public static ScriptValues None => default;

    /// <summary>
    /// Each value by its name in the file, in the order of the file; empty when there are none.
    /// </summary>
    public ImmutableArray<KeyValuePair<string, JsonElement>> All =>
        _byId.TryGetValue(_id, out ImmutableArray<KeyValuePair<string, JsonElement>> all) ? all : [];

    public static bool operator ==(ScriptValues left, ScriptValues right) => left.Equals(right);

    public static bool operator !=(ScriptValues left, ScriptValues right) => !left.Equals(right);

    /// <summary>
    /// The set of <paramref name="values"/> that a file gives, which the process then holds.
    /// </summary>
    public static ScriptValues Of(ReadOnlySpan<KeyValuePair<string, JsonElement>> values)
    {
        if (values.IsEmpty)
            return None;

        string json = GetJson(values);
        if (_ofFiles.TryGetValue(json, out int shared))
            return new ScriptValues(shared);

        // Another thread can read the same values at the same time; the set that loses the race is held by nothing.
        int made = Add(values);
        shared = _ofFiles.GetOrAdd(json, made);
        if (shared != made)
            _byId.TryRemove(made, out _);

        return new ScriptValues(shared);
    }

    /// <summary>
    /// Whether both are the same set of values: the files of equal values give one set, and each set that a save kept
    /// is its own.
    /// </summary>
    public bool Equals(ScriptValues other) => _id == other._id;

    public override bool Equals(object? obj) => obj is ScriptValues other && Equals(other);

    public override int GetHashCode() => _id;

    /// <summary>
    /// The set of <paramref name="state"/>, the state that a save keeps of one copy of a script, which only the slot
    /// that a <see cref="ScriptContainer.Keep"/> puts it in holds.
    /// </summary>
    internal static ScriptValues OfSave(ReadOnlySpan<KeyValuePair<string, JsonElement>> state)
    {
        if (state.IsEmpty)
            return None;

        int id = Add(state);
        _ofSaves[id] = 0;

        return new ScriptValues(id);
    }

    /// <summary>
    /// Frees each set that a save made and that <paramref name="held"/> does not hold, so the state of a copy that
    /// went goes with it. Call it on the game thread, with the sets of every slot of the world.
    /// </summary>
    internal static void FreeUnheld(IReadOnlySet<ScriptValues> held)
    {
        foreach (int id in _ofSaves.Keys)
        {
            if (held.Contains(new ScriptValues(id)))
                continue;

            _ofSaves.TryRemove(id, out _);
            _byId.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The JSON object of <paramref name="values"/>, which tells one set of values from every other set.
    /// </summary>
    private static string GetJson(ReadOnlySpan<KeyValuePair<string, JsonElement>> values)
    {
        ArrayBufferWriter<byte> json = new();
        using Utf8JsonWriter writer = new(json);
        writer.WriteStartObject();
        foreach ((string name, JsonElement value) in values)
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(json.WrittenSpan);
    }

    /// <summary>
    /// Holds <paramref name="values"/> as a new set and gives its number.
    /// </summary>
    private static int Add(ReadOnlySpan<KeyValuePair<string, JsonElement>> values)
    {
        ImmutableArray<KeyValuePair<string, JsonElement>>.Builder set = ImmutableArray.CreateBuilder<
            KeyValuePair<string, JsonElement>
        >(values.Length);
        foreach ((string name, JsonElement value) in values)
        {
            // A clone outlives the document that read it, which the caller frees.
            set.Add(KeyValuePair.Create(name, value.Clone()));
        }

        int id = Interlocked.Increment(ref _lastId);
        _byId[id] = set.MoveToImmutable();

        return id;
    }
}
