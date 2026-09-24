namespace Magic.Contexts.Components;

public enum Projection : byte
{
    Perspective,
    Orthographic
}

/// <summary>
/// The part of the render target a camera draws into, so split screen is two cameras with two regions and
/// no pixel arithmetic in a scene. <see cref="Mathematics.CameraMath"/> turns one into pixels.
/// </summary>
public enum ViewportRegion : byte
{
    Full,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    TopHalf,
    BottomHalf,
    LeftHalf,
    RightHalf
}

/// <summary>
/// What a camera draws into. Id 0 (<see cref="MainWindow"/>) is the main window; other values are window
/// handles (<see cref="Interfaces.IWindow.Handle"/>), or offscreen targets.
/// </summary>
public readonly record struct RenderTarget(ulong Id)
{
    public static readonly RenderTarget MainWindow = default;
}

/// <summary>
/// A camera looking down its entity's +Z. The component holds only what a scene decides; the matrices,
/// frustum and screen mappings come from <see cref="Mathematics.CameraMath"/>, so the renderer and gameplay
/// code (picking, screen-space UI) compute them the same way. Every camera entity is drawn, each into its
/// <see cref="Target"/> and <see cref="Viewport"/>.
/// </summary>
public struct Camera
{
    public Projection Projection { get; set; }

    /// <summary>Vertical field of view in degrees; for an orthographic camera, the vertical size in metres.</summary>
    public float FieldOfView { get; set; }

    public float Near { get; set; }

    /// <summary>
    /// Perspective: <see cref="float.PositiveInfinity"/> means an infinite reverse-Z projection, which is
    /// the default because nothing is then ever clipped for being distant. Orthographic: always finite.
    /// </summary>
    public float Far { get; set; }

    public ViewportRegion Viewport { get; set; }
    public RenderTarget Target { get; set; }

    public static readonly Camera Default = new()
    {
        Projection = Projection.Perspective,
        FieldOfView = 60f,
        Near = 0.05f,
        Far = float.PositiveInfinity,
    };

    public static Camera Perspective(float fovDegrees, float near, float far = float.PositiveInfinity)
    {
        return new() { Projection = Projection.Perspective, FieldOfView = fovDegrees, Near = near, Far = far };
    }

    /// <summary><paramref name="size"/> is the vertical extent in metres; the width follows the viewport's aspect.</summary>
    public static Camera Orthographic(float size, float near, float far)
    {
        return new() { Projection = Projection.Orthographic, FieldOfView = size, Near = near, Far = far };
    }
}
