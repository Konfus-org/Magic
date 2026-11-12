namespace Magic.Configuration;

/// <summary>
/// A gem with settings that it keeps and that are written whole: the older way, beside <see cref="ISettings"/>, until
/// every gem has moved. The settings are a <c>sealed record</c> of init properties with a
/// <see cref="System.ComponentModel.CategoryAttribute"/>, each property with its default and a doc comment in plain
/// text, which is what <see cref="Settings"/> lists it with. A property may carry a category of its own, and
/// <see cref="RequireRestartAttribute"/> when the gem reads it only as it is made. The gem takes
/// <typeparamref name="TSettings"/> in its constructor, the defaults with what was changed in earlier runs, and keeps it
/// as <see cref="Settings"/>.
/// </summary>
public interface IHasSettings<TSettings>
    where TSettings : class
{
    /// <summary>
    /// The settings the gem runs by: what its constructor was given until <see cref="Configuration.Settings"/> sets
    /// newer. Game thread.
    /// </summary>
    TSettings Settings { get; set; }
}
