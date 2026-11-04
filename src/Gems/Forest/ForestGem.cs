using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Gems;
using Magic.Pipelines;
using Magic.Worlds;

namespace Forest;

/// <summary>
/// Plants the forest as the Forest domain loads: a hundred thousand trees, each a crown and a trunk, scattered over a
/// grid two kilometres across, every one the domain's, so they go when it does. The sample's proof that a world
/// draws to the horizon.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Magic makes it by reflection when a project lists the gem."
)]
internal sealed class ForestGem(IEcs ecs, AssetDatabase assets) : IGem, IPipeline<WorldLoad>
{
    private const string DomainPath = "Domains/Forest/Forest.domain";
    private const int TreesPerSide = 316;
    private const float Spacing = 6.3f;
    private const float Jitter = 2.5f;

    public ValueTask<WorldLoad> RunAsync(WorldLoad state, CancellationToken cancel)
    {
        Handle<Domain> forest = assets.Find<Domain>(DomainPath);
        if (forest == Handle<Domain>.None || state.Content.Domain != forest)
            return ValueTask.FromResult(state);

        Handle<Model> crown = assets.Find<Model>("Models/Cone.fbx");
        Handle<Model> trunk = assets.Find<Model>("Models/Cylinder.fbx");
        Handle<Material> leaves = assets.Find<Material>("Materials/Leaves.mat");
        Handle<Material> bark = assets.Find<Material>("Materials/Bark.mat");
        if (crown == Handle<Model>.None || trunk == Handle<Model>.None)
        {
            Log.Error("The forest has no trees: the cone and cylinder models are missing.");

            return ValueTask.FromResult(state);
        }

        int count = TreesPerSide * TreesPerSide;
        Transform[] transforms = new Transform[count * 2];
        Renderer[] renderers = new Renderer[count * 2];
        float half = TreesPerSide * Spacing / 2;
        for (int i = 0; i < count; i++)
        {
            uint tree = (uint)i;
            float column = i % TreesPerSide * Spacing;
            float row = i / TreesPerSide * Spacing;
            float x = column - half + (Scatter(tree, 0) * Jitter);
            float z = row - half + (Scatter(tree, 1) * Jitter);
            float height = 4.5f + (Scatter(tree, 2) * 1.5f);
            float turn = Scatter(tree, 3) * MathF.PI;
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);
            transforms[i * 2] = new Transform
            {
                Position = new Vector3(x, height * 0.45f, z),
                Rotation = rotation,
                Scale = new Vector3(height * 0.35f, height, height * 0.35f),
            };
            renderers[i * 2] = new Renderer { Model = crown, Materials = Slots(leaves) };
            transforms[(i * 2) + 1] = new Transform
            {
                Position = new Vector3(x, height * 0.2f, z),
                Rotation = rotation,
                Scale = new Vector3(height * 0.06f, height * 0.4f, height * 0.06f),
            };
            renderers[(i * 2) + 1] = new Renderer { Model = trunk, Materials = Slots(bark) };
        }

        Entity[] created = new Entity[count * 2];
        ecs.Create(transforms, renderers, created);
        Origin origin = new() { Domain = forest, Content = state.Content.Id };
        foreach (Entity entity in created)
        {
            ecs.Set(entity, in origin);
        }

        Log.Info($"The forest grew: {count} trees.");

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// A number in [-1, 1] that is always the same for a tree and a purpose, from a hash of both, so the forest is the
    /// same forest every run.
    /// </summary>
    private static float Scatter(uint tree, uint purpose)
    {
        uint hash = unchecked((tree * 0x9E3779B1u) ^ (purpose * 0x85EBCA77u));
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x2C1B3C6Du);
        hash ^= hash >> 12;

        return ((hash & 0xFFFFFFu) / 8388607.5f) - 1;
    }

    private static MaterialSlots Slots(Handle<Material> material)
    {
        MaterialSlots slots = default;
        slots.Add(material);

        return slots;
    }
}
