using Magic.Contexts.Components;
using Magic.Extensions;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

// Everything the GPU reads by layout, mirroring Resources/Shaders/Include/Structs.hlsli and Frame.hlsli
// field for field. Every row is 16 bytes there; here the vector fields and scalar runs keep the same
// layout, and each struct carries its byte size as a constant that AssertLayout checks once.
// Do not reorder, pack or resize a field here without making the same change in the HLSL: the shaders read
// these bytes blind, so a mismatch compiles fine and draws garbage.

/// <summary>
/// 32 B. One drawn instance: world-space bounds, the radius it is size-culled by (that of its bounds unless its
/// renderer says otherwise), its material, its flags and where its draw goes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuInstance
{
    public const int Size = 32;

    public Vector4 Sphere;
    public float CullRadius;
    public uint MaterialSlot;
    public uint BucketGroup;
    public InstanceFlags Flags;
}

/// <summary>
/// 48 B. The three rows of transpose(world): row i holds column i of the row-vector matrix.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuInstanceXform
{
    public const int Size = 48;

    public Vector4 R0, R1, R2;

    public static GpuInstanceXform From(in Matrix4x4 world)
    {
        return new GpuInstanceXform
        {
            R0 = new Vector4(world.M11, world.M21, world.M31, world.M41),
            R1 = new Vector4(world.M12, world.M22, world.M32, world.M42),
            R2 = new Vector4(world.M13, world.M23, world.M33, world.M43),
        };
    }
}

/// <summary>
/// 128 B. A material's parameters, packed by the surface's parameter layout; only the generated loader knows the layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GpuMaterial
{
    public const int Size = 128;

    public fixed uint W[32];
}

/// <summary>
/// 16 B. A run of instance slots belonging to one cell.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPage
{
    public const int Size = 16;

    public uint FirstInstance, Count, Cell, Pad;
}

/// <summary>
/// 32 B. A residency cell's world-space bounds.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCell
{
    public const int Size = 32;

    public Vector4 AabbMin, AabbMax;
}

/// <summary>
/// 8 B. One entry of a view's visible list, written by the cull shaders and read by the vertex stage: the instance's
/// slot, and how much of it is drawn while it blends between two versions of its mesh (1 is all of it; see
/// Structs.hlsli).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuVisible
{
    public const int Size = 8;

    public uint Slot;
    public float LodFade;
}

/// <summary>
/// 20 B. An indexed indirect draw command, one per bucket group. The field order is the graphics API's
/// (<c>SDL_GPUIndexedIndirectDrawCommand</c>, Vulkan's and D3D12's): the cull shaders write it and the GPU reads it as
/// the draw. Never reorder it; and like every struct in this file, never change it without the HLSL twin in
/// Structs.hlsli: the layouts must match byte for byte, and nothing checks that at build time.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DrawArgs
{
    public const int Size = 20;

    public uint IndexCount, InstanceCount, FirstIndex;
    public int VertexOffset;
    public uint FirstInstance;
}

/// <summary>
/// 32 B. The lesser versions of one bucket group's mesh, one row per group: the culler draws an instance of the group
/// in <see cref="Group1"/> once it is under <c>Thresholds.X</c> of the view's height on screen, in
/// <see cref="Group2"/> under <c>Thresholds.Y</c>, in <see cref="Group3"/> under <c>Thresholds.Z</c>.
/// <see cref="Count"/> is how many of the three there are; 0 is a group whose mesh has none.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLodRow
{
    public const int Size = 32;

    public const int Capacity = 3;

    public Vector4 Thresholds;
    public uint Group1, Group2, Group3, Count;
}

/// <summary>
/// GpuInstance.Flags, as Structs.hlsli names them: only what a shader reads.
/// </summary>
[Flags]
internal enum InstanceFlags : uint
{
    None = 0,
    Alive = 1u << 0,
    Mirrored = 1u << 1,
    NoSizeCull = 1u << 2,
    Hidden = 1u << 3,
}

/// <summary>
/// The per-view constants, 256 bytes, exactly as <c>Include/Frame.hlsli</c> declares them. One block per stage: DXC
/// drops a cbuffer nothing reads and SDL wants the used uniform bindings consecutive from 0, so everything a stage
/// needs per frame lives together, lighting included.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public const int Size = 256;

    /// <summary>
    /// Camera-relative view times reverse-Z projection.
    /// </summary>
    public Matrix4x4 ViewProj;

    /// <summary>
    /// Rotation only.
    /// </summary>
    public Matrix4x4 View;

    public Vector3 CameraPos;

    /// <summary>
    /// Seconds since the renderer was built.
    /// </summary>
    public float Time;

    /// <summary>
    /// The view's rectangle in pixels, and one over it.
    /// </summary>
    public Vector2 ViewSize, ViewTexel;

    /// <summary>
    /// Where the view's rectangle sits in its render target, in pixels.
    /// </summary>
    public Vector2 ViewOrigin;

    /// <summary>
    /// The projection's diagonal: P11, P22.
    /// </summary>
    public Vector2 ProjScale;

    public float Near;

    /// <summary>
    /// An instance whose bounds project to a smaller radius is culled.
    /// </summary>
    public float MinPixels;

    /// <summary>
    /// 1 = orthographic: the culler lets everything through.
    /// </summary>
    public uint IsOrthographic;

    /// <summary>
    /// The view's depth pyramid: its level count, the size of level 0, and the first level the build pass being dispatched writes.
    /// </summary>
    public uint HiZLevelCount, HiZWidth, HiZHeight, HiZFirstLevel;

    /// <summary>
    /// Scales an instance's height on screen before it is held against its LOD thresholds: <see cref="Magic.Contexts.Settings.RenderSettings.LodBias"/>.
    /// </summary>
    public float LodBias;

    /// <summary>
    /// The direction the sun's light travels.
    /// </summary>
    public Vector3 SunDirection;

    /// <summary>
    /// The far plane's distance; infinite for a perspective view that has none.
    /// </summary>
    public float Far;

    public Vector3 SunColor;

    public float SunColorPad;

    public Vector3 Ambient;

    /// <summary>
    /// How many <see cref="GpuLight"/> rows the lights buffer holds this frame.
    /// </summary>
    public uint LightCount;

    /// <summary>
    /// Camera-relative view times projection for a view drawn into <paramref name="rect"/>, with its depth pyramid's size.
    /// </summary>
    public static FrameConstants Build(
        in View view,
        Rectangle rect,
        (int Width, int Height, int Levels) hiZ,
        float time,
        float minPixels,
        float lodBias,
        in LightingConstants lighting)
    {
        float aspect = rect.Height > 0 ? (float)rect.Width / rect.Height : 1f;
        Matrix4x4 rotation = Camera.ViewMatrix(view.World);
        Matrix4x4 projection = view.Camera.ProjectionMatrix(aspect);

        return new FrameConstants
        {
            ViewProj = rotation * projection,
            View = rotation,
            CameraPos = view.World.Translation,
            Time = time,
            ViewSize = new Vector2(rect.Width, rect.Height),
            ViewTexel = new Vector2(1f / Math.Max(1, rect.Width), 1f / Math.Max(1, rect.Height)),
            ViewOrigin = new Vector2(rect.X, rect.Y),
            ProjScale = new Vector2(projection.M11, projection.M22),
            Near = view.Camera.Near,
            MinPixels = minPixels,
            IsOrthographic = view.Camera.Projection == Projection.Orthographic ? 1u : 0u,
            HiZLevelCount = (uint)hiZ.Levels,
            HiZWidth = (uint)hiZ.Width,
            HiZHeight = (uint)hiZ.Height,
            LodBias = lodBias,
            SunDirection = lighting.SunDirection,
            Far = view.Camera.Far,
            SunColor = lighting.SunColor,
            Ambient = lighting.Ambient,
            LightCount = lighting.LightCount,
        };
    }
}

/// <summary>
/// A data pass's constants: the frame block followed by its packed parameters (<c>Include/Pass.hlsli</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PassConstants
{
    public const int Size = FrameConstants.Size + GpuMaterial.Size;

    public FrameConstants Frame;
    public fixed uint Raw[GpuMaterial.Size / 4];
}

/// <summary>
/// 48 B. One point or spot light, in absolute world space. A point light is a spot whose cone never ends: its cosines
/// are below any a direction can have.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    public const int Size = 48;

    /// <summary>
    /// xyz the position, w the range in metres.
    /// </summary>
    public Vector4 PositionRange;

    /// <summary>
    /// rgb the linear colour times intensity, w the cosine of half the inner cone angle.
    /// </summary>
    public Vector4 ColorInnerCos;

    /// <summary>
    /// xyz the direction the light travels, w the cosine of half the outer cone angle.
    /// </summary>
    public Vector4 DirectionOuterCos;

    public static GpuLight From(in LightInstance light)
    {
        bool isSpot = light.Kind == LightKind.Spot;
        return new GpuLight
        {
            PositionRange = new Vector4(light.World.Translation, light.Range),
            ColorInnerCos = new Vector4(light.Color * light.Intensity, isSpot ? MathF.Cos(light.InnerAngle * 0.5f) : -1f),
            DirectionOuterCos = new Vector4(Vector3.Normalize(light.World.Forward), isSpot ? MathF.Cos(light.OuterAngle * 0.5f) : -2f),
        };
    }
}

/// <summary>
/// 32 B. One glow: an unlit bright dot, in absolute world space.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuGlow
{
    public const int Size = 32;

    /// <summary>
    /// xyz the position, w the radius in metres.
    /// </summary>
    public Vector4 PositionRadius;

    /// <summary>
    /// rgb linear, emitted as it is; a unused.
    /// </summary>
    public Vector4 Color;
}

/// <summary>
/// The lights the frame constants carry: the sun (the first directional light), a flat ambient, and how many point and
/// spot lights there are in the lights buffer.
/// </summary>
internal struct LightingConstants
{
    public Vector3 SunDirection;
    public Vector3 SunColor;
    public Vector3 Ambient;
    public uint LightCount;

    public static LightingConstants From(ReadOnlySpan<LightInstance> lights)
    {
        LightingConstants constants = new()
        {
            SunDirection = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f)),
            SunColor = Vector3.Zero,
            Ambient = new Vector3(0.15f, 0.16f, 0.2f),
        };

        bool hasSun = false;
        foreach (LightInstance light in lights)
        {
            if (light.Kind != LightKind.Directional)
            {
                constants.LightCount++;
                continue;
            }

            if (hasSun)
                continue;

            constants.SunDirection = Vector3.Normalize(light.World.Forward);
            constants.SunColor = light.Color * light.Intensity;
            hasSun = true;
        }

        return constants;
    }
}

internal static class GpuStructs
{
    public static void AssertLayout()
    {
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuInstance>() == GpuInstance.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuInstanceXform>() == GpuInstanceXform.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuMaterial>() == GpuMaterial.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuPage>() == GpuPage.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuCell>() == GpuCell.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuVisible>() == GpuVisible.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<DrawArgs>() == DrawArgs.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuLodRow>() == GpuLodRow.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuLight>() == GpuLight.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuGlow>() == GpuGlow.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<FrameConstants>() == FrameConstants.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<PassConstants>() == PassConstants.Size);
    }
}
