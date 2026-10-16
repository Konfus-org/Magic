namespace Magic.Contexts.Settings;

/// <summary>
/// One gem setting as it is now, as <see cref="Services.Settings.All"/> lists them for a settings UI: what to call it,
/// what it is, and the value to show. It is changed with <see cref="Services.Settings.Set{T}"/> by its
/// <see cref="Key"/>.
/// </summary>
/// <param name="Name">The setting's name in its gem's settings, as a preset writes it: <c>renderScale</c>.</param>
/// <param name="Description">What it is, as its doc comment says; empty when the gem's documentation file is not
/// there.</param>
/// <param name="Owner">The gem whose it is, as the project lists it.</param>
/// <param name="Type">What it holds: a flag, a number, an enum, a string, or anything a preset can write as JSON.</param>
/// <param name="Value">What it holds now, what was just set included, as a <see cref="Type"/>.</param>
/// <param name="Attributes">The attributes on it, its own and then its class's: <see cref="RequireRestartAttribute"/>
/// marks one its gem reads only as it starts, so changing it starts the engine again;
/// <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/> bounds a number; and
/// <see cref="System.ComponentModel.CategoryAttribute"/> groups it with other gems' settings of its kind, a tab and,
/// past a slash, a header in it (<c>Render/Shadows</c>); a UI lists one without under its gem.</param>
public sealed record Setting(string Name, string Description, string Owner, Type Type, object? Value, IReadOnlyList<Attribute> Attributes)
{
    /// <summary>
    /// The key that names it, <c>Owner.name</c>: what <see cref="Services.Settings.Get{T}"/>,
    /// <see cref="Services.Settings.Set{T}"/>, <c>--set</c> and a preset take.
    /// </summary>
    public string Key => $"{Owner}.{Name}";
}
