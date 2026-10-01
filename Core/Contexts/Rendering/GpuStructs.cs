using Magic.Contexts.Components;
using Magic.Extensions;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Contexts.Rendering;

// Everything the GPU reads by layout, mirroring Resources/Shaders/Include/Structs.hlsli and Frame.hlsli
// field for field. Every row is 16 bytes there; here the vector fields and scalar runs keep the same
// layout, and each struct carries its byte size as a constant that AssertLayout checks once.
// Do not reorder, pack or resize a field here without making the same change in the HLSL: the shaders read
// these bytes blind, so a mismatch compiles fine and draws garbage.

/// <summary>32 B. One drawn instance: world-space bounds, its material, its flags and where its draw goes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuInstance
{
    public const int Size = 32;

    public Vector4 Sphere;
    public uint MeshSlot;
    public uint MaterialSlot;
    public uint BucketGroup;
    public InstanceFlags Flags;
}

/// <summary>48 B. The three rows of transpose(world): row i holds column i of the row-vector matrix.</summary>
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

/// <summary>128 B. A material's parameters, packed by the surface's parameter layout; only the generated loader knows the layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GpuMaterial
{
    public const int Size = 128;

    public fixed uint W[32];
}

/// <summary>16 B. A run of instance slots belonging to one cell.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPage
{
    public const int Size = 16;

    public uint FirstInstance, Count, Cell, Pad;
}

/// <summary>32 B. A residency cell's world-space bounds.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCell
{
    public const int Size = 32;

    public Vector4 AabbMin, AabbMax;
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

/// <summary>GpuInstance.Flags, as Structs.hlsli names them: only what a shader reads.</summary>
[Flags]
internal enum InstanceFlags : uint
{
    None = 0,
    Alive = 1u << 0,
    Mirrored = 1u << 1,
    NoSizeCull = 1u << 2,
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

    /// <summary>Camera-relative view times reverse-Z projection.</summary>
    public Matrix4x4 ViewProj;

    /// <summary>Rotation only.</summary>
    public Matrix4x4 View;

    public Vector3 CameraPos;

    /// <summary>Seconds since the renderer was built.</summary>
    public float Time;

    /// <summary>The view's rectangle in pixels, and one over it.</summary>
    public Vector2 ViewSize, ViewTexel;

    /// <summary>Where the view's rectangle sits in its render target, in pixels.</summary>
    public Vector2 ViewOrigin;

    /// <summary>The projection's diagonal: P11, P22.</summary>
    public Vector2 ProjScale;

    public float Near;

    /// <summary>An instance whose bounds project to a smaller radius is culled.</summary>
    public float MinPixels;

    /// <summary>1 = orthographic: the culler lets everything through.</summary>
    public uint IsOrthographic;

    /// <summary>The view's depth pyramid: its level count, the size of level 0, and the first level the build pass being dispatched writes.</summary>
    public uint HiZLevelCount, HiZWidth, HiZHeight, HiZFirstLevel;

    public uint Pad;

    public LightingConstants Lighting;

    /// <summary>Camera-relative view times projection for a view drawn into <paramref name="rect"/>, with its depth pyramid's size.</summary>
    public static FrameConstants Build(
        in View view,
        Rectangle rect,
        (int Width, int Height, int Levels) hiZ,
        float time,
        float minPixels,
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
            Lighting = lighting,
        };
    }
}

/// <summary>A data pass's constants: the frame block followed by its packed parameters (<c>Include/Pass.hlsli</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PassConstants
{
    public const int Size = FrameConstants.Size + GpuMaterial.Size;

    public FrameConstants Frame;
    public fixed uint Raw[GpuMaterial.Size / 4];
}

/// <summary>The lights as the frame constants carry them: one sun and a flat ambient, each xyz with w unused.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LightingConstants
{
    public Vector4 SunDirection;
    public Vector4 SunColor;
    public Vector4 Ambient;

    public static LightingConstants From(ReadOnlySpan<LightInstance> lights)
    {
        LightingConstants constants = new()
        {
            SunDirection = new Vector4(Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f)), 0f),
            SunColor = Vector4.Zero,
            Ambient = new Vector4(0.15f, 0.16f, 0.2f, 0f),
        };

        foreach (LightInstance light in lights)
        {
            if (light.Kind != LightKind.Directional)
                continue;

            constants.SunDirection = new Vector4(Vector3.Normalize(light.World.Forward), 0f);
            constants.SunColor = new Vector4(light.Color * light.Intensity, 0f);
            break;
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
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<DrawArgs>() == DrawArgs.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<FrameConstants>() == FrameConstants.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<PassConstants>() == PassConstants.Size);
    }
}
