using System.Numerics;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Ecs.Components.Effects;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.GIRendering;

/// <summary>
/// What the frame draws, read out of the entities in <c>Update</c> so <c>Render</c> never touches the ECS: every
/// renderer with its transform, the camera that draws into the window, and the first directional light. Filled again
/// each frame, into the same lists.
/// </summary>
internal sealed class RenderWorld
{
    public List<RenderInstance> Instances { get; } = [];

    public List<GpuLight> Lights { get; } = [];

    public RenderCamera? Camera { get; set; }

    public RenderSun? Sun { get; set; }

    public Fog? Fog { get; set; }

    /// <summary>
    /// What the camera does to its picture after the lighting; the defaults when it says nothing.
    /// </summary>
    public PostProcessing Post { get; set; }

    public void Clear()
    {
        Instances.Clear();
        Lights.Clear();
        Camera = null;
        Sun = null;
        Fog = null;
        Post = new PostProcessing();
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
/// <param name="Radiance">The sun's colour times its intensity, in lux, above the air.</param>
/// <param name="Exposure">What the tonemap scales the picture by, so the sun's light lands near 1.</param>
internal readonly record struct RenderSun(Vector3 Direction, Vector3 Radiance, float Exposure);
