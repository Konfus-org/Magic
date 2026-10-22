using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// The meshes imported from one file: what a <see cref="Renderer"/> draws, each mesh with the material in the slot it
/// names. They are in the engine's frame (+X right, +Y up, +Z forward, a metre a unit, triangles clockwise from
/// outside), with the point its <c>.meta</c> names as its <c>origin</c> moved to (0, 0, 0), so scale and rotation turn
/// about it. Spawned (<see cref="World.Spawn"/>), it is one entity that draws it, with the default materials.
/// </summary>
public sealed class Model : IAsset<Model>, ISpawnable
{
    public static IReadOnlyList<string> Extensions { get; } = [".fbx", ".gltf", ".glb", ".obj"];

    public Handle<Model> Handle { get; init; }

    public required IReadOnlyList<Mesh> Meshes { get; init; }

    /// <summary>
    /// Its triangles in clusters at every level, finest to coarsest, which a renderer cuts through per cluster as it
    /// draws, so detail follows distance across the model. A gem makes them as it loads
    /// (<see cref="ILoadPostProcessor{T}"/>); null when none does, and a renderer then draws its meshes as they are.
    /// </summary>
    public ClusterHierarchy? Clusters { get; init; }

    /// <summary>
    /// Its cards, one for each mesh (<see cref="Mesh.Card"/>), drawn in place of its triangles from their
    /// <see cref="ModelCards.Distance"/> on, where it is too small on screen for triangles to show more; null when it
    /// has none, a model too flat for one, and then its coarsest triangles are drawn however far away it is.
    /// </summary>
    public ModelCards? Cards { get; init; }

    /// <summary>
    /// Makes one entity at <paramref name="at"/> that draws it.
    /// </summary>
    public Entity[] Spawn(World world, Transform at) => [world.Create([at, new Renderer { Model = Handle }])];
}

/// <summary>
/// A <see cref="Model"/>'s cards: a card for each of its meshes, mesh for mesh (<see cref="Mesh.Card"/>), drawn in
/// place of its triangles from <see cref="Distance"/> on.
/// </summary>
public sealed class ModelCards
{
    /// <summary>
    /// Metres from the nearest camera from which it is drawn, for an entity at unit scale: twice as far for one twice
    /// the size.
    /// </summary>
    public required float Distance { get; init; }

    public required IReadOnlyList<Mesh> Meshes { get; init; }
}

/// <summary>
/// One part of a <see cref="Model"/>: triangles drawn with one material.
/// </summary>
public sealed class Mesh
{
    public required ReadOnlyMemory<Vertex> Vertices { get; init; }

    /// <summary>
    /// Three per triangle, into <see cref="Vertices"/>.
    /// </summary>
    public required ReadOnlyMemory<uint> Indices { get; init; }

    /// <summary>
    /// Which of a <see cref="Renderer"/>'s <see cref="MaterialSlots"/> it is drawn with, 0 to 7.
    /// </summary>
    public int MaterialSlot { get; init; }

    /// <summary>
    /// What it shows when it is a card, a quad facing the camera in place of a mesh far away; null for a mesh of
    /// triangles.
    /// </summary>
    public Card? Card { get; init; }
}
