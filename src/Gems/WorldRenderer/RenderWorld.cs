using System.Numerics;
using Magic.Assets;
using Magic.Assets.Types;
using Magic.Ecs;
using Magic.Ecs.Components;

namespace WorldRenderer;

/// <summary>
/// What the frame draws, read out of the entities in <c>Update</c> so <c>Render</c> never touches the ECS: every
/// renderer with its transform, the camera that draws into the window, and the first directional light. Filled again
/// each frame, into the same lists.
/// </summary>
internal sealed class RenderWorld
{
    public List<RenderInstance> Instances { get; } = [];

    public RenderCamera? Camera { get; set; }

    public RenderSun? Sun { get; set; }

    public void Clear()
    {
        Instances.Clear();
        Camera = null;
        Sun = null;
    }
}

internal readonly record struct RenderInstance(
    Entity Entity,
    Handle<Model> Model,
    MaterialSlots Materials,
    Transform Transform
);

/// <param name="Transform">Where the camera is and which way it looks.</param>
/// <param name="FieldOfView">Vertical, in degrees.</param>
/// <param name="Near">Metres to the nearest the camera sees.</param>
internal readonly record struct RenderCamera(Transform Transform, float FieldOfView, float Near);

/// <param name="Direction">Towards the sun, unit.</param>
/// <param name="Radiance">The sun's colour at the output's exposure.</param>
internal readonly record struct RenderSun(Vector3 Direction, Vector3 Radiance);
