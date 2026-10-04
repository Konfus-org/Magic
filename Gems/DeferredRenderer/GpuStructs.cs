using Magic.Contexts.Components;
using Magic.Contexts.Settings;
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
    NoShadow = 1u << 4,
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

    /// <summary>
    /// The constants of one shadow view, a cascade of the sun or a face of a local light, looking from <paramref name="eye"/>
    /// into its <paramref name="tile"/> of the atlas: positions are relative to the eye, <see cref="MinPixels"/> is in the
    /// tile's texels, and the LOD bias is the shadow views' own.
    /// </summary>
    public static FrameConstants ForShadowView(
        in Matrix4x4 viewProj,
        in Matrix4x4 rotation,
        Vector3 eye,
        Rectangle tile,
        in Matrix4x4 projection,
        float near,
        float far,
        bool isOrthographic,
        RenderSettings settings,
        long frame)
    {
        return new FrameConstants
        {
            ViewProj = viewProj,
            View = rotation,
            CameraPos = eye,
            Time = frame,
            ViewSize = new Vector2(tile.Width, tile.Height),
            ViewTexel = new Vector2(1f / Math.Max(1, tile.Width), 1f / Math.Max(1, tile.Height)),
            ViewOrigin = new Vector2(tile.X, tile.Y),
            ProjScale = new Vector2(projection.M11, projection.M22),
            Near = near,
            Far = far,
            MinPixels = settings.Shadows.MinTexels,
            IsOrthographic = isOrthographic ? 1u : 0u,
            LodBias = MathF.Max(0.01f, settings.LodBias * settings.Shadows.LodBias),
            SunDirection = Vector3.UnitY,
        };
    }
}

[InlineArray(Cascades.MaxCascades)]
internal struct CascadeMatrices
{
    private Matrix4x4 _element;
}

[InlineArray(ShadowCullConstants.MaxPlanes)]
internal struct ReceiverPlanes
{
    private Vector4 _element;
}

/// <summary>
/// 464 B. A shadow view's cull constants: its frame block, then the planes of the receivers' volume swept towards the
/// light (<c>Include/ShadowCull.hlsli</c>), relative to the view's eye, normals pointing in; none for a local light's face.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowCullConstants
{
    public const int MaxPlanes = Cascades.MaxReceiverPlanes;

    public const int Size = FrameConstants.Size + (MaxPlanes * 16) + 16;

    public FrameConstants Frame;
    public ReceiverPlanes Planes;
    public uint PlaneCount, Pad0, Pad1, Pad2;
}

[InlineArray(Cascades.MaxCascades)]
internal struct CascadeRows
{
    private Vector4 _element;
}

/// <summary>
/// 864 B. The lighting's constants: the frame block, then everything the shading reads beyond the camera's view, exactly
/// as <c>Include/Shade.hlsli</c> appends it: the sun's cascades, the GI clipmap, the ambient occlusion, the debug view.
/// Pushed to the shade dispatch and the ambient occlusion and GI passes; the light binning keeps the plain frame block.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadeConstants
{
    public const int Size = FrameConstants.Size + 608;

    public FrameConstants Frame;

    /// <summary>
    /// Camera-relative world to each cascade's clip space.
    /// </summary>
    public CascadeMatrices CascadeViewProj;

    /// <summary>
    /// Per cascade: X the view depth it ends at, Y metres per shadow texel, ZW its tile's uv origin in the atlas.
    /// </summary>
    public CascadeRows Cascade;

    /// <summary>
    /// X the cascade count, Y the blend fraction, Z the filter radius in metres, W the normal bias in texels.
    /// </summary>
    public Vector4 ShadowParams;

    /// <summary>
    /// XY a cascade tile's size in uv, ZW one atlas texel in uv.
    /// </summary>
    public Vector4 ShadowAtlasUv;

    public uint ShadowFlags, ShadowRecordCount;
    public float ShadowDepthBias, ShadowPad1;

    public CascadeRows ClipmapOrigin, ClipmapCameraOffset;
    public uint GiLevels, GiResolution, GiFlags, GiUpdateLevel;
    public Vector4 GiInteriorTint, SkyColor, GiShift;

    public Vector4 AoParams;
    public uint AoSlices, AoSteps, AoFlags;
    public float AoScale;

    public uint DebugView, ShadePad0, ShadePad1, ShadePad2;

    public const uint SunFlag = 1u;
    public const uint LocalFlag = 2u;
    public const uint AoEnabledFlag = 1u;
    public const uint AoMultiBounceFlag = 2u;
    public const uint GiEnabledFlag = 1u;

    /// <summary>
    /// A view's lighting constants: its frame block, the cascades fitted for it (none when <paramref name="cascades"/>
    /// is null or holds none) and the atlas as laid out this frame.
    /// </summary>
    public static ShadeConstants Build(in FrameConstants frame, ShadowBuffers? cascades, ShadowState shadows, RenderSettings settings)
    {
        ShadowSettings shadowSettings = settings.Shadows;
        AoSettings ao = settings.Ao;
        ShadeConstants constants = new()
        {
            Frame = frame,
            DebugView = (uint)settings.DebugView,
            ShadowRecordCount = (uint)shadows.RecordRows.Count,
            AoParams = new Vector4(ao.Radius, ao.MaxRadiusPixels, ao.Thickness, ao.Strength),
            AoSlices = (uint)ao.Slices,
            AoSteps = (uint)ao.Steps,
            AoFlags = (ao.Enabled ? AoEnabledFlag : 0u) | (ao.MultiBounce ? AoMultiBounceFlag : 0u),
            AoScale = AmbientOcclusion.Scale(ao),
        };

        if (!shadowSettings.Enabled || !shadows.Atlas.IsValid)
            return constants;

        float atlasWidth = shadows.AtlasWidth, atlasHeight = shadows.AtlasHeight;
        constants.ShadowAtlasUv = new Vector4(shadows.CascadeResolution / atlasWidth, shadows.CascadeResolution / atlasHeight, 1f / atlasWidth, 1f / atlasHeight);
        constants.ShadowFlags = shadows.RecordRows.Count > 0 ? LocalFlag : 0u;
        constants.ShadowDepthBias = shadowSettings.DepthBias;
        if (cascades is not { Count: > 0 })
            return constants;

        constants.ShadowFlags |= SunFlag;
        constants.ShadowParams = new Vector4(cascades.Count, shadowSettings.BlendFraction, shadowSettings.FilterRadius, shadowSettings.NormalBias);
        for (int i = 0; i < cascades.Count; i++)
        {
            Cascade cascade = cascades.Cascades[i];
            Rectangle tile = cascades.Tile(i, shadows.CascadeResolution);
            constants.CascadeViewProj[i] = cascade.ViewProj(frame.CameraPos);
            constants.Cascade[i] = new Vector4(cascade.FarSplit, cascade.TexelWorld, tile.X / atlasWidth, tile.Y / atlasHeight);
        }

        return constants;
    }
}

/// <summary>
/// 48 B. A draw group's mesh box in the mesh's own space and the mesh's occupancy brick, for the GI's stamping.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuGiGroup
{
    public const int Size = 48;

    public Vector4 BoxMin, BoxMax;
    public uint Brick, MeshSlot, Pad0, Pad1;
}

/// <summary>
/// 32 B. What a material gives the GI: its albedo and what it emits, linear.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuGiMaterial
{
    public const int Size = 32;

    public Vector4 Albedo, Emissive;
}

/// <summary>
/// Four unsigned words in a row.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UintVector4(uint X, uint Y, uint Z, uint W);

/// <summary>
/// 304 B. A brick job's constants: the frame block, the mesh's box, and its triangles' place in the mega buffers (<c>Gi/Brick.hlsli</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BrickConstants
{
    public const int Size = FrameConstants.Size + 48;

    public FrameConstants Frame;
    public Vector4 BoxMin, BoxMax;
    public UintVector4 Job;
}

/// <summary>
/// 96 B. One face of a local light's shadow, as the lighting reads it: where its page sits in the atlas, the camera-relative
/// view-projection into it, and in X how many metres a texel of it is per metre from the light.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuShadowRecord
{
    public const int Size = 96;

    public Vector4 RectUv;
    public Matrix4x4 ViewProj;
    public Vector4 Params;
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
/// 64 B. One point or spot light, in absolute world space. A point light is a spot whose cone never ends: its cosines
/// are below any a direction can have. <see cref="ShadowRecord"/> is the first <see cref="GpuShadowRecord"/> of its
/// shadow faces (<see cref="ShadowFaces"/> of them, 1 or 6), or <see cref="ShadowState.None"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    public const int Size = 64;

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

    public uint ShadowRecord, ShadowFaces, ShadowPad0, ShadowPad1;

    public static GpuLight From(in LightInstance light, uint shadowRecord, uint shadowFaces)
    {
        bool isSpot = light.Kind == LightKind.Spot;
        return new GpuLight
        {
            PositionRange = new Vector4(light.World.Translation, light.Range),
            ColorInnerCos = new Vector4(light.Color * light.Intensity, isSpot ? MathF.Cos(light.InnerAngle * 0.5f) : -1f),
            DirectionOuterCos = new Vector4(Vector3.Normalize(light.World.Forward), isSpot ? MathF.Cos(light.OuterAngle * 0.5f) : -2f),
            ShadowRecord = shadowRecord,
            ShadowFaces = shadowFaces,
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
    public bool SunCastsShadows;

    /// <summary>
    /// Whether a <see cref="Sky"/> entity set the ambient: the sky is then shown where nothing is drawn.
    /// </summary>
    public bool HasSky;

    /// <summary>
    /// The sky without a <see cref="Sky"/> entity: today's dim blue-grey.
    /// </summary>
    public static readonly Vector3 DefaultSky = new(0.15f, 0.16f, 0.2f);

    public static LightingConstants From(ReadOnlySpan<LightInstance> lights, Vector3? sky)
    {
        LightingConstants constants = new()
        {
            SunDirection = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f)),
            SunColor = Vector3.Zero,
            Ambient = sky ?? DefaultSky,
            HasSky = sky is not null,
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
            constants.SunCastsShadows = light.CastsShadows;
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
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuLight>() == GpuLight.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuShadowRecord>() == GpuShadowRecord.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<ShadeConstants>() == ShadeConstants.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<ShadowCullConstants>() == ShadowCullConstants.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuGiGroup>() == GpuGiGroup.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<GpuGiMaterial>() == GpuGiMaterial.Size);
        System.Diagnostics.Debug.Assert(Unsafe.SizeOf<BrickConstants>() == BrickConstants.Size);
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
