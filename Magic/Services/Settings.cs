using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Utils;
using System.Reflection;
using System.Text.Json;

namespace Magic.Services;

/// <summary>
/// The settings, a service: a flat pile of <c>"Owner.Property"</c> values (<see cref="Values"/>) written into one object
/// per <see cref="SettingsAttribute"/> class of every loaded assembly (<see cref="All"/>), which gems ask for by type.
/// The values are three layers, each over the one before: the applied <see cref="Assets.Preset"/>, the start-up
/// <c>--set</c> lines, which stay over every preset, and what was <see cref="Set"/> while running (the settings window,
/// the console), which lasts until the next preset. The flow is one way: whatever changes a value changes it here,
/// the values are written into the objects, gems read the objects every frame. Pass parameters are values too
/// (<c>"ShadowPlan.distance"</c>); the renderer reads those from <see cref="Values"/>. Main thread only.
/// </summary>
public sealed class Settings : IRegisterFromGem<SettingsAttribute>
{
    private readonly Dictionary<string, JsonElement> _startup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonElement> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _objects = [];

    /// <summary>
    /// <paramref name="startup"/> are the <c>--set</c> lines, <c>Owner.Property=value</c>: the value as JSON, or else
    /// as text (what an enum wants). They stay over every preset applied.
    /// </summary>
    public Settings(IEnumerable<string>? startup = null)
    {
        Add(_startup, startup ?? []);
        Values = new Dictionary<string, JsonElement>(_startup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every value, by <c>"Owner.Property"</c>, ignoring case. Made again by <see cref="Apply"/> and <see cref="Set"/>,
    /// never changed, so a reader can tell a new set by the reference.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Values { get; private set; }

    /// <summary>
    /// The preset applied last; null before the first.
    /// </summary>
    public Preset? Preset { get; private set; }

    /// <summary>
    /// The settings objects of every loaded assembly, in the order they loaded.
    /// </summary>
    public IReadOnlyList<object> All => _objects;

    /// <summary>
    /// Lays <paramref name="preset"/> under the start-up lines and writes the values into every object: what neither
    /// names goes back to its default, and what was <see cref="Set"/> while running is dropped.
    /// </summary>
    public void Apply(Preset preset)
    {
        Preset = preset;
        _live.Clear();
        Rewrite();
    }

    /// <summary>
    /// Changes values while running: <paramref name="lines"/> are <c>Owner.Property=value</c>, read as <c>--set</c> reads
    /// them, and last until the next <see cref="Apply"/>. Returns the lines that are not <c>Owner.Property=value</c>,
    /// which are left out.
    /// </summary>
    public string[] Set(IEnumerable<string> lines)
    {
        string[] unused = Add(_live, lines);
        Rewrite();
        return unused;
    }

    /// <summary>
    /// The name a settings class is known by in <see cref="Values"/>: its <see cref="SettingsAttribute.Name"/>.
    /// </summary>
    public static string NameOf(object settings)
    {
        return settings.GetType().GetCustomAttribute<SettingsAttribute>()?.Name ?? settings.GetType().Name;
    }

    /// <summary>
    /// A value as <c>--set</c> and the console write it: JSON, or else text (what an enum wants).
    /// </summary>
    public static JsonElement Parse(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    /// <summary>
    /// Each <c>Owner.Property=value</c> line into <paramref name="layer"/>; returns those that are not one.
    /// </summary>
    private static string[] Add(Dictionary<string, JsonElement> layer, IEnumerable<string> lines)
    {
        List<string> unused = [];
        foreach (string line in lines)
        {
            string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].IndexOf('.', StringComparison.Ordinal) > 0)
                layer[pair[0]] = Parse(pair[1]);
            else
                unused.Add(line);
        }

        return [.. unused];
    }

    /// <summary>
    /// The preset, the start-up lines and the live ones, each over the last, made the <see cref="Values"/> and written
    /// into every object.
    /// </summary>
    private void Rewrite()
    {
        Dictionary<string, JsonElement> values = new(Preset?.Values ?? [], StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonElement value) in _startup.Concat(_live))
            values[key] = value;

        Values = values;
        foreach (object settings in _objects)
            Write(values, settings);
    }

    /// <summary>
    /// Sets every public read-write property of <paramref name="settings"/>: to its value in <paramref name="values"/>,
    /// or else (none, or one that does not fit, warned) to its default.
    /// </summary>
    private static void Write(IReadOnlyDictionary<string, JsonElement> values, object settings)
    {
        Type type = settings.GetType();
        string name = NameOf(settings);
        object defaults = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{type.FullName} could not be made with its defaults.");

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
                continue;

            object? value = property.GetValue(defaults);
            if (values.TryGetValue($"{name}.{property.Name}", out JsonElement given))
            {
                try
                {
                    value = given.Deserialize(property.PropertyType, AssetJson.Options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    Debugging.Log.Warn($"Setting {name}.{property.Name} = {given} does not fit a {property.PropertyType.Name}, so it is left at its default: {ex.Message}");
                }
            }

            property.SetValue(settings, value);
        }

        foreach (string key in values.Keys)
        {
            if (key.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && type.GetProperty(key[(name.Length + 1)..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null)
                Debugging.Log.Warn($"Setting {key} names nothing: {type.FullName} has no such property.");
        }
    }

    object? IRegisterFromGem.Register(Type type)
    {
        object settings = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{type.FullName} could not be made with its defaults.");
        Write(Values, settings);
        _objects.Add(settings);
        return settings;
    }

    void IRegisterFromGem.Unregister(Type type)
    {
        _objects.RemoveAll(settings => settings.GetType() == type);
    }
}
