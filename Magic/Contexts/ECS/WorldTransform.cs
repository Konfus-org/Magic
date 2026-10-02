using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>
/// The entity's <see cref="Transform"/> composed with its parents', in world space. Written by the host's
/// transform system every frame after LateUpdate (once, for entities tagged
/// <see cref="Tag.Static"/>) and added to any entity that has a <see cref="Transform"/>; read by everything
/// that needs a world matrix, the renderer first.
/// </summary>
public struct WorldTransform
{
    public Matrix4x4 Value;
}
