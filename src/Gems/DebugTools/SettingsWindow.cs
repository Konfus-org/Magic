using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Threading;
using Magic.Utils;

namespace Magic.DebugTools;

/// <summary>
/// A window of every gem's settings, under a header per category, each with a control for its type: a checkbox, a
/// number dragged, a choice of an enum's values, or a number per part of a size or vector. A change is set at once and
/// the summary shows while the mouse rests on the control. A setting that restarts the run says so, and is set only
/// when its button is clicked. Save writes them for this user and project.
/// </summary>
internal sealed class SettingsWindow(IDebugUI ui, Settings settings)
{
    /// <summary>
    /// The categories in the order their first setting is listed, gathered each frame drawn.
    /// </summary>
    private readonly List<string> _categories = [];

    /// <summary>
    /// What each setting's row draws with, by key, made the first time it shows.
    /// </summary>
    private readonly Dictionary<string, Row> _rows = [];

    /// <summary>
    /// The values chosen for settings that restart the run, by key, until they are applied.
    /// </summary>
    private readonly Dictionary<string, object> _pending = [];

    public void Update(ref bool isOpen)
    {
        if (!isOpen)
            return;

        ui.Begin("Settings", ref isOpen, scrollable: true);
        if (ui.Button("Save"))
            SaveAsync().FireAndForget();

        ui.Tooltip("Saves the settings that differ from their defaults, for this user and project.");

        // A category can have settings in several gems, so its rows are gathered under one header.
        ImmutableArray<Setting> options = settings.Options;
        _categories.Clear();
        foreach (Setting option in options)
        {
            if (!_categories.Contains(option.Category))
                _categories.Add(option.Category);
        }

        foreach (string category in _categories)
        {
            if (!ui.Header(category))
                continue;

            foreach (Setting option in options)
            {
                if (option.Category == category)
                    Draw(option);
            }
        }

        ui.End();
    }

    private static bool IsNumber(Type type) => Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;

    private static Row CreateRow(Setting option)
    {
        string name = option.Key[(option.Category.Length + 1)..];
        string label = option.RequiresRestart ? $"{name} (restarts)##{option.Key}" : $"{name}##{option.Key}";
        bool isStruct = option.Type is { IsValueType: true, IsPrimitive: false, IsEnum: false };
        Part[] parts = isStruct
            ?
            [
                .. option
                    .Type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(field => IsNumber(field.FieldType))
                    .Select(field => new Part(
                        $"{name} {field.Name}##{option.Key}.{field.Name}",
                        field.FieldType,
                        field.GetValue,
                        field.SetValue
                    )),
                .. option
                    .Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property =>
                        property.SetMethod is { IsPublic: true }
                        && property.GetIndexParameters().Length == 0
                        && IsNumber(property.PropertyType)
                    )
                    .Select(property => new Part(
                        $"{name} {property.Name}##{option.Key}.{property.Name}",
                        property.PropertyType,
                        property.GetValue,
                        property.SetValue
                    )),
            ]
            : [];

        return new Row(
            label,
            option.Type.IsEnum ? Enum.GetNames(option.Type) : [],
            option.Type.IsEnum ? Enum.GetValues(option.Type) : Array.Empty<object>(),
            parts,
            $"Apply {name} and restart##{option.Key}",
            $"{name}: a {option.Type.Name}, set with the console's set {option.Key}"
        );
    }

    private static object ToNumber(float value, Type type) =>
        Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

    /// <summary>
    /// A setting's control, and its button while a value that restarts the run waits.
    /// </summary>
    private void Draw(Setting option)
    {
        if (!_rows.TryGetValue(option.Key, out Row? row))
        {
            row = CreateRow(option);
            _rows[option.Key] = row;
        }

        object? value = _pending.GetValueOrDefault(option.Key) ?? option.Value;
        if (Edit(option, row, value) is { } changed)
        {
            if (option.RequiresRestart)
                _pending[option.Key] = changed;
            else
                Set(option.Key, changed);
        }

        if (_pending.TryGetValue(option.Key, out object? pending) && ui.Button(row.ApplyLabel))
        {
            _pending.Remove(option.Key);
            Set(option.Key, pending);
        }
    }

    /// <summary>
    /// The control for <paramref name="value"/>, with the setting's summary as its tooltip: the value it was changed
    /// to, or null when unchanged.
    /// </summary>
    private object? Edit(Setting option, Row row, object? value)
    {
        switch (value)
        {
            case bool isOn:
                bool changed = ui.Checkbox(row.Label, ref isOn);
                ShowSummary(option);

                return changed ? isOn : null;

            case Enum:
                int index = Array.IndexOf(row.Values, value);
                bool chosen = ui.Choice(row.Label, ref index, row.Names);
                ShowSummary(option);

                return chosen ? row.Values.GetValue(index) : null;

            case not null when IsNumber(option.Type):
                float number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                bool dragged = ui.Slider(row.Label, ref number);
                ShowSummary(option);

                return dragged ? ToNumber(number, option.Type) : null;

            case not null when row.Parts.Length > 0:
                // The value is the setting's own boxed copy, so its parts are set in place.
                bool isPartChanged = false;
                foreach (Part part in row.Parts)
                {
                    float partNumber = Convert.ToSingle(part.Get(value), CultureInfo.InvariantCulture);
                    if (ui.Slider(part.Label, ref partNumber))
                    {
                        part.Set(value, ToNumber(partNumber, part.Type));
                        isPartChanged = true;
                    }

                    ShowSummary(option);
                }

                return isPartChanged ? value : null;

            default:
                ui.Label(row.ReadOnlyLabel);
                ShowSummary(option);

                return null;
        }
    }

    private void ShowSummary(Setting option)
    {
        if (option.Summary.Length > 0)
            ui.Tooltip(option.Summary);
    }

    private void Set(string key, object value)
    {
        Result set = settings.Set(key, value);
        if (!set.Ok)
            Log.Error($"{key} was not set: {set.Error}");
    }

    private async Task SaveAsync()
    {
        Result saved = await settings.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        if (saved.Ok)
            Log.Info("Saved the settings.");
        else
            Log.Error($"The settings were not saved: {saved.Error}");
    }

    /// <summary>
    /// A number of a struct setting, as a size's width: its label, its type, and how it is read and set on the boxed
    /// value.
    /// </summary>
    private sealed record Part(string Label, Type Type, Func<object, object?> Get, Action<object, object?> Set);

    /// <summary>
    /// What a setting's row draws with: its control's label, an enum's names and values, a struct's parts, its apply
    /// button's label, and the line of a type with no control.
    /// </summary>
    private sealed record Row(
        string Label,
        string[] Names,
        Array Values,
        Part[] Parts,
        string ApplyLabel,
        string ReadOnlyLabel
    );
}
