using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// A perspective camera, looking along its entity's +Z, drawing into its <see cref="Target"/>. A zero reads as its
/// default, so <c>new Camera()</c>, or <c>{}</c> in a file, has a 60 degree field of view, sees from 5 cm away, sees
/// without end, and draws into the whole window.
/// </summary>
public record struct Camera : IComponent
{
    public CameraTarget Target;

    /// <summary>
    /// The vertical field of view, in degrees.
    /// </summary>
    public float FieldOfView
    {
        readonly get => field == 0 ? 60 : field;
        set;
    }

    /// <summary>
    /// Metres from the camera to the nearest it sees.
    /// </summary>
    public float Near
    {
        readonly get => field == 0 ? 0.05f : field;
        set;
    }

    /// <summary>
    /// Metres from the camera to the farthest it sees: by default no limit, so nothing is cut off for being far away.
    /// </summary>
    public float Far
    {
        readonly get => field == 0 ? float.PositiveInfinity : field;
        set;
    }
}

/// <summary>
/// What a <see cref="Camera"/> draws into: the window, in one <see cref="Section"/> of it, or a render texture. In a
/// file: <c>{ "section": "top_left" }</c>, or <c>{ "texture": { "id": 9101 } }</c>. Whatever draws the camera turns
/// it into a render target; the GPU never sees it.
/// </summary>
public record struct CameraTarget
{
    /// <summary>
    /// A render texture to draw into, whole; none draws into the window.
    /// </summary>
    public Handle<RenderTexture> Texture;

    /// <summary>
    /// The part of the window drawn into, so split screen is cameras with sections; ignored for a render texture.
    /// </summary>
    public ScreenSection Section;

    public readonly bool IsWindow => Texture == Handle<RenderTexture>.None;
}

/// <summary>
/// A part of the window a camera draws into: all of it, a half, or a quarter.
/// </summary>
public enum ScreenSection : byte
{
    Full,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Top,
    Bottom,
    Left,
    Right,
}
