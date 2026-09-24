namespace Magic.Contexts.Assets;

/// <summary>
/// What a <see cref="Components.Renderer"/> draws: the meshes of one source file and, per part, which of
/// the renderer's material slots it is drawn with. Meshes belong to the model instead of being assets of
/// their own because one file imports as one unit and a mesh is never referenced without its model.
/// </summary>
public sealed class Model : Asset
{
    public Mesh[] Meshes { get; set; } = [];

    public ModelPart[] Parts { get; set; } = [];

    /// <summary>
    /// Material name per slot, as the source file called them, so a scene can bind slots by name.
    /// </summary>
    public string[] SlotNames { get; set; } = [];
}

/// <summary>
/// One drawn mesh of a model: an index into <see cref="Model.Meshes"/> and the material slot it uses.
/// </summary>
public readonly record struct ModelPart(int MeshIndex, int MaterialSlot);
