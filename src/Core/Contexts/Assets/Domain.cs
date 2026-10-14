using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// A piece of the world that opens as one (<see cref="World.OpenAsync"/>): a <c>.domain</c> file in a folder of its
/// own, beside the prefabs it spawns, every one in the folder: the cells, named <c>x_y_z.prefab</c>, each at its
/// corner, and any other, such as <c>globals.prefab</c>, at the domain's origin. The file is this class as JSON:
/// <c>{ "cellSize": 64 }</c>.
/// </summary>
public sealed class Domain : IJsonAsset<Domain>
{
    public static IReadOnlyList<string> Extensions { get; } = [".domain"];

    public Handle<Domain> Handle { get; init; }

    /// <summary>
    /// Metres a cell covers on each axis: cell <c>x_y_z</c> spans <c>[x * size, (x + 1) * size)</c>, and the positions
    /// in it are relative to its corner, <c>(x, y, z) * size</c>.
    /// </summary>
    public float CellSize { get; set; } = 64;
}
