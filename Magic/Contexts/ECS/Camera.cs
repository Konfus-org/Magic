using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Mathematics;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Components;

public enum Projection : byte
{
    Perspective,
    Orthographic
}

/// <summary>
/// The part of the render target a camera draws into, so split screen is two cameras with two regions and
/// no pixel arithmetic in a scene. <see cref="ViewportRegionExtensions.ToPixels"/> turns one into pixels.
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
/// What a camera draws into: the <see cref="RenderTexture"/> <see cref="Texture"/> when it is set, else the window
/// whose <see cref="Interfaces.IWindow.Handle"/> is <see cref="Window"/> (0, <see cref="MainWindow"/>, is the main
/// window). In a chunk: <c>{ "texture": { "id": 9101 } }</c> or <c>{ "window": 2 }</c>.
/// </summary>
public readonly record struct RenderTarget(uint Window, Handle<RenderTexture> Texture)
{
    public static readonly RenderTarget MainWindow = default;

    [JsonIgnore]
    public bool IsTexture => Texture.IsValid;

    public static RenderTarget Of(uint window)
    {
        return new RenderTarget(window, default);
    }

    public static RenderTarget Of(Handle<RenderTexture> texture)
    {
        return new RenderTarget(0, texture);
    }

    public override string ToString()
    {
        return IsTexture ? $"render texture {Texture.Id}" : Window == 0 ? "the main window" : $"window {Window}";
    }
}

/// <summary>
/// A camera looking down its entity's +Z. The component holds only what a scene decides; the matrices,
/// frustum and rays come from its methods, given the entity's world matrix, so the renderer and gameplay
/// code (picking, screen-space UI) compute them the same way. The view matrix is rotation only: rendering is
/// camera relative (positions are taken relative to the camera before it is applied), which keeps float
/// precision flat however far from the origin the camera is. Every camera entity is drawn, each into its
/// <see cref="Target"/> and <see cref="Viewport"/>.
/// <para>
/// A zero field reads as its default: field of view 60 degrees, near 0.05 m, far infinite (1000 m when
/// orthographic). So a camera written as <c>{}</c> in a chunk, or added uninitialised, is <see cref="Default"/>.
/// </para>
/// </summary>
public struct Camera : IComponent, IEquatable<Camera>
{
    public static readonly Camera Default = default;

    private float _fieldOfView, _near, _far;

    public Projection Projection { get; set; }

    /// <summary>Vertical field of view in degrees; for an orthographic camera, the vertical size in metres.</summary>
    public float FieldOfView
    {
        readonly get => _fieldOfView == 0f ? 60f : _fieldOfView;
        set => _fieldOfView = value;
    }

    public float Near
    {
        readonly get => _near == 0f ? 0.05f : _near;
        set => _near = value;
    }

    /// <summary>
    /// Perspective: <see cref="float.PositiveInfinity"/> means an infinite reverse-Z projection, which is
    /// the default because nothing is then ever clipped for being distant. Orthographic: always finite.
    /// </summary>
    public float Far
    {
        readonly get => _far != 0f ? _far : Projection == Projection.Orthographic ? 1000f : float.PositiveInfinity;
        set => _far = value;
    }

    public ViewportRegion Viewport { get; set; }

    public RenderTarget Target { get; set; }

    public static Camera Perspective(float fovDegrees, float near, float far = float.PositiveInfinity)
    {
        return new() { Projection = Projection.Perspective, FieldOfView = fovDegrees, Near = near, Far = far };
    }

    /// <summary><paramref name="size"/> is the vertical extent in metres; the width follows the viewport's aspect.</summary>
    public static Camera Orthographic(float size, float near, float far)
    {
        return new() { Projection = Projection.Orthographic, FieldOfView = size, Near = near, Far = far };
    }

    /// <summary>The rotation-only view matrix for a camera at <paramref name="world"/>: the inverse of its orientation.</summary>
    public static Matrix4x4 ViewMatrix(in Matrix4x4 world)
    {
        Vector3 right = Vector3.Normalize(world.Right);
        Vector3 up = Vector3.Normalize(world.Up);
        Vector3 forward = Vector3.Normalize(world.Forward);

        // The inverse of an orthonormal basis is its transpose: rows become columns.
        return new Matrix4x4(
            right.X, up.X, forward.X, 0f,
            right.Y, up.Y, forward.Y, 0f,
            right.Z, up.Z, forward.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    public readonly Matrix4x4 ProjectionMatrix(float aspect)
    {
        if (Projection == Projection.Orthographic)
            return Matrix4x4.OrthographicReverseZ(FieldOfView * aspect, FieldOfView, Near, Far);

        return Matrix4x4.PerspectiveReverseZ(float.DegreesToRadians(FieldOfView), aspect, Near, Far);
    }

    /// <summary>Camera-relative: <see cref="ViewMatrix"/> times <see cref="ProjectionMatrix"/>.</summary>
    public readonly Matrix4x4 ViewProjection(in Matrix4x4 world, float aspect)
    {
        return ViewMatrix(world) * ProjectionMatrix(aspect);
    }

    /// <summary>The camera-relative view volume: test positions taken relative to <c>world.Translation</c>.</summary>
    public readonly Frustum Frustum(in Matrix4x4 world, float aspect)
    {
        return Mathematics.Frustum.FromViewProjection(ViewProjection(world, aspect));
    }

    /// <summary>
    /// The world-space ray under a point of the viewport, <paramref name="ndc"/> in -1..1 with +Y up
    /// (the GPU's clip convention), for picking.
    /// </summary>
    public readonly Ray Pick(in Matrix4x4 world, float aspect, Vector2 ndc)
    {
        if (Projection == Projection.Orthographic)
        {
            float halfH = FieldOfView * 0.5f;
            Vector3 origin = world.Translation + (world.Right * (ndc.X * halfH * aspect)) + (world.Up * (ndc.Y * halfH));

            return new Ray(origin, Vector3.Normalize(world.Forward));
        }

        float h = MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f);
        Vector3 direction = Vector3.Normalize(world.Forward + (world.Right * (ndc.X * h * aspect)) + (world.Up * (ndc.Y * h)));

        return new Ray(world.Translation, direction);
    }

    /// <summary>By the values the camera reads as, so <c>{}</c> equals <see cref="Default"/> spelled out.</summary>
    public readonly bool Equals(Camera other)
    {
        return Projection == other.Projection && FieldOfView == other.FieldOfView && Near == other.Near && Far == other.Far
            && Viewport == other.Viewport && Target == other.Target;
    }

    public override readonly bool Equals(object? obj) => obj is Camera other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Projection, FieldOfView, Near, Far, Viewport, Target);

    public static bool operator ==(Camera left, Camera right) => left.Equals(right);

    public static bool operator !=(Camera left, Camera right) => !left.Equals(right);
}
