using Magic.Ecs.Components;

namespace Magic.GIRendering;

/// <param name="Transform">Where the camera is and which way it looks.</param>
/// <param name="FieldOfView">Vertical, in degrees.</param>
/// <param name="Near">Metres to the nearest the camera sees.</param>
internal readonly record struct RenderCamera(Transform Transform, float FieldOfView, float Near);
