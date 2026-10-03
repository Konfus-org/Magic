using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StreamingGem;

/// <summary>
/// Reads a chunk file's bytes into what the main thread spawns (<see cref="PreparedChunk"/>): everything of a spawn
/// that does not touch the ECS, so it runs on a worker. The JSON is walked once, as bytes: a component is set from
/// its JSON by the name it was written under, resolved against every loaded assembly, so gems add components without
/// registering anything, and its value goes straight into a column of its own type. Nothing is made per entity or
/// per component on the way (no text of the file, no JSON tree, no boxed value), because a domain streams thousands
/// of chunks and what each leaves behind is what the garbage collector stops the frame for. Components that cannot
/// be read are warned about once and skipped. Any thread.
/// </summary>
internal sealed class ChunkReader
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly HashSet<string> _warned = []; // locked: several workers read at once

    // Component names -> types; replaced whole when gems change.
    private volatile ComponentTypes? _types;

    /// <summary>
    /// Gems changed: the component types are looked for again, and what was warned about is said again.
    /// </summary>
    public void Forget()
    {
        _types = null;
        lock (_warned)
            _warned.Clear();
    }

    /// <summary>
    /// The chunk's entities flattened parent-first, their component names resolved and their values read. Throws <see cref="JsonException"/> for a file that is not a chunk,
    /// and <see cref="OperationCanceledException"/> between two entities once <paramref name="cancel"/> is cancelled.
    /// </summary>
    public PreparedChunk Read(Chunk chunk, CancellationToken cancel = default)
    {
        Reading reading = new(_types ??= new ComponentTypes(FindComponentTypes()), chunk.Path);
        Utf8JsonReader reader = new(chunk.Json, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"{chunk.Path} is not a JSON object.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool entities = Named(ref reader, "entities"u8);
            reader.Read();
            if (entities)
                ReadEntities(ref reader, reading, parent: -1, depth: 0, cancel);
            else
                reader.Skip();
        }

        return new PreparedChunk([.. reading.Entities], [.. reading.Components], [.. reading.Columns]);
    }

    /// <summary>
    /// Is the property the reader is on called this? Without regard to case, as the asset JSON is read everywhere.
    /// </summary>
    private static bool Named(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        return reader.ValueTextEquals(name)
            || (!reader.ValueIsEscaped && !reader.HasValueSequence && Ascii.EqualsIgnoreCase(reader.ValueSpan, name));
    }

    private void ReadEntities(ref Utf8JsonReader reader, Reading reading, int parent, int depth, CancellationToken cancel)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            cancel.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject)
                ReadEntity(ref reader, reading, parent, depth, cancel);
            else
                reader.Skip();
        }
    }

    private void ReadEntity(ref Utf8JsonReader reader, Reading reading, int parent, int depth, CancellationToken cancel)
    {
        // Its place is taken now, before its children take theirs, and filled in once the whole object is read.
        int index = reading.Entities.Count;
        reading.Entities.Add(default);

        // Its children's components are read in between, so its own wait here until they can go in side by side.
        List<ComponentRef> own = reading.Own(depth);
        ulong id = 0;
        string? name = null;
        Tags? tags = null;
        JsonElement[] scripts = [];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (Named(ref reader, "id"u8))
            {
                reader.Read();
                id = reader.GetUInt64();
            }
            else if (Named(ref reader, "name"u8))
            {
                reader.Read();
                name = reader.GetString();
            }
            else if (Named(ref reader, "tags"u8))
            {
                reader.Read();
                tags = ReadTags(ref reader, reading, name);
            }
            else if (Named(ref reader, "components"u8))
            {
                reader.Read();
                ReadComponents(ref reader, reading, own, name);
            }
            else if (Named(ref reader, "scripts"u8))
            {
                reader.Read();
                scripts = ReadScripts(ref reader);
            }
            else if (Named(ref reader, "children"u8))
            {
                reader.Read();
                ReadEntities(ref reader, reading, index, depth + 1, cancel);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        reading.Entities[index] = new PreparedEntity(parent, name, id, tags, reading.Components.Count, own.Count, scripts);
        reading.Components.AddRange(own);
    }

    private Tags? ReadTags(ref Utf8JsonReader reader, Reading reading, string? entity)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }

        Span<Tag> tags = stackalloc Tag[Tags.Capacity];
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                continue;
            }

            if (count < Tags.Capacity)
                tags[count] = reading.TagOf(ref reader);

            count++;
        }

        if (count > Tags.Capacity)
            Debugging.Log.Warn($"{reading.File}: entity {entity ?? "(unnamed)"} has {count} tags; only {Tags.Capacity} fit.");

        return count == 0 ? null : Tags.Of(tags[..Math.Min(count, Tags.Capacity)]);
    }

    private void ReadComponents(ref Utf8JsonReader reader, Reading reading, List<ComponentRef> own, string? entity)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            int column = ColumnOf(ref reader, reading);
            reader.Read();
            if (column < 0)
            {
                reader.Skip();
                continue;
            }

            try
            {
                own.Add(new ComponentRef(column, reading.Columns[column].Read(ref reader)));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // The serializer leaves the reader where the value began.
                Type type = reading.Columns[column].Type;
                Warn($"{reading.File}:{type.Name}", $"{reading.File}: component {type.Name} on {entity ?? "(unnamed)"} could not be read: {ex.Message}");
                reader.Skip();
            }
        }
    }

    private static JsonElement[] ReadScripts(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        // Only the script's class, known once it is loaded, says what its values are: they stay JSON.
        List<JsonElement> scripts = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            scripts.Add(JsonElement.ParseValue(ref reader));

        return [.. scripts];
    }

    /// <summary>
    /// The column of the component the property the reader is on names; -1 for a name that is no component
    /// (warned about). A chunk names the same few components over and over, so each name is looked up once.
    /// </summary>
    private int ColumnOf(ref Utf8JsonReader reader, Reading reading)
    {
        bool plain = !reader.ValueIsEscaped && !reader.HasValueSequence;
        if (plain)
        {
            foreach ((byte[] known, int column) in reading.ColumnByName)
            {
                if (reader.ValueSpan.SequenceEqual(known))
                    return column;
            }
        }

        string name = reader.GetString() ?? "";
        int found = -1;
        if (Resolve(reading.Types, name, reading.File) is { } type)
        {
            found = reading.Columns.FindIndex(column => column.Type == type);
            if (found < 0)
            {
                try
                {
                    reading.Columns.Add(reading.Types.NewColumn(type));
                    found = reading.Columns.Count - 1;
                }
                catch (ArgumentException)
                {
                    Warn($"{reading.File}:{name}", $"{reading.File}: component {name} is a {type.Name}, which holds references and cannot be a component; skipped.");
                }
            }
        }

        if (plain)
            reading.ColumnByName.Add((reader.ValueSpan.ToArray(), found));

        return found;
    }

    /// <summary>
    /// The struct a component name means: its type name or full name, among every <see cref="IComponent"/> in the process.
    /// </summary>
    private Type? Resolve(ComponentTypes types, string name, string file)
    {
        if (!types.ByName.TryGetValue(name, out List<Type>? found))
        {
            Warn(name, $"{file}: no loaded component type is named {name}; skipped.");
            return null;
        }

        if (found.Count == 1)
            return found[0];

        Warn(name, $"{file}: {name} names {found.Count} component types ({string.Join(", ", found.Select(type => type.FullName))}); use the full name.");
        return null;
    }

    /// <summary>
    /// Logs <paramref name="message"/> the first time <paramref name="key"/> is seen since gems last changed.
    /// </summary>
    private void Warn(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key))
                return;
        }

        Debugging.Log.Warn(message);
    }

    private static Dictionary<string, List<Type>> FindComponentTypes()
    {
        // Only Core and what references it can declare an IComponent; skipping the framework is most of the time saved.
        Assembly core = typeof(IComponent).Assembly;
        string? coreName = core.GetName().Name;
        Dictionary<string, List<Type>> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || (assembly != core && !assembly.GetReferencedAssemblies().Any(reference => reference.Name == coreName)))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }

            foreach (Type type in types)
            {
                if (!type.IsValueType || type.IsGenericTypeDefinition || !typeof(IComponent).IsAssignableFrom(type))
                    continue;

                Add(found, type.Name, type);
                if (type.FullName is { } full && full != type.Name)
                    Add(found, full, type);
            }
        }

        return found;

        static void Add(Dictionary<string, List<Type>> found, string key, Type type)
        {
            if (!found.TryGetValue(key, out List<Type>? list))
                found[key] = list = [];

            if (!list.Contains(type))
                list.Add(type);
        }
    }

    /// <summary>
    /// One chunk being read: what has been read so far, and what its names turned out to mean.
    /// </summary>
    private sealed class Reading(ComponentTypes types, string file)
    {
        private readonly List<List<ComponentRef>> _own = [];
        private readonly List<(byte[] Name, Tag Tag)> _tags = [];

        public ComponentTypes Types { get; } = types;

        public string File { get; } = file;

        public List<PreparedEntity> Entities { get; } = [];

        public List<ComponentRef> Components { get; } = [];

        public List<ComponentColumn> Columns { get; } = [];

        /// <summary>
        /// The component names seen, as they are written in the file, and the column of each (-1 for none).
        /// </summary>
        public List<(byte[] Name, int Column)> ColumnByName { get; } = [];

        /// <summary>
        /// An empty list for the components of the entity being read at this depth of the hierarchy.
        /// </summary>
        public List<ComponentRef> Own(int depth)
        {
            while (_own.Count <= depth)
                _own.Add([]);

            _own[depth].Clear();
            return _own[depth];
        }

        /// <summary>
        /// The tag the string the reader is on names; each name is hashed once per chunk.
        /// </summary>
        public Tag TagOf(ref Utf8JsonReader reader)
        {
            bool plain = !reader.ValueIsEscaped && !reader.HasValueSequence;
            if (plain)
            {
                foreach ((byte[] known, Tag tag) in _tags)
                {
                    if (reader.ValueSpan.SequenceEqual(known))
                        return tag;
                }
            }

            Tag made = Tag.Of(reader.GetString() ?? "");
            if (plain)
                _tags.Add((reader.ValueSpan.ToArray(), made));

            return made;
        }
    }

    /// <summary>
    /// Every <see cref="IComponent"/> by name, and how to make a column for each, as one snapshot workers share.
    /// </summary>
    private sealed class ComponentTypes(Dictionary<string, List<Type>> byName)
    {
        private readonly ConcurrentDictionary<Type, Type> _columns = new();

        public Dictionary<string, List<Type>> ByName { get; } = byName;

        /// <summary>
        /// Throws ArgumentException for a struct that is not unmanaged.
        /// </summary>
        public ComponentColumn NewColumn(Type type)
        {
            object? column = Activator.CreateInstance(_columns.GetOrAdd(type, static component => typeof(ComponentColumn<>).MakeGenericType(component)));
            return column as ComponentColumn ?? throw new ArgumentException($"No column could be made for {type.Name}.", nameof(type));
        }
    }
}

/// <summary>
/// A chunk as the main thread spawns it: its entities parent-first, the components of each as a run of
/// <see cref="Components"/>, and every component's values in the column of its type.
/// </summary>
internal sealed class PreparedChunk(PreparedEntity[] entities, ComponentRef[] components, ComponentColumn[] columns)
{
    public static PreparedChunk Empty { get; } = new([], [], []);

    public PreparedEntity[] Entities { get; } = entities;

    public ComponentRef[] Components { get; } = components;

    public ComponentColumn[] Columns { get; } = columns;
}

/// <summary>
/// One entity of a chunk; <see cref="Parent"/> indexes an earlier entity, or is -1 for the chunk's root. Its
/// components are the <see cref="ComponentCount"/> of <see cref="PreparedChunk.Components"/> from <see cref="FirstComponent"/> on.
/// </summary>
internal readonly record struct PreparedEntity(int Parent, string? Name, ulong Id, Tags? Tags, int FirstComponent, int ComponentCount, JsonElement[] Scripts);

/// <summary>
/// One component of one entity: which of <see cref="PreparedChunk.Columns"/> it is in, and where.
/// </summary>
internal readonly record struct ComponentRef(int Column, int Row);

/// <summary>
/// The values a chunk has of one component type, for however many of its entities have one.
/// </summary>
internal abstract class ComponentColumn
{
    public abstract Type Type { get; }

    /// <summary>
    /// Reads the JSON value the reader is on as one more of the component; the row it went to.
    /// </summary>
    public abstract int Read(ref Utf8JsonReader reader);

    /// <summary>
    /// Gives <paramref name="entity"/> the component at <paramref name="row"/>.
    /// </summary>
    public abstract void Set(IEcs ecs, Handle entity, int row);

    /// <summary>
    /// Adds the different values of the column to <paramref name="holders"/>, for a type that can name assets (has a
    /// handle somewhere in it): a chunk's entities mostly draw the same few things, and each is looked into once.
    /// </summary>
    public abstract void AddAssetHolders(HashSet<object> holders);
}

internal sealed class ComponentColumn<T> : ComponentColumn where T : unmanaged
{
    // Worked out once per type: most components (a transform) name no asset, and are never looked at.
    private static readonly bool NamesAssets = ((object)default(T)).ValuesOf<Handle<Asset>>().Any();

    private readonly List<T> _values = [];

    public override Type Type => typeof(T);

    public override int Read(ref Utf8JsonReader reader)
    {
        _values.Add(JsonSerializer.Deserialize<T>(ref reader, AssetJson.Options));

        return _values.Count - 1;
    }

    public override void Set(IEcs ecs, Handle entity, int row)
    {
        ecs.Set(entity, in CollectionsMarshal.AsSpan(_values)[row]);
    }

    public override void AddAssetHolders(HashSet<object> holders)
    {
        if (!NamesAssets)
            return;

        HashSet<T> different = new(_values, SameBytes.Instance);
        foreach (T value in different)
            holders.Add(value);
    }

    /// <summary>
    /// Two values of an unmanaged struct are the same when their bytes are, which needs no boxing to tell.
    /// </summary>
    private sealed class SameBytes : IEqualityComparer<T>
    {
        public static SameBytes Instance { get; } = new();

        public bool Equals(T left, T right)
        {
            return MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in right)));
        }

        public int GetHashCode(T value)
        {
            HashCode hash = new();
            hash.AddBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

            return hash.ToHashCode();
        }
    }
}
