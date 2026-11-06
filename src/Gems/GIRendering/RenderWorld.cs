using System.Numerics;
using Magic.Ecs.Components;
using Magic.Ecs.Components.Effects;

namespace Magic.GIRendering;

/// <summary>
/// What the frame draws beside the renderers, read out of the entities in <c>Update</c> so <c>Render</c> never touches
/// the ECS: the camera that draws into the window, the sun and the lights. Filled again each frame, into the same lists.
/// </summary>
internal sealed class RenderWorld
{
    public List<GpuLight> Lights { get; } = [];

    public RenderCamera? Camera { get; set; }

    public RenderSun? Sun { get; set; }

    public Fog? Fog { get; set; }

    public bool HasSky { get; set; }

    /// <summary>
    /// What is done to the picture after the lighting: the first entity's that has a <see cref="PostProcessing"/>, else
    /// the defaults.
    /// </summary>
    public PostProcessing Post { get; set; }

    public void Clear()
    {
        Lights.Clear();
        Camera = null;
        Sun = null;
        Fog = null;
        HasSky = false;
        Post = new PostProcessing();
    }
}

/// <param name="Transform">Where the camera is and which way it looks.</param>
/// <param name="FieldOfView">Vertical, in degrees.</param>
/// <param name="Near">Metres to the nearest the camera sees.</param>
internal readonly record struct RenderCamera(Transform Transform, float FieldOfView, float Near);

/// <param name="Direction">Towards the sun, unit.</param>
/// <param name="Radiance">The sun's colour times its intensity, in lux, above the air.</param>
/// <param name="Exposure">What the tonemap scales the picture by, so the sun's light lands near 1.</param>
internal readonly record struct RenderSun(Vector3 Direction, Vector3 Radiance, float Exposure);
