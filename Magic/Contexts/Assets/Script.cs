using Magic.Interfaces;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A script file as an asset. The project's own build compiles it; loading it only finds the class it declares, an
/// <see cref="IScript"/> named like the file, which a scripting gem does. Entities name it in a chunk by id.
/// </summary>
public sealed class Script : Asset
{
    /// <summary>The script's class, of the project assembly loaded now; a reloaded project has a new one.</summary>
    [JsonIgnore]
    public Type? Type { get; set; }
}
