using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Magic.Ecs.Components;

/// <summary>
/// The values that a prefab gives one script of an entity, as JSON by name, such as <c>{ "depth": 0.5 }</c>.
/// </summary>
/// <remarks>
/// <para>
/// The process holds each set once, and no one changes it, so a <see cref="ScriptSlot"/> stays plain unmanaged data:
/// it holds only the set's number. Equal values are one set. Any thread reads a set, as the importers run on workers.
/// </para>
/// <para>
/// A value is JSON, so a set fits a script in any language, and the gem of the script's language gives it to the
/// script in the way of that language. In a file, the values stand beside the handle in the slot:
/// <c>{ "id": 3312, "depth": 0.5 }</c>.
/// </para>
/// </remarks>
public readonly struct ScriptValues : IEquatable<ScriptValues>
{
    /// <summary>
    /// The sets by their number, which any thread reads; 0, no values, is none of them.
    /// </summary>
    private static readonly ConcurrentDictionary<int, ImmutableArray<KeyValuePair<string, JsonElement>>> _byId = [];

    /// <summary>
    /// The number of each set by its JSON, so the same values read twice are one set.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _ids = [];

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
    /// The set of <paramref name="values"/>, which the process then holds.
    /// </summary>
    public static ScriptValues Of(ReadOnlySpan<KeyValuePair<string, JsonElement>> values)
    {
        if (values.IsEmpty)
            return None;

        ImmutableArray<KeyValuePair<string, JsonElement>>.Builder set = ImmutableArray.CreateBuilder<
            KeyValuePair<string, JsonElement>
        >(values.Length);
        StringBuilder json = new();
        foreach ((string name, JsonElement value) in values)
        {
            // A clone outlives the document that read it, which the caller frees.
            set.Add(KeyValuePair.Create(name, value.Clone()));
            json.Append(name).Append(':').Append(value.GetRawText()).Append(';');
        }

        ImmutableArray<KeyValuePair<string, JsonElement>> all = set.MoveToImmutable();

        return new ScriptValues(_ids.GetOrAdd(json.ToString(), _ => Add(all)));
    }

    /// <summary>
    /// Whether both are the same set of values.
    /// </summary>
    public bool Equals(ScriptValues other) => _id == other._id;

    public override bool Equals(object? obj) => obj is ScriptValues other && Equals(other);

    public override int GetHashCode() => _id;

    /// <summary>
    /// Holds <paramref name="values"/> as a new set and gives its number.
    /// </summary>
    private static int Add(ImmutableArray<KeyValuePair<string, JsonElement>> values)
    {
        int id = Interlocked.Increment(ref _lastId);
        _byId[id] = values;

        return id;
    }
}
