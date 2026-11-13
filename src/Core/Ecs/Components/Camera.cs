using Magic.Assets;

namespace Magic.Ecs.Components;

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

/// <summary>
/// A perspective camera that looks along the +Z of its entity and draws into its <see cref="Target"/>.
/// </summary>
/// <remarks>
/// A new one has a 60 degree field of view, sees from 5 cm away, has no far limit, and draws into the whole window.
/// </remarks>
public record struct Camera : IComponent
{
    public CameraTarget Target;

    /// <summary>
    /// The vertical field of view, in degrees.
    /// </summary>
    public float FieldOfView = 60;

    /// <summary>
    /// Metres from the camera to the nearest it sees.
    /// </summary>
    public float Near = 0.05f;

    /// <summary>
    /// Metres from the camera to the farthest it sees: by default no limit, so nothing is cut off for being far away.
    /// </summary>
    public float Far = float.PositiveInfinity;

    public Camera() { }
}

/// <summary>
/// What a <see cref="Camera"/> draws into: one <see cref="Section"/> of the window, or a render texture.
/// </summary>
/// <remarks>
/// The part that draws the camera changes it into a render target. The GPU never sees it.
/// </remarks>
public record struct CameraTarget
{
    /// <summary>
    /// A render texture to draw into, whole; none draws into the window.
    /// </summary>
    public Handle<RenderTexture> Texture;

    /// <summary>
    /// The part of the window to draw into.
    /// </summary>
    /// <remarks>
    /// Split screen is cameras with different sections. A render texture ignores it.
    /// </remarks>
    public ScreenSection Section;

    public readonly bool IsWindow => Texture == Handle<RenderTexture>.None;
}
